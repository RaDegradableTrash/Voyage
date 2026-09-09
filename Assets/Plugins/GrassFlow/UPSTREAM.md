# GrassFlow integration for Voyage

Free, MIT-licensed fork of [Mithzzx/Project-GrassFlow](https://github.com/Mithzzx/Project-GrassFlow), pinned to commit `dd876ce7d5c92ae256bc8e6b4fd1e9e550d02b23`. Copyright and permission text are retained in LICENSE.txt.

The compute generation/culling and packed instance format originate from upstream `Grass/Assets/Shaders/GrassCompute.compute` and `GrassShader.shader`. The renderer, stylized tuft meshes and replacement color-gradient shader are customized for Voyage's streamed mesh terrain. The upstream demo scene and Terrain-only painter are not imported.

Local changes:

- Mesh-height/coverage textures instead of a Unity Terrain dependency; no runtime placement raycasts.
- Persistent, per-tile density assets, a Scene view brush, Undo/Redo, and explicit saving.
- Bounded candidate counts, three meadow LODs (44/15/5 narrow blades), plus the preserved Aloe / 芦荟 preset (9/5/3 folded leaves), a 90 m rendering cap, distance thinning, opaque rendering and no grass shadow pass submissions.
- Buffers owned per renderer and camera, released on disable/unload. No synchronous GPU counter reads in normal rendering.
- Disabled upstream Hi-Z path because camera depth can be stale or belong to another camera; frustum/distance culling remains enabled.
- Density is a probability, not just a binary threshold. Invalid mesh coverage and zero-height blades are rejected.
- Shared grass/ground lighting and shadow reception, fog and near-camera per-blade ground/contact sampling, including stationary pressure and recovery from the interaction field.
- Golden meadow is the default: broad density variation, travelling wind waves and narrow tapered ribbons. The previous wide-leaf look is explicitly named Aloe / 芦荟 and remains selectable.

No package-manager download or paid license is required. Runtime files are in `Assets/Plugins/GrassFlow`; the mesh-specific editor adapter is in `Assets/TerrainSystem/Editor/GrassFlowPainterWindow.cs` so it can reference Voyage's terrain assembly.

