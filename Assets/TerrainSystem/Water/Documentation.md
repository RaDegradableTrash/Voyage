# Voyage Water System

The water system is an independent first-phase module. It does not modify the source FBX or generated terrain meshes.

Open `Tools/Voyage/Water System/Water Control`, create the settings asset, tune the values, then click `Add Water System To Current Demo Object`. The runtime bootstrap creates tide, wave, streaming, underwater, vehicle interaction and debug components automatically.

The terrain runtime aligns the imported terrain's vertical bounds center to `Y = 0`. The sea-level setting is `Y = -600`; terrain below this level is submerged. The gameplay SeaLevelWaterSystem extends its radial mesh to 1000 km, with distant clip-depth projection so water reaches the horizon without increasing the terrain camera far clip. Other defaults use 256m square water tiles, a 5x5 preload area, near/mid/far procedural mesh resolutions, smooth tide motion and near-player trigger colliders.

Phase 1 intentionally defers boats, full fluid simulation, buoyancy and underwater ecology. Shoreline classification is represented by configurable depth bands and is ready for terrain sampling/foam expansion without coupling the baker to the water runtime.
