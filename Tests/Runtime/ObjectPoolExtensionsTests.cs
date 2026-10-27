using Dories.ObjectPool.Runtime;
using NUnit.Framework;
using UnityEngine.Pool;

namespace Dories.ObjectPool.Tests
{
    public class ObjectPoolExtensionsTests
    {
        private class Item { }

        [Test]
        public void Prewarm_CreatesInactiveItems()
        {
            int created = 0;
            var pool = new ObjectPool<Item>(() => { created++; return new Item(); });

            pool.Prewarm(5);

            Assert.AreEqual(5, created);
            Assert.AreEqual(5, pool.CountInactive);
            Assert.AreEqual(0, pool.CountActive);
        }

        [Test]
        public void Prewarm_WithExistingItems_OnlyCreatesMissing()
        {
            int created = 0;
            var pool = new ObjectPool<Item>(() => { created++; return new Item(); });
            pool.Prewarm(3);

            pool.Prewarm(5);

            Assert.AreEqual(5, created);
            Assert.AreEqual(5, pool.CountInactive);
        }

        [Test]
        public void Prewarm_RespectsMaxSize()
        {
            var pool = new ObjectPool<Item>(() => new Item(), maxSize: 2);

            pool.Prewarm(5);

            Assert.AreEqual(2, pool.CountInactive);
        }
    }
}
