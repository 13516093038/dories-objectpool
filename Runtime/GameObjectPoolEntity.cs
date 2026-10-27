using System.Collections.Generic;
using Dories.Logging;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;
#if DORIES_UNITASK_SUPPORT
using GameObjectTask = Cysharp.Threading.Tasks.UniTask<UnityEngine.GameObject>;
using PoolTask = Cysharp.Threading.Tasks.UniTask;
#else
using GameObjectTask = System.Threading.Tasks.Task<UnityEngine.GameObject>;
using PoolTask = System.Threading.Tasks.Task;
#endif

namespace Dories.ObjectPool.Runtime
{
    /// <summary>
    /// GameObject 预制体池，按（包名, 资源地址）管理，预制体通过 <see cref="PrefabProvider"/> 加载。
    /// 各接口的 packageName 可选，为空时由 PrefabProvider 决定（YooAsset 实现：Inspector 配置的包，再为空走默认包）。
    /// 闲置实例挂在未激活的根节点下，所以借出/回收即触发 OnEnable/OnDisable，可直接在里面重置状态。
    /// </summary>
    public class GameObjectPoolEntity : MonoBehaviour
    {
        private const string Tag = "ObjectPool";
        private const float IdleCheckInterval = 1f;

        [SerializeField, Min(1), Tooltip("每个预制体最多缓存的闲置实例数，超出的回收直接销毁")]
        private int _maxSize = 64;

        [SerializeField, Tooltip("池内无借出实例且闲置超过该秒数，销毁缓存并卸载预制体；<=0 不自动释放")]
        private float _idleReleaseSeconds = 60f;

        [SerializeField, Tooltip("场景卸载后释放所有无借出实例的池（KeepAlive 的除外）")]
        private bool _releaseUnusedOnSceneUnloaded = true;

        internal sealed class PrefabPool
        {
            public string Package;
            public string Location;
            public GameObject Prefab;
            public Transform Root;
            public ObjectPool<GameObject> Pool;
            public int ActiveCount;
            public float LastUseTime;
            public bool KeepAlive;
        }

        private readonly Dictionary<(string Package, string Location), PrefabPool> _pools = new();
        private readonly Dictionary<GameObject, PrefabPool> _spawned = new();
        private Transform _root;
        private float _nextIdleCheckTime;
        private bool _isQuitting;

        [SerializeReference, Tooltip("预制体加载来源；留空时有 YooAssetSystem 则自动用场景里的 AssetLoaderEntity")]
        private IPrefabProvider _prefabProvider;

        /// <summary>
        /// 预制体加载来源，可在 Inspector 选择，需在第一次 Spawn/Prewarm 前设置，之后不要更换。
        /// </summary>
        public IPrefabProvider PrefabProvider
        {
            get => _prefabProvider;
            set => _prefabProvider = value;
        }

        internal Dictionary<(string Package, string Location), PrefabPool> Pools => _pools;

        public bool HasPool(string key, string packageName = null) => _pools.ContainsKey(PoolKey(packageName, key));
        public int CountActive(string key, string packageName = null) => _pools.TryGetValue(PoolKey(packageName, key), out var pp) ? pp.ActiveCount : 0;
        public int CountInactive(string key, string packageName = null) => _pools.TryGetValue(PoolKey(packageName, key), out var pp) ? pp.Pool.CountInactive : 0;

        private static (string, string) PoolKey(string packageName, string location) => (packageName ?? string.Empty, location);

        private Transform Root
        {
            get
            {
                if (_root == null)
                {
                    var go = new GameObject("[PoolRoot]");
                    go.SetActive(false);
                    _root = go.transform;
                    _root.SetParent(transform, false);
                }
                return _root;
            }
        }

        #region Spawn / Despawn

        /// <summary>
        /// 借出实例，局部位姿与预制体一致。加载失败返回 null，加载异常向上抛出。
        /// </summary>
        public async GameObjectTask SpawnAsync(string key, Transform parent = null, string packageName = null)
        {
            if (await EnsurePoolAsync(packageName, key) == null || !_pools.TryGetValue(PoolKey(packageName, key), out var pp))
                return null;

            var prefabTransform = pp.Prefab.transform;
            return Spawn(pp, parent, prefabTransform.localPosition, prefabTransform.localRotation);
        }

        /// <summary>
        /// 借出实例并放到世界坐标 position/rotation。
        /// </summary>
        public async GameObjectTask SpawnAsync(string key, Vector3 position, Quaternion rotation, Transform parent = null, string packageName = null)
        {
            if (await EnsurePoolAsync(packageName, key) == null || !_pools.TryGetValue(PoolKey(packageName, key), out var pp))
                return null;

            // 先在未激活的根下摆好位姿再挂到 parent，避免 OnEnable / 拖尾在旧位置出现
            if (parent != null)
            {
                position = parent.InverseTransformPoint(position);
                rotation = Quaternion.Inverse(parent.rotation) * rotation;
            }
            return Spawn(pp, parent, position, rotation);
        }

        private GameObject Spawn(PrefabPool pp, Transform parent, Vector3 localPosition, Quaternion localRotation)
        {
            var go = pp.Pool.Get();
            var t = go.transform;
            t.SetLocalPositionAndRotation(localPosition, localRotation);
            t.localScale = pp.Prefab.transform.localScale;

            pp.ActiveCount++;
            pp.LastUseTime = Time.unscaledTime;
            _spawned.Add(go, pp);

            t.SetParent(parent, false);
            return go;
        }

        /// <summary>
        /// 回收实例。非本池借出或已回收的对象会被忽略并告警。
        /// </summary>
        public void Despawn(GameObject go)
        {
            if (go == null)
                return;

            if (!_spawned.Remove(go, out var pp))
            {
                Log.Warn(Tag, $"{go.name} 不是本池借出的或已回收，忽略本次回收", go);
                return;
            }

            pp.ActiveCount--;
            pp.LastUseTime = Time.unscaledTime;
            pp.Pool.Release(go);
        }

        /// <summary>
        /// 预热 key 对应的池到至少 count 个闲置实例。keepAlive 为 true 时该池不参与空闲释放与场景卸载释放。
        /// </summary>
        public async PoolTask PrewarmAsync(string key, int count, bool keepAlive = false, string packageName = null)
        {
            if (await EnsurePoolAsync(packageName, key) == null || !_pools.TryGetValue(PoolKey(packageName, key), out var pp))
                return;

            pp.KeepAlive |= keepAlive;
            pp.LastUseTime = Time.unscaledTime;
            pp.Pool.Prewarm(Mathf.Min(count, _maxSize));
        }

        #endregion

        #region Load / Release

        /// <summary>
        /// 确保池存在，返回预制体；失败返回 null。
        /// </summary>
        private async GameObjectTask EnsurePoolAsync(string packageName, string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                Log.Error(Tag, "Pool key is null or empty", this);
                return null;
            }

            var key = PoolKey(packageName, location);
            if (_pools.TryGetValue(key, out var existing))
                return existing.Prefab;

            if (PrefabProvider == null)
            {
                Log.Error(Tag, $"PrefabProvider 未设置，无法加载: {DisplayName(key)}", this);
                return null;
            }

            var prefab = await PrefabProvider.LoadAsync(packageName, location);

            // 加载期间池已被销毁或加载结果为空：退还这次拿到的引用
            if (this == null || prefab == null)
            {
                PrefabProvider.Unload(packageName, location);
                if (prefab == null)
                    Log.Error(Tag, $"预制体加载失败: {DisplayName(key)}");
                return null;
            }

            // 并发加载时别人先建好了池：退还多拿的一份引用
            if (_pools.TryGetValue(key, out existing))
            {
                PrefabProvider.Unload(packageName, location);
                return existing.Prefab;
            }

            _pools.Add(key, CreatePool(key, prefab));
            Log.Debug(Tag, $"Pool created: {DisplayName(key)}", this);
            return prefab;
        }

        internal static string DisplayName((string Package, string Location) key) =>
            string.IsNullOrEmpty(key.Package) ? key.Location : $"{key.Package}/{key.Location}";

        private PrefabPool CreatePool((string Package, string Location) key, GameObject prefab)
        {
            var root = new GameObject(DisplayName(key)).transform;
            root.SetParent(Root, false);

            return new PrefabPool
            {
                Package = key.Package,
                Location = key.Location,
                Prefab = prefab,
                Root = root,
                LastUseTime = Time.unscaledTime,
                Pool = new ObjectPool<GameObject>(
                    () => Instantiate(prefab, root),
                    actionOnRelease: go => go.transform.SetParent(root, false),
                    actionOnDestroy: DestroyObject,
                    // 防重复回收由 _spawned 负责，省掉 ObjectPool 的 O(n) 检查
                    collectionCheck: false,
                    maxSize: _maxSize)
            };
        }

        /// <summary>
        /// 销毁池并卸载预制体。仍借出的实例也会被销毁（预制体资源已卸载，留着会丢资源）。
        /// </summary>
        public void ReleasePool(string key, string packageName = null)
        {
            if (string.IsNullOrEmpty(key) || !_pools.Remove(PoolKey(packageName, key), out var pp))
                return;

            var actives = ListPool<GameObject>.Get();
            foreach (var pair in _spawned)
            {
                if (pair.Value == pp)
                    actives.Add(pair.Key);
            }
            foreach (var go in actives)
            {
                _spawned.Remove(go);
                if (go != null)
                    DestroyObject(go);
            }
            ListPool<GameObject>.Release(actives);

            pp.Pool.Clear();
            if (pp.Root != null)
                DestroyObject(pp.Root.gameObject);
            PrefabProvider?.Unload(pp.Package, pp.Location);
            Log.Debug(Tag, $"Pool released: {DisplayName((pp.Package, pp.Location))}", this);
        }

        /// <summary>
        /// 释放所有池。
        /// </summary>
        public void ReleaseAll() => ReleaseWhere(_ => true);

        /// <summary>
        /// 释放所有没有借出实例且未标记 KeepAlive 的池。
        /// </summary>
        public void ReleaseUnused()
        {
            PruneDestroyed();
            ReleaseWhere(pp => pp.ActiveCount == 0 && !pp.KeepAlive);
        }

        internal void ReleaseIdle(float now)
        {
            if (_idleReleaseSeconds <= 0f)
                return;

            PruneDestroyed();
            ReleaseWhere(pp => pp.ActiveCount == 0 && !pp.KeepAlive && now - pp.LastUseTime >= _idleReleaseSeconds);
        }

        private void ReleaseWhere(System.Predicate<PrefabPool> match)
        {
            var pools = ListPool<PrefabPool>.Get();
            foreach (var pp in _pools.Values)
            {
                if (match(pp))
                    pools.Add(pp);
            }
            foreach (var pp in pools)
                ReleasePool(pp.Location, pp.Package);
            ListPool<PrefabPool>.Release(pools);
        }

        /// <summary>
        /// 借出后被外部销毁（如随场景卸载）的实例不会再回收，从借出表里清掉。
        /// </summary>
        private void PruneDestroyed()
        {
            var dead = ListPool<GameObject>.Get();
            foreach (var pair in _spawned)
            {
                if (pair.Key == null)
                    dead.Add(pair.Key);
            }
            foreach (var go in dead)
            {
                if (_spawned.Remove(go, out var pp))
                {
                    pp.ActiveCount--;
                    pp.LastUseTime = Time.unscaledTime;
                }
            }
            ListPool<GameObject>.Release(dead);
        }

        private static void DestroyObject(GameObject go)
        {
            if (Application.isPlaying)
                Destroy(go);
            else
                DestroyImmediate(go);
        }

        #endregion

        #region Unity

        private void Awake()
        {
#if DORIES_YOOASSET_SUPPORT
            // 没配置时默认走 YooAsset，加载器在首次加载时从场景里找
            _prefabProvider ??= new YooAssetPrefabProvider();
#endif
        }

        private void OnEnable() => SceneManager.sceneUnloaded += OnSceneUnloaded;

        private void OnDisable() => SceneManager.sceneUnloaded -= OnSceneUnloaded;

        // 退出时 YooAsset 可能先于本组件销毁，再 Unload 会抛异常；进程结束资源自会回收
        private void OnApplicationQuit() => _isQuitting = true;

        private void OnDestroy()
        {
            if (!_isQuitting)
                ReleaseAll();
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            if (now < _nextIdleCheckTime)
                return;

            _nextIdleCheckTime = now + IdleCheckInterval;
            ReleaseIdle(now);
        }

        internal void OnSceneUnloaded(Scene scene)
        {
            if (_releaseUnusedOnSceneUnloaded)
                ReleaseUnused();
            else
                PruneDestroyed();
        }

        #endregion
    }
}
