# Interactive Grass

当前版本的草地交互使用受光的草材质，重点验证密度、风场、车辆轨迹、姿态恢复和流式生命周期。

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

`GrassInteractionSystem` 控制交互纹理分辨率、覆盖范围、衰减、轨迹插值预算和永久样本重建预算。`InteractiveGrassTile` 控制草丛间距、每丛叶片数量、丛半径、密度、株高、局部坡度阈值、分帧生成预算和地面 LayerMask。草 Shader 使用世界空间风场、主光源/环境光、阴影和临时/永久交互场。

草材质近景使用全不透明、远景使用 Alpha Blend：实例绘制关闭阴影投射和阴影接收；草片只保留一套索引，由双面 Shader 负责背面着色，避免重复光栅化。`grassFadeStart` 到 `grassFadeEnd`（默认 105～495m）之间按距离连续降低 Alpha，同时将颜色逐渐压向深绿色 `grassFadeColor`，远端由地形底色自然接管，不再使用造成硬边的距离抖动裁剪。近景统一为更明亮的黄绿色层次，不再使用棕红色草尖。风场沿全局风向形成连续麦浪；轮胎轨迹沿车辆行驶方向倒伏，并由临时场约 10 秒恢复。

草簇原型使用三组交叉宽叶片，并将叶片顶部沿面法线轻微外鼓，减少“针尖草”的感觉并形成更厚的团状轮廓；修改叶片几何后需要重新执行一次 Grass Prototype 烘焙，已生成的 Tile 不会自动重写。

可在 `Tools/Voyage/Terrain System/FBX Tile Baker` 中点击 `Rebuild Grass Prototype Only`，只更新共享草簇 Mesh 并保持现有 Tile Prefab 的资源引用，不需要重建所有地形 Chunk。

地形重新烘焙时，`TerrainChunkSettings.bakeGrass` 只生成一个共享的 `GrassPrototypeAsset`：它保存一次草丛原型 Mesh，所有 Tile 通过引用共享。Tile 不再保存 `GrassChunkAsset` 的完整位置数组，而是在进入近景 LOD 后根据 Tile 坐标和碰撞面分帧生成轻量 GPU 实例矩阵。运行时默认使用 `DrawMeshInstancedIndirect`：Compute Shader 按相机视锥、距离和 LOD 密度写入可见实例 AppendBuffer，GPU 生成间接绘制实例数；不支持 Compute Shader 或找不到 `GrassCulling.compute` 时自动回退到 `DrawMeshInstanced`（每批最多 1023 个草丛）。草丛预算由 `grassClusterBudget` 限制，避免大尺寸 Tile 因细间距生成过量几何体。

旧版本生成的 `*_Grass.asset` 属于一次性派生缓存。升级后请重新执行完整烘焙；新烘焙会删除旧的每 Tile 草资产并生成 `Source/GrassPrototype.asset`。GeneratedLOD 仍是可重建输出，不应作为长期数据源或提交到版本库。

永久车辙数据与临时草姿态分离：临时姿态由 `_VoyageGrassInteraction` 驱动并自动衰减，永久数据由 `GrassPermanentTrackStore` 保存，并通过 `_VoyageGrassPermanentInteraction` 预留给后续地表损伤表现。
