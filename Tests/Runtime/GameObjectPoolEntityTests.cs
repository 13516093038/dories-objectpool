using System.Collections.Generic;
using Dories.ObjectPool.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Cysharp.Threading.Tasks;

namespace Dories.ObjectPool.Tests
{
    public class GameObjectPoolEntityTests
    {
        private const string Key = "Prefabs/Bullet";

        private class FakePrefabProvider : IPrefabProvider
        {
            public GameObject Prefab;
            public int LoadCount;
            public int UnloadCount;
            public readonly List<string> LoadedPackages = new();
            public readonly List<string> UnloadedPackages = new();
            public readonly Queue<UniTask<GameObject>> Pending = new();

            public UniTask<GameObject> LoadAsync(string packageName, string location)
            {
                LoadCount++;
                LoadedPackages.Add(packageName);
                if (Pending.Count > 0)
                    return Pending.Dequeue();
                return UniTask.FromResult(Prefab);
            }

            public void Unload(string packageName, string location)
            {
                UnloadCount++;
                UnloadedPackages.Add(packageName);
            }
        }

        private GameObjectPoolEntity _entity;
        private FakePrefabProvider _provider;
        private GameObject _prefab;
        private GameObject _parent;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("Bullet");
            _prefab.transform.localScale = new Vector3(2f, 2f, 2f);
            _parent = new GameObject("Parent");
            _provider = new FakePrefabProvider { Prefab = _prefab };
            _entity = new GameObject("Pool").AddComponent<GameObjectPoolEntity>();
            _entity.PrefabProvider = _provider;
        }

        [TearDown]
        public void TearDown()
        {
            _entity.ReleaseAll();
            Object.DestroyImmediate(_entity.gameObject);
            Object.DestroyImmediate(_parent);
            Object.DestroyImmediate(_prefab);
        }

        private static T Run<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        private static void Run(UniTask task) => task.GetAwaiter().GetResult();

        private GameObject Spawn(Transform parent = null) => Run(_entity.SpawnAsync(Key, parent));

        [Test]
        public void Spawn_ParentsUnderTarget_AndIsActive()
        {
            var go = Spawn(_parent.transform);

            Assert.IsNotNull(go);
            Assert.AreSame(_parent.transform, go.transform.parent);
            Assert.IsTrue(go.activeInHierarchy);
            Assert.AreEqual(_prefab.transform.localScale, go.transform.localScale);
            Assert.AreEqual(1, _entity.CountActive(Key));
        }

        [Test]
        public void Spawn_WithWorldPose_PlacesInWorldSpace()
        {
            _parent.transform.position = new Vector3(10f, 0f, 0f);
            var go = Run(_entity.SpawnAsync(Key, new Vector3(1f, 2f, 3f), Quaternion.identity, _parent.transform));

            Assert.That(Vector3.Distance(new Vector3(1f, 2f, 3f), go.transform.position), Is.LessThan(1e-4f));
        }

        [Test]
        public void Despawn_ThenSpawn_ReusesInstance_AndParksInactive()
        {
            var first = Spawn(_parent.transform);
            _entity.Despawn(first);

            Assert.IsFalse(first.activeInHierarchy);
            Assert.AreEqual(1, _entity.CountInactive(Key));
            Assert.AreEqual(0, _entity.CountActive(Key));

            var second = Spawn();
            Assert.AreSame(first, second);
            Assert.AreEqual(1, _provider.LoadCount);
        }

        [Test]
        public void Despawn_Twice_IsIgnored()
        {
            var go = Spawn();
            _entity.Despawn(go);

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("已回收"));
            _entity.Despawn(go);

            Assert.AreEqual(1, _entity.CountInactive(Key));
            Assert.AreEqual(0, _entity.CountActive(Key));
        }

        [Test]
        public void Despawn_ForeignObject_IsIgnored()
        {
            Spawn();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("不是本池借出"));
            _entity.Despawn(_parent);

            Assert.IsNotNull(_parent);
            Assert.AreEqual(1, _entity.CountActive(Key));
        }

        [Test]
        public void Prewarm_CreatesInactiveInstances()
        {
            Run(_entity.PrewarmAsync(Key, 4));

            Assert.AreEqual(4, _entity.CountInactive(Key));
            Assert.AreEqual(0, _entity.CountActive(Key));
        }

        [Test]
        public void ReleaseIdle_ReleasesUnusedPool_AndUnloadsPrefab()
        {
            var go = Spawn();
            _entity.Despawn(go);

            _entity.ReleaseIdle(Time.unscaledTime + 1000f);

            Assert.IsFalse(_entity.HasPool(Key));
            Assert.IsTrue(go == null);
            Assert.AreEqual(1, _provider.UnloadCount);
        }

        [Test]
        public void ReleaseIdle_KeepsPoolWithActiveInstances()
        {
            Spawn();

            _entity.ReleaseIdle(Time.unscaledTime + 1000f);

            Assert.IsTrue(_entity.HasPool(Key));
            Assert.AreEqual(0, _provider.UnloadCount);
        }

        [Test]
        public void ReleaseIdle_SkipsKeepAlivePool()
        {
            Run(_entity.PrewarmAsync(Key, 2, keepAlive: true));

            _entity.ReleaseIdle(Time.unscaledTime + 1000f);
            _entity.ReleaseUnused();

            Assert.IsTrue(_entity.HasPool(Key));
        }

        [Test]
        public void ExternallyDestroyedInstance_IsPruned_OnSceneUnloaded()
        {
            var go = Spawn(_parent.transform);
            Object.DestroyImmediate(go);

            _entity.OnSceneUnloaded(default);

            Assert.IsFalse(_entity.HasPool(Key));
            Assert.AreEqual(1, _provider.UnloadCount);
        }

        [Test]
        public void ReleasePool_DestroysActiveInstances()
        {
            var go = Spawn(_parent.transform);

            _entity.ReleasePool(Key);

            Assert.IsTrue(go == null);
            Assert.AreEqual(0, _entity.CountActive(Key));
            Assert.AreEqual(1, _provider.UnloadCount);
        }

        [Test]
        public void Spawn_WithoutProvider_ReturnsNull()
        {
            _entity.PrefabProvider = null;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("PrefabProvider"));

            Assert.IsNull(Spawn());
        }

        [Test]
        public void SameKeyInDifferentPackages_UsesSeparatePools()
        {
            var a = Spawn();
            var b = Run(_entity.SpawnAsync(Key, packageName: "DLC1"));

            Assert.AreNotSame(a, b);
            Assert.AreEqual(1, _entity.CountActive(Key));
            Assert.AreEqual(1, _entity.CountActive(Key, "DLC1"));
            CollectionAssert.AreEqual(new string[] { null, "DLC1" }, _provider.LoadedPackages);

            _entity.Despawn(b);
            _entity.ReleasePool(Key, "DLC1");

            Assert.IsFalse(_entity.HasPool(Key, "DLC1"));
            Assert.IsTrue(_entity.HasPool(Key));
            CollectionAssert.AreEqual(new[] { "DLC1" }, _provider.UnloadedPackages);
        }

        [Test]
        public void ConcurrentSpawn_LoadsOnce_AndReturnsExtraReference()
        {
            var first = new UniTaskCompletionSource<GameObject>();
            var second = new UniTaskCompletionSource<GameObject>();
            _provider.Pending.Enqueue(first.Task);
            _provider.Pending.Enqueue(second.Task);

            var a = _entity.SpawnAsync(Key);
            var b = _entity.SpawnAsync(Key);
            first.TrySetResult(_prefab);
            second.TrySetResult(_prefab);

            Assert.AreNotSame(Run(a), Run(b));
            Assert.AreEqual(2, _provider.LoadCount);
            Assert.AreEqual(1, _provider.UnloadCount);
            Assert.AreEqual(2, _entity.CountActive(Key));
        }
    }
}
