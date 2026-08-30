# 旧系统暂停说明

本系统没有修改或删除现有 `Assets/Scripts`、`Assets/Resources/Prefabs`、`Assets/Scenes/SampleScene.unity` 和 `_Recovery` 资源。

旧的 `WildernessBuilder`、旧 `TerrainTile`、车辆跟随器、任务逻辑和旧 HUD 不会被新 TerrainSystem 自动调用。恢复旧系统时，只需继续使用原来的 SampleScene/GameRoot 流程；使用新系统时，使用 `TerrainSystemDemo.unity` 或单独业务场景。

