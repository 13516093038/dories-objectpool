#if DORIES_YOOASSET_SUPPORT
using System;
using Dories.YooassetSystem.Runtime.AssetLoader;
using UnityEngine;
#if DORIES_UNITASK_SUPPORT
using GameObjectTask = Cysharp.Threading.Tasks.UniTask<UnityEngine.GameObject>;
#else
using GameObjectTask = System.Threading.Tasks.Task<UnityEngine.GameObject>;
#endif

namespace Dories.ObjectPool.Runtime
{
    /// <summary>
    /// 通过 <see cref="AssetLoaderEntity"/> 加载预制体。loader 为空时首次加载从场景里找。
    /// </summary>
    [Serializable]
    public class YooAssetPrefabProvider : IPrefabProvider
    {
        [SerializeField] private AssetLoaderEntity _loader;
        [SerializeField, Tooltip("调用未传包名时使用；留空走 AssetLoaderEntity 的默认包")] private string _packageName;

        public YooAssetPrefabProvider() { }

        public YooAssetPrefabProvider(AssetLoaderEntity loader, string packageName = null)
        {
            _loader = loader;
            _packageName = packageName;
        }

        private AssetLoaderEntity Loader
        {
            get
            {
                if (_loader == null)
                    _loader = UnityEngine.Object.FindObjectOfType<AssetLoaderEntity>();
                if (_loader == null)
                    throw new InvalidOperationException("AssetLoaderEntity not found");
                return _loader;
            }
        }

        public GameObjectTask LoadAsync(string packageName, string location)
        {
            var package = ResolvePackage(packageName);
            return string.IsNullOrEmpty(package)
                ? Loader.LoadAssetAsync<GameObject>(location)
                : Loader.LoadAssetAsync<GameObject>(package, location);
        }

        public void Unload(string packageName, string location)
        {
            if (_loader == null)
                return;

            var package = ResolvePackage(packageName);
            if (string.IsNullOrEmpty(package))
                _loader.UnloadAsset(location);
            else
                _loader.UnloadAsset(package, location);
        }

        // 调用方传的包名优先，其次 Inspector 配置的包名，都为空走 AssetLoaderEntity 默认包
        private string ResolvePackage(string packageName) => string.IsNullOrEmpty(packageName) ? _packageName : packageName;
    }
}
#endif
