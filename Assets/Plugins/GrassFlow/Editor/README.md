The mesh-terrain painter lives in `Assets/TerrainSystem/Editor/GrassFlowPainterWindow.cs`.
Unity compiles editor scripts beneath Plugins in the first pass, before Voyage's terrain types are available. Keeping the adapter outside Plugins allows it to use the project's terrain index and stream saved GrassFlow patches.
