#if DORIES_UNITASK_SUPPORT
using GameObjectTask = Cysharp.Threading.Tasks.UniTask<UnityEngine.GameObject>;
#else
using GameObjectTask = System.Threading.Tasks.Task<UnityEngine.GameObject>;
#endif

namespace Dories.ObjectPool.Runtime
{
    /// <summary>
    /// 预制体加载来源。每次成功的 LoadAsync 对应一次 Unload（引用计数语义）。
    /// packageName 为空表示由实现决定用哪个包。
    /// </summary>
    public interface IPrefabProvider
    {
        GameObjectTask LoadAsync(string packageName, string location);
        void Unload(string packageName, string location);
    }
}
