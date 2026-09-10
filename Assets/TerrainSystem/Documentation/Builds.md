# Windows builds

Use **Tools > Voyage > Build > Windows prealpha v0.0.4**. The normal Unity Build
window also prepares the terrain cache automatically. For batch builds, use
`-executeMethod VoyageBuild.BuildRelease`.

The complete 9,792-tile world lives in `GeneratedTiles/RuntimeTiles`, outside
`Resources`. The tile GUIDs, meshes, collisions and LODs are unchanged.
The grass painter loads editable prefabs directly. Both Play Mode driving and
standalone players load individual prefabs asynchronously from the local terrain
AssetBundle. Scenes containing DrivingCore validate the package before entering
Play Mode; a stale or missing cache is rebuilt before driving starts. A failed
package build cancels entry into Play Mode rather than using stale terrain.

This removes synchronous AssetDatabase.LoadAssetAtPath calls from driving. A
Windows Editor capture on 2026-09-10 measured one such call at 9.67 ms immediately
before a 14.32 ms frame interval on first entering a tile ring. In the 45-second
route comparison (W input, vehicle physics enabled), frames within 10 m of a
256 m tile boundary after the first 10 seconds improved from 14.32 to 7.88 ms
maximum and from 12.49 to 6.81 ms at the 99th percentile. Frames over 10 ms fell
from 62/1153 to 0/1162. This is an Editor route measurement, not a guarantee for
all hardware or player builds; separate GC and initial grass preparation spikes
remain. Captures are stored locally in Logs/boundary-capture-cold.csv and
Logs/boundary-capture-bundle.csv.

A subsequent 180-second physical drive covered 7.43 km and 34 cell transitions
(44,640 samples; Logs/boundary-capture-bundle-long.csv). Boundary-frame p99 was
6.72 / 6.57 / 6.83 ms for 10–60 / 60–120 / 120–180 seconds respectively.
Loaded tiles peaked at 121, pending loads at 12 and pending unloads at 12;
the end-of-run queues were empty. The last interval included a 24.18 ms frame
with an 8.24 ms GC.Collect marker and no terrain loading, activation or
destruction work. Thus synchronous first-visit terrain stalls are removed in
this test, but occasional GC stalls are still present. A repeated Play entry
validated all 9,792 cached tiles in 0.30 seconds without rebuilding the bundle.

Before building the player, the build command checks prefab dependency hashes,
Unity version, target platform and graphics settings. It rebuilds the LZ4 terrain
package only when those inputs change. The reusable package and fingerprint live
in `Library/VoyageTerrainBuildCache/<target>`. Interrupted package builds are not
marked as reusable. Deleting Library also deletes this cache.

The package is copied into `Voyage_Data/StreamingAssets/VoyageTerrain` during the
player build, without adding generated binaries to Assets or triggering another
asset import. Always distribute the entire output folder, including StreamingAssets.
No download or external service is required. Changing terrain still incurs a
terrain-package build; changing unrelated gameplay code can reuse it.

`Logs/prealpha-v0.0.4-build.json` records the player build steps and the total build
command duration including cache preparation. A cold project import happens before
the build command and must not be confused with an incremental build measurement.

For a smoke test of the actual player, launch `Voyage.exe -voyage-validate`.
It verifies package coverage, vehicle spawn, terrain collision, nearby grass and
streaming to a distant tile, writes `Validation/result.txt` and screenshots, then
exits. Normal launches do not create or run this probe.
