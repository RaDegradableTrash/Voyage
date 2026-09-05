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

源 FBX 只通过 `sharedMesh` 读取，原始顶点、法线、UV、材质和索引不会被写回。派生地形 Mesh 位于 `GeneratedLOD`，Prefab 位于 `GeneratedTiles/Resources`。草地原型只写入 `Source/GrassPrototype.asset` 并由所有 Tile 共享，Tile 草实例在运行时按需生成，不再写入每 Tile 的 `*_Grass.asset`。

## 当前边界

- `WildernessBuilder`、旧 `TerrainTile` 和旧任务系统保留但不被本系统调用。
- 当前未安装 Addressables，因此默认使用 Unity 原生 `Resources.LoadAsync`；后续可将索引中的 resourcePath 替换为 Addressables key。
- 地形网格允许跨 Tile 被裁剪，因为它是静态空间数据而不是一个带库存/血量/网络状态的逻辑实体；当前 Baker 实际使用 `ClipDerivedMesh`，并配合边界吸附、边界高度量化和裙边防止裂缝。
- 建筑、生产设施、电力网络等逻辑对象仍应采用单一 Owner Tile；跨 Tile 的可见范围通过包围盒/加载范围扩展解决，不应把一个逻辑实体拆成多份存档对象。
- LOD3 作为远景低模/HLOD 替代，保证远处仍有地形轮廓。
- 加载半径只会从 Tile Index 中选择已有地块，不会为源 FBX 范围外凭空生成地形。若源模型只有 2×2 个 Tile，最多只能加载这 4 个；要看到更远区域，需要更大的 FBX、较小的 Tile Size，或另外生成远景代理。
- `ClipDerivedMesh` 使用派生 Mesh 裁剪跨边界三角形，并对法线和 UV 做插值；它不会写回源 Mesh。
- 派生 Mesh 会将边界水平坐标吸附到世界网格，并对边界高度按配置精度统一量化；LOD 保留边界顶点并可生成 Skirt。
- `Validate Generated Tiles` 会比较相邻 Tile 的 LOD0 边界顶点，报告共享边数量、失败数量和最大误差。

### Chunk ownership rule

地形的几何三角形可以在 Tile 边界裁剪，前提是相邻 Tile 使用同一套世界坐标边界和高度精度；碰撞只使用 Tile 自己的 LOD0，不把一整座山作为单个运行时对象加载。草实例也由所属 Tile 管理，但共享一个 Grass Prototype，不切割单个草簇。建筑或其他有持久逻辑状态的对象则只序列化一次，由中心/根节点决定 Owner Tile，渲染包围盒可以覆盖相邻 Tile。
