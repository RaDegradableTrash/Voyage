# Interactive Grass

当前版本的草地交互不包含上色，草片使用白色材质，重点验证姿态、轨迹和流式生命周期。

## 自动接入

- `DrivingCore` 会创建或复用 `GrassInteractionSystem`。
- 地形 Chunk 初始化时会自动挂载 `InteractiveGrassTile`。
- 玩家车辆生成后，系统会注册其所有子物体中的 `WheelCollider`。
- 交互场使用世界坐标，默认覆盖 160m、512×512 像素，并随玩家移动分块滚动。

## 手动验证

1. 进入包含 `DrivingCore` 的游戏场景并开始运行。
2. 驾驶车辆经过有地形碰撞体的草地；草片应按车轮移动方向弯曲。
3. 停车后观察草片逐渐恢复，并有轻微阻尼回弹。
4. 快速驾驶穿过地块边界，确认轨迹连续，没有明显虚线或整块草同步倾斜。
5. 远离车辆的地块应关闭高质量交互；回到附近后自动恢复。
6. 重新加载场景后，永久轨迹数据从 `Application.persistentDataPath` 的 `voyage-grass-tracks.json` 读取。

## 运行时参数

`GrassInteractionSystem` 控制交互纹理分辨率、覆盖范围、衰减、轨迹插值预算和永久样本重建预算。`InteractiveGrassTile` 控制草丛间距、每丛叶片数量、丛半径、密度、株高、局部坡度阈值、分帧生成预算和地面 LayerMask。

地形重新烘焙时，`TerrainChunkSettings.bakeGrass` 会为每个 Tile 生成一个 `GrassChunkAsset`：资产保存草丛原型 Mesh、位置、旋转和缩放。运行时通过 GPU instancing 分批绘制（每批最多 1023 个草丛），不创建逐根草对象。预烘焙资产优先于运行时射线采样；旧资源或未开启烘焙时才使用分帧运行时回退。草丛预算由 `grassClusterBudget` 限制，避免大尺寸 Tile 因细间距生成过量几何体。

永久车辙数据与临时草姿态分离：临时姿态由 `_VoyageGrassInteraction` 驱动并自动衰减，永久数据由 `GrassPermanentTrackStore` 保存，并通过 `_VoyageGrassPermanentInteraction` 预留给后续地表损伤表现。
