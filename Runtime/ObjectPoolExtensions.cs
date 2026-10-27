using UnityEngine.Pool;

namespace Dories.ObjectPool.Runtime
{
    /// <summary>
    /// 纯 C# 对象池直接用 <see cref="UnityEngine.Pool.ObjectPool{T}"/>（回调、maxSize、collectionCheck 都已内置），这里只补预热。
    /// </summary>
    public static class ObjectPoolExtensions
    {
        /// <summary>
        /// 预热到池内至少有 <paramref name="count"/> 个闲置对象（受池的 maxSize 限制）。会触发 actionOnGet / actionOnRelease。
        /// </summary>
        public static void Prewarm<T>(this IObjectPool<T> pool, int count) where T : class
        {
            var items = ListPool<T>.Get();
            // Get 会先取走已有闲置对象，所以要整取 count 个再全部放回
            for (int i = 0; i < count; i++)
                items.Add(pool.Get());
            foreach (var item in items)
                pool.Release(item);
            ListPool<T>.Release(items);
        }
    }
}
