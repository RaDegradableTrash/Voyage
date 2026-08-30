# Voyage TerrainSystem

这是一个与旧任务、旧场景和旧 Terrain 流程隔离的 FBX 地形基础系统。

## 快速使用

1. 打开 `Tools/Voyage/Terrain System/FBX Tile Baker`。
2. 创建 `Chunk Settings`、`Source Descriptor` 和 `Tile Index`。
3. 将 FBX 模型 Prefab 拖入 `FBX / Prefab`。
4. 点击 `Analyze FBX`，确认源包围盒和 Mesh 统计。
5. 点击 `Generate All Tiles`。
6. 在独立场景中创建 `TerrainSystem Streaming`，把生成的 `TerrainTileIndex` 赋给 `TerrainStreamingManager`。
7. 同一个对象上的 `TerrainVehicleBootstrap` 会自动加载旧的 `PlayerCar` Prefab，使用 WASD/方向键驾驶，并由 MeshCollider 地块进行接触采样。

项目中的试用源文件为 `Assets/VoyageTerrain_Trial1.fbx`。Unity 完成导入后，可直接从 Project 窗口拖入 Baker；如果 FBX 尚未完成导入，先等待 Inspector 中的模型预览出现。

源 FBX 只通过 `sharedMesh` 读取，原始顶点、法线、UV、材质和索引不会被写回。派生 Mesh 位于 `GeneratedLOD`，Prefab 位于 `GeneratedTiles/Resources`。

## 当前边界

- `WildernessBuilder`、旧 `TerrainTile` 和旧任务系统保留但不被本系统调用。
- 当前未安装 Addressables，因此默认使用 Unity 原生 `Resources.LoadAsync`；后续可将索引中的 resourcePath 替换为 Addressables key。
- `ClipDerivedMesh` 目前保留为策略入口；默认推荐 `AssignToSingleTile`，需要跨边界重复时使用 `DuplicateToAdjacentTiles`。
- LOD3 作为远景低模/HLOD 替代，保证远处仍有地形轮廓。
- 加载半径只会从 Tile Index 中选择已有地块，不会为源 FBX 范围外凭空生成地形。若源模型只有 2×2 个 Tile，最多只能加载这 4 个；要看到更远区域，需要更大的 FBX、较小的 Tile Size，或另外生成远景代理。
- `ClipDerivedMesh` 使用派生 Mesh 裁剪跨边界三角形，并对法线和 UV 做插值；它不会写回源 Mesh。
- 派生 Mesh 会将边界水平坐标吸附到世界网格，并对边界高度按配置精度统一量化；LOD 保留边界顶点并可生成 Skirt。
- `Validate Generated Tiles` 会比较相邻 Tile 的 LOD0 边界顶点，报告共享边数量、失败数量和最大误差。
