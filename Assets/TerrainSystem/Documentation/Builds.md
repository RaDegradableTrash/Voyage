# Windows builds

Use **Tools > Voyage > Build > Windows prealpha v0.0.4**. The normal Unity Build
window also prepares the terrain cache automatically. For batch builds, use
`-executeMethod VoyageBuild.BuildRelease`.

The complete 9,792-tile world lives in `GeneratedTiles/RuntimeTiles`, outside
`Resources`. The tile GUIDs, meshes, collisions and LODs are unchanged.
The grass painter loads editable prefabs directly. Standalone players load
individual prefabs asynchronously from the local terrain AssetBundle. In the
Editor, Play Mode reuses that bundle only when its fingerprint is current. If the
cache is missing or stale, Play Mode loads prefabs from the project as terrain
streams in instead of synchronously building all 9,792 tiles. This avoids a long
freeze when pressing Play; player builds still regenerate stale packages.

Editor cache validation runs incrementally on editor updates with a 2 ms work
budget between dependency queries. A valid result is reused across Play Mode
domain reloads. Asset imports, build-target/graphics changes, or package changes
invalidate that result. If Play starts while validation is pending, the initial
terrain request yields until it finishes; no package is built automatically.
Current caches use async bundle reads, while missing/stale caches still load
current project assets and can exhibit first-visit loading stalls. Refresh the
terrain package through the normal Voyage build command to regain async reads.

With a current cache, driving avoids synchronous AssetDatabase.LoadAssetAtPath
calls. A Windows Editor capture on 2026-09-10 measured one such call at 9.67 ms
immediately before a 14.32 ms frame interval on first entering a tile ring. In
the 45-second route comparison (W input, vehicle physics enabled), frames within
10 m of a 256 m tile boundary after the first 10 seconds improved from 14.32 to
7.88 ms maximum and from 12.49 to 6.81 ms at the 99th percentile. Frames over
10 ms fell from 62/1153 to 0/1162. This is an Editor route measurement, not a
guarantee for all hardware or player builds; separate GC and initial grass
preparation spikes remain. A stale-cache Editor session uses per-tile asset
loads until the next player build refreshes the package. Captures are stored
locally in Logs/boundary-capture-cold.csv and Logs/boundary-capture-bundle.csv.

A subsequent 180-second physical drive covered 7.43 km and 34 cell transitions
(44,640 samples; Logs/boundary-capture-bundle-long.csv). Boundary-frame p99 was
6.72 / 6.57 / 6.83 ms for 10–60 / 60–120 / 120–180 seconds respectively.
Loaded tiles peaked at 121, pending loads at 12 and pending unloads at 12;
the end-of-run queues were empty. The last interval included a 24.18 ms frame
with an 8.24 ms GC.Collect marker and no terrain loading, activation or
destruction work. Thus synchronous first-visit terrain stalls are removed in
this test, but occasional GC stalls are still present. A repeated Play entry
validated all 9,792 cached tiles in 0.30 seconds without rebuilding the bundle.

GrassFlow roots use the streamed LOD0 mesh's triangle planes, indexed in a small
XZ grid. Density maps still control painting, but filtered height maps no longer
determine streamed grass placement. Each meadow leaf samples its own root at
every draw distance. Lighting uses the mesh's interpolated vertex normals;
placement uses its geometric plane. The renderer owns and releases these GPU
buffers. Terrain LOD hysteresis retains LOD0 throughout the renderer's actual
90 m draw limit so simplified geometry cannot move away from the roots.

On 2026-09-10, GPU projection checks covered 14,076 points on six actual terrain
meshes, including triangle edges and transformed meshes at distant coordinates.
Maximum height error was below 1 mm; Direct3D11 and Direct3D12 checks passed.
The final implementation also passed vertex-normal and LOD hysteresis checks.
Standalone URP draws at 20/64/80 m compiled and rendered successfully. These
isolated checks do not establish full driving-scene frame times. Local evidence
is in Logs/grass-ground-gpu-validation.txt and Logs/grass-ground-render-timing.txt;
regression tests are in GrassGroundProjectionTests.cs.

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

For a physical driving capture, launch a development player at 1920x1080 with
`-voyage-profile <label> -screen-width 1920 -screen-height 1080 -screen-fullscreen 0`.
The opt-in probe warms up, holds the accelerator using a virtual input device,
then coasts. It records frame times, speed, position and profiler counters under
`Profile/` beside the executable, and exits. Track writes go to a separate test
file. For the Editor, put a label in `Library/VoyageDriveProfile.txt` before Play;
the request is consumed once and the camera renders to a 1920x1080 target.
The probe submits explicit URP requests so hidden windows still render. Labels
containing `default` retain the configured camera pitch; `highres` uses 2880x1508.
Other labels use a 15-degree pitch to include sky/water. Screenshots are saved
with the CSV to verify the workload. Offscreen timings do not test window
presentation/VSync, and Editor input focus can prevent virtual throttle: verify
acceleration as well as displacement before calling a run a driving comparison.
The probe leaves normal launches untouched. Check speed and position in the CSV
before interpreting the result: a stuck vehicle is not a successful driving test.

Permanent wheel tracks serialize and write an isolated snapshot on a background
worker. Only one writer runs per store. New samples stay dirty until their own
save succeeds; disabling, pausing or quitting flushes the newest history. The
`Voyage.GrassTracks.Save` profiler marker measures the main-thread snapshot work;
`Serialize` and `Write` normally run on the worker.
Permanent recording is off by default; this optimization does not explain
hitches in runs where no track writes occur.

After the vehicle spawns, DrivingCore selects the low background-loading priority
to reduce the per-frame main-thread integration budget for asynchronous assets.
It restores the prior priority when destroyed. This is a scheduling budget,
not a hard limit on individual mesh/texture integration work.

In the command console, `/chunk border` toggles nearby chunk boundaries;
`/chunk border on` and `off` set the state explicitly. Yellow marks the current
cell, blue the surrounding cells. Up/Down browse the last 100 submitted commands
in the current session and restore unfinished input when returning to the end.
