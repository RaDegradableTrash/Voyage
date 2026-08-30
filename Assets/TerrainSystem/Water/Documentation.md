# Voyage Water System

The water system is an independent first-phase module. It does not modify the source FBX or generated terrain meshes.

Open `Tools/Voyage/Water System/Water Control`, create the settings asset, tune the values, then click `Add Water System To Current Demo Object`. The runtime bootstrap creates tide, wave, streaming, underwater, vehicle interaction and debug components automatically.

Defaults use a fixed global sea level of `Y = 0`, 256m square water tiles, a 5x5 preload area, near/mid/far procedural mesh resolutions, smooth tide motion and near-player trigger colliders. Terrain below sea level remains the original terrain mesh; the water surface is an independent overlay.

Phase 1 intentionally defers boats, full fluid simulation, buoyancy and underwater ecology. Shoreline classification is represented by configurable depth bands and is ready for terrain sampling/foam expansion without coupling the baker to the water runtime.
