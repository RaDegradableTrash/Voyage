# 参数说明

- Tile Size：推荐 256m；必须与地图坐标体系一致。
- Boundary Overlap：默认 0m，保证派生块严格按方形边界裁切；需要 LOD stitching 时可配置为 1～2m。
- LOD 距离：默认 150 / 400 / 1000 / 3000m，可在 Chunk Settings 中调整。
- Loaded Radius：完整地块和碰撞范围，默认 1。
- Preload Radius：提前加载范围，默认 2。
- Unload Radius：卸载范围，必须大于 Preload Radius，默认 3。
- Streaming Cell Size：为后续建筑、植被和交互对象预留，默认 512m。
- Boundary Position Tolerance：边界位置检查阈值，默认 0.0001m。
- Boundary Height Precision：边界高度统一量化精度，默认 0.0001m。
- Generate Skirts / Skirt Depth：是否生成裙边以及裙边深度，默认开启、2m。
- Max Neighbor LOD Difference：相邻区块允许的最大 LOD 差异，默认 1。

注意：`Loaded Radius = 1` 表示 3×3 候选窗口，不代表索引一定有 9 个地块。Overlay 会同时显示候选数、完整加载数和 LOD/HLOD 加载数。
## View-driven terrain streaming

Terrain visuals are selected from the active camera, not only from the player's cell. `visualDistanceOverride = 0` uses the camera far clip plane; the demo camera uses 5000m. Tiles inside the camera frustum and this range are loaded as visual terrain, while only the configured near radius enables collision. Far tiles use LOD3/HLOD and upgrade to LOD0 automatically when they enter the camera's near range.

`visualTileMargin` adds a small streaming buffer around the frustum. `collisionRadius` is independent from visual LOD and keeps LOD0 collision near the vehicle.
