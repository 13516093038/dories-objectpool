# Dories ObjectPool

## 纯 C# 对象

直接用 `UnityEngine.Pool.ObjectPool<T>`（回调、`maxSize`、`collectionCheck` 内置），本包只补 `Prewarm`：

```csharp
var pool = new ObjectPool<Damage>(() => new Damage(), actionOnRelease: d => d.Reset(), maxSize: 256);
pool.Prewarm(32);
var d = pool.Get();
pool.Release(d);
```

## GameObject 预制体

```csharp
var pool = gameObject.AddComponent<GameObjectPoolEntity>();
// Inspector 的 Prefab Provider 下拉可选实现并填参数（YooAsset：拖入 AssetLoaderEntity、填包名）；
// 留空且装了 YooAssetSystem 时自动用场景里的 AssetLoaderEntity；也可代码指定
pool.PrefabProvider = new YooAssetPrefabProvider(assetLoaderEntity, "DefaultPackage");

await pool.PrewarmAsync("Prefabs/Bullet", 20, keepAlive: true);
var go = await pool.SpawnAsync("Prefabs/Bullet", pos, rot, parent);
pool.Despawn(go);

// 其它包：可选传 packageName，同一地址在不同包里是不同的池
var boss = await pool.SpawnAsync("Boss", pos, rot, packageName: "DLC1");
pool.ReleasePool("Boss", "DLC1");
```

- 闲置实例挂在未激活的 `[PoolRoot]` 下，借出/回收即触发 `OnEnable`/`OnDisable`，在里面重置状态。
- 重复回收、回收非本池对象：告警并忽略。
- 无借出实例且闲置超过 `_idleReleaseSeconds` 的池自动销毁并 `Unload` 预制体；`keepAlive` 的池除外。
- 场景卸载后：清理随场景销毁的借出实例，并（默认）释放无借出的池。
- `ReleasePool(key)` / `ReleaseAll()` 会连同仍借出的实例一起销毁（预制体已卸载）。
- Inspector：Play Mode 下显示各池的借出数、闲置数、空闲时长，可单独 Release 或 Release Unused。
- 自定义加载来源：实现 `IPrefabProvider`，标 `[Serializable]` 并保留无参构造，就会出现在 Inspector 下拉里。
