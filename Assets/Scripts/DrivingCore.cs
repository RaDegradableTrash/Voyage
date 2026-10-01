using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Profiling;
using Voyage.TerrainSystem;

/// <summary>Only the vehicle, terrain, camera and pause loop.</summary>
public sealed class DrivingCore : MonoBehaviour
{
    // Orientation used by the RV1.0 instance in Cementery/Main_Persistent.
    // The original input sign and WheelCollider spin direction are authored
    // for this prefab orientation.
    static readonly Quaternion ReferenceVehicleRotation = Quaternion.Euler(0f, -152.683f, 0f);
    public static DrivingCore Instance { get; private set; }
    public PlayerCar Player { get; private set; }
    public Transform ControlledTarget => Voyage.Exploration.ExplorationSession.Instance != null && Voyage.Exploration.ExplorationSession.Instance.OnFoot
        ? Voyage.Exploration.ExplorationSession.Instance.Explorer.transform : Player != null ? Player.transform : null;
    public bool HudPaused { get; private set; }
    public float HudTargetDistance { get { return 0f; } }
    public string HudObjective { get { return "DRIVE"; } }
    public string HudMode { get { return "4X4"; } }
    public string HudStatus { get { return string.Empty; } }
    public bool HudStatusVisible { get { return false; } }
    public float HudSpeedKmh
    {
        get
        {
            if (Player == null) return 0f;
            CarControl car = Player.GetComponent<CarControl>();
            return car != null ? car.CurrentSpeedKmh : Player.speedKmh;
        }
    }

    FollowCamera cameraFollow;
    float resetCooldown;
    bool headlightsOn;
    Material vehicleBody;
    Material vehicleGlass;
    Material vehicleTail;
    Material vehicleHead;
    TerrainTileIndex terrainIndex;
    GrassInteractionSystem grassInteraction;
    readonly Dictionary<Vector2Int, GameObject> loadedTerrainTiles = new Dictionary<Vector2Int, GameObject>();
    readonly Queue<TerrainTileRecord> pendingTerrainLoads = new Queue<TerrainTileRecord>();
    // Forward lookahead tiles must not wait behind the rest of the preload ring.
    // Keeping a separate queue avoids reordering or rebuilding the normal queue
    // while the vehicle is moving through a cell.
    readonly Queue<TerrainTileRecord> pendingPriorityTerrainLoads = new Queue<TerrainTileRecord>();
    readonly HashSet<Vector2Int> pendingTerrainCoordinates = new HashSet<Vector2Int>();
    readonly Queue<GameObject> pendingTerrainUnloads = new Queue<GameObject>();
    Coroutine terrainLoadRoutine;
    bool terrainLoadRoutineRunning;
    Vector2Int streamedCenter;
    bool hasStreamedCenter;
    Vector2Int prefetchedCenter;
    bool hasPrefetchedCenter;
    int terrainWorkFrame = -1;
    ThreadPriority previousLoadingPriority;
    bool loadingBudgetApplied;
    static readonly ProfilerMarker InstantiateTileMarker = new ProfilerMarker("Voyage.Terrain.Instantiate");
    static readonly ProfilerMarker UnloadTileMarker = new ProfilerMarker("Voyage.Terrain.Unload");
    static readonly ProfilerMarker ActivateCollisionMarker = new ProfilerMarker("Voyage.Terrain.ActivateCollision");
    static readonly ProfilerMarker InitializeGrassMarker = new ProfilerMarker("Voyage.Terrain.InitializeGrass");
    static readonly ProfilerMarker StreamUpdateMarker = new ProfilerMarker("Voyage.Terrain.StreamUpdate");
    static readonly ProfilerMarker LodCollisionScanMarker = new ProfilerMarker("Voyage.Terrain.LodCollisionScan");

    bool TryBeginTerrainWork()
    {
        if (terrainWorkFrame == Time.frameCount) return false;
        terrainWorkFrame = Time.frameCount;
        return true;
    }

    void Awake()
    {
        Instance = this;
        grassInteraction = GetComponent<GrassInteractionSystem>();
        if (grassInteraction == null) grassInteraction = GrassInteractionSystem.Instance;
        if (grassInteraction == null) grassInteraction = gameObject.AddComponent<GrassInteractionSystem>();
        if (GetComponent<VoyageCommandConsole>() == null) gameObject.AddComponent<VoyageCommandConsole>();
        if (GetComponent<VoyageHUD>() == null) gameObject.AddComponent<VoyageHUD>();
    }

    void Start()
    {
        CreateVehicleMaterials();
        StartCoroutine(StartDriving());
    }

    IEnumerator StartDriving()
    {
        yield return StartCoroutine(LoadFbxTerrain());
        yield return null;
        RaycastHit ground;
        Vector3 spawnBase = new Vector3(-24f, 1000f, -24f);
        float y = Physics.Raycast(spawnBase, Vector3.down, out ground, 2000f)
            ? ground.point.y + 3.2f : 3.2f;
        // Use the original Cementery vehicle hierarchy. It contains the real
        // six WheelColliders and the reference chassis instead of a generated
        // four-wheel approximation.
        GameObject carObject = null;
        yield return StartCoroutine(PrefabRuntime.SpawnAsync("RV1.0", "PLAYER VEHICLE",
            new Vector3(-24f, y, -24f), ReferenceVehicleRotation, value => carObject = value));
        if (carObject == null) yield break;
        Player = carObject.GetComponent<PlayerCar>();
        if (Player == null) Player = carObject.AddComponent<PlayerCar>();
        ReferenceVehicleRuntimeBinder binder = carObject.GetComponent<ReferenceVehicleRuntimeBinder>();
        if (binder == null) binder = carObject.AddComponent<ReferenceVehicleRuntimeBinder>();
        binder.Bind();
        // Binder creates CarControl first so PlayerCar's legacy physics
        // initializer cannot overwrite RV1.0's 25000kg chassis settings.
        Player.EnsureVehiclePhysics();
        grassInteraction.SetTarget(Player.transform);
        grassInteraction.RegisterVehicle(Player.gameObject);
        Camera camera = Camera.main;
        if (camera != null)
        {
            cameraFollow = camera.GetComponent<FollowCamera>();
            if (cameraFollow == null) cameraFollow = camera.gameObject.AddComponent<FollowCamera>();
            cameraFollow.SetTarget(Player.transform);
        }
        var exploration = GetComponent<Voyage.Exploration.ExplorationSession>();
        if (exploration == null) exploration = gameObject.AddComponent<Voyage.Exploration.ExplorationSession>();
        exploration.Initialize(Player, cameraFollow);
        // Async loading still integrates objects on the main thread. Use Unity's
        // small gameplay budget after the spawn-critical assets are ready.
        previousLoadingPriority = Application.backgroundLoadingPriority;
        Application.backgroundLoadingPriority = ThreadPriority.Low;
        loadingBudgetApplied = true;
    }

    void Update()
    {
        UpdateTerrainStreaming();
        if (Player == null || VoyageCommandConsole.IsOpen || VoyageCommandConsole.ConsumedInputThisFrame) return;
        if (ReadKeyDown(KeyCode.P) || ReadKeyDown(KeyCode.Escape))
        {
            HudPaused = !HudPaused;
            Time.timeScale = HudPaused ? 0f : 1f;
        }
        if (HudPaused) return;
        if (Voyage.Exploration.ExplorationSession.Instance != null && Voyage.Exploration.ExplorationSession.Instance.OnFoot) return;
        if (ReadKeyDown(KeyCode.H))
        {
            headlightsOn = !headlightsOn;
            SetHeadlights(headlightsOn);
        }
        if (ReadKeyDown(KeyCode.R) && resetCooldown <= 0f) ResetVehicle();
        resetCooldown -= Time.deltaTime;
    }

    void ResetVehicle()
    {
        RaycastHit ground;
        float y = Physics.Raycast(new Vector3(-24f, 1000f, -24f), Vector3.down, out ground, 2000f)
            ? ground.point.y + 3.2f : 3.2f;
        Player.ResetCar(Player.transform.position);
        resetCooldown = 0.25f;
    }

    IEnumerator LoadFbxTerrain()
    {
        terrainIndex = Resources.Load<TerrainTileIndex>("TerrainSystem/TerrainTileIndex");
        if (terrainIndex == null || terrainIndex.tiles == null || terrainIndex.tiles.Count == 0)
        {
            Debug.LogError("FBX TERRAIN // TerrainTileIndex is missing or empty");
            yield break;
        }
        Vector3 spawnPoint = new Vector3(-24f, 0f, -24f);
        terrainIndex.RebuildLookup();
        // A small, collider-free LOD3 backdrop survives the local streaming
        // radius. Fog can hide its fill without erasing mountain silhouettes.
        var horizonRequest = Resources.LoadAsync<GameObject>("TerrainSystem/Horizon/TerrainHorizon");
        yield return horizonRequest;
        if (horizonRequest.asset != null)
        {
            Vector3 horizonPosition = terrainIndex.source != null
                ? terrainIndex.source.GetRuntimePosition(Vector3.zero)
                : Vector3.zero;
            Instantiate((GameObject)horizonRequest.asset, horizonPosition, Quaternion.identity, transform);
        }
        StreamTerrain(spawnPoint, true);
        // Do not spawn into an empty tile. The center tile is enough to place
        // the vehicle safely; the surrounding visual/preload ring continues
        // through the budgeted streaming coroutine after control is handed to
        // the player.
        float startupDeadline = Time.realtimeSinceStartup + 20f;
        while (!AreVisibleTerrainTilesReady(spawnPoint) && terrainLoadRoutineRunning &&
               Time.realtimeSinceStartup < startupDeadline)
            yield return null;
        if (!AreVisibleTerrainTilesReady(spawnPoint))
            Debug.LogError("FBX TERRAIN // center tile did not become ready before startup timeout; continuing vehicle startup.");
        Physics.SyncTransforms();
        Debug.Log("FBX TERRAIN // center tile ready; continuing background load (" + loadedTerrainTiles.Count + " tiles)");
        yield return null;
    }

    bool AreVisibleTerrainTilesReady(Vector3 position)
    {
        if (terrainIndex == null || terrainIndex.settings == null) return false;
        TerrainChunkSettings settings = terrainIndex.settings;
        Vector2Int center = settings.WorldToTile(position);
        bool found = false;
        for (int i = 0; i < terrainIndex.tiles.Count; i++)
        {
            TerrainTileRecord record = terrainIndex.tiles[i];
            if (record == null || record.coordinate != center)
                continue;
            found = true;
            GameObject tileObject;
            if (!loadedTerrainTiles.TryGetValue(record.coordinate, out tileObject) || tileObject == null)
                return false;
            // Grass GPU buffers are visual follow-up work. They must not delay
            // vehicle spawn once the center terrain object itself exists.
        }
        return found;
    }

    void UpdateTerrainStreaming()
    {
        using var streamUpdateScope = StreamUpdateMarker.Auto();
        if (terrainIndex == null || terrainIndex.settings == null) return;
        // LoadFbxTerrain owns the initial center-tile request. Avoid queuing the
        // surrounding preload ring from Update before the car has spawned; that
        // competes with the center tile needed to finish startup.
        Transform target = ControlledTarget;
        if (target == null) return;
        Vector3 position = target.position;
        float visualDistance = terrainIndex.settings.GetVisualDistance();
        Shader.SetGlobalVector("_VoyageTerrainView", new Vector4(position.x, position.z,
            Mathf.Max(0f, visualDistance - terrainIndex.settings.tileSize), visualDistance));
        StreamTerrain(position, false);
    }

    void StreamTerrain(Vector3 position, bool force)
    {
        if (terrainIndex == null || terrainIndex.settings == null) return;
        TerrainChunkSettings settings = terrainIndex.settings;
        Vector2Int center = settings.WorldToTile(position);
        // LOD selection is distance-based and must continue while the player
        // moves inside the same streaming cell. Only tile load/unload work is
        // gated by the cell change below.
        if (!force && hasStreamedCenter && center == streamedCenter)
        {
            // Begin the next ring while the vehicle is still inside the
            // current tile. The previous threshold was larger than half a
            // tile, so it could never be reached for a 256m tile and the
            // queue only started after the boundary crossing.
            float localX = Mathf.Repeat(position.x - settings.worldOrigin.x, settings.tileSize);
            float localZ = Mathf.Repeat(position.z - settings.worldOrigin.z, settings.tileSize);
            float edge = Mathf.Min(Mathf.Min(localX, settings.tileSize - localX),
                                   Mathf.Min(localZ, settings.tileSize - localZ));
            if (edge <= settings.tileSize * 0.5f &&
                (!hasPrefetchedCenter || prefetchedCenter != center))
            {
                Vector3 direction = GetTravelDirection();
                Vector2Int lookAhead = center;
                if (direction.sqrMagnitude > 0.001f)
                {
                    if (Mathf.Abs(direction.x) >= Mathf.Abs(direction.z))
                        lookAhead.x += direction.x >= 0f ? 1 : -1;
                    else
                        lookAhead.y += direction.z >= 0f ? 1 : -1;
                }
                QueueTerrainCandidates(position, settings, lookAhead, true);
                prefetchedCenter = center;
                hasPrefetchedCenter = true;
                if (!terrainLoadRoutineRunning && (pendingPriorityTerrainLoads.Count > 0 || pendingTerrainLoads.Count > 0))
                    StartTerrainLoadRoutine(settings);
            }
            if (hasPrefetchedCenter && prefetchedCenter == center)
            {
                UpdateLoadedTerrainLods(position, settings, center);
                return;
            }
        }
        else if (!hasStreamedCenter || center != streamedCenter)
        {
            prefetchedCenter = center;
            hasPrefetchedCenter = false;
        }
        streamedCenter = center;
        hasStreamedCenter = true;

        int unloadRadius = Mathf.Max(settings.GetPreloadRadius() + 1, settings.unloadRadius);
        // The initial Play Mode load needs only its spawn tile. Its preload
        // neighborhood is requested after the vehicle exists and can provide
        // a meaningful forward direction.
        QueueTerrainCandidates(position, settings, center, false, force && Player == null);
        UpdateLoadedTerrainLods(position, settings, center);

        List<Vector2Int> stale = new List<Vector2Int>();
        foreach (KeyValuePair<Vector2Int, GameObject> pair in loadedTerrainTiles)
        {
            int distance = Mathf.Max(Mathf.Abs(pair.Key.x - center.x), Mathf.Abs(pair.Key.y - center.y));
            if (distance > unloadRadius) stale.Add(pair.Key);
        }
        for (int i = 0; i < stale.Count; i++)
        {
            GameObject tile = loadedTerrainTiles[stale[i]];
            if (tile != null) pendingTerrainUnloads.Enqueue(tile);
            loadedTerrainTiles.Remove(stale[i]);
        }
        if (!terrainLoadRoutineRunning && (pendingPriorityTerrainLoads.Count > 0 || pendingTerrainLoads.Count > 0 || pendingTerrainUnloads.Count > 0))
            StartTerrainLoadRoutine(settings);
    }

    void StartTerrainLoadRoutine(TerrainChunkSettings settings)
    {
        terrainLoadRoutineRunning = true;
        terrainLoadRoutine = StartCoroutine(ProcessTerrainLoads(settings));
    }

    Vector3 GetTravelDirection()
    {
        if (Voyage.Exploration.ExplorationSession.Instance != null && Voyage.Exploration.ExplorationSession.Instance.OnFoot)
            return Vector3.ProjectOnPlane(Voyage.Exploration.ExplorationSession.Instance.Explorer.Velocity, Vector3.up).normalized;
        Vector3 direction = Vector3.zero;
        if (Player != null)
        {
            Rigidbody vehicleBody = Player.GetComponent<Rigidbody>();
            if (vehicleBody != null) direction = vehicleBody.linearVelocity;
            if (direction.sqrMagnitude < 4f)
            {
                CarControl car = Player.GetComponent<CarControl>();
                direction = car != null ? car.DriveForward : Player.transform.right;
            }
        }
        direction.y = 0f;
        return direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.zero;
    }

    void QueueTerrainCandidates(Vector3 position, TerrainChunkSettings settings, Vector2Int center, bool priority, bool centerOnly = false)
    {
        // The square preload radius is only an indexing convenience. Reject
        // corners outside the actual view circle before they enter the queue.
        int loadRadius = settings.GetPreloadRadius();
        float preloadDistance = settings.GetVisualDistance() + settings.tileSize;
        float preloadDistanceSq = preloadDistance * preloadDistance;
        List<TerrainTileRecord> candidates = new List<TerrainTileRecord>();
        for (int y = center.y - loadRadius; y <= center.y + loadRadius; y++)
        for (int x = center.x - loadRadius; x <= center.x + loadRadius; x++)
        {
            TerrainTileRecord record;
            if (!terrainIndex.TryGet(new Vector2Int(x, y), out record) || record == null ||
                loadedTerrainTiles.ContainsKey(record.coordinate) ||
                pendingTerrainCoordinates.Contains(record.coordinate)) continue;
            if (centerOnly && record.coordinate != center) continue;
            if (record.bounds.SqrDistance(position) > preloadDistanceSq) continue;
            candidates.Add(record);
        }
        Vector3 travelDirection = GetTravelDirection();
        candidates.Sort((a, b) =>
        {
            float da = (a.bounds.center - position).sqrMagnitude;
            float db = (b.bounds.center - position).sqrMagnitude;
            if (settings.prioritizeForward && travelDirection.sqrMagnitude > 0.001f)
            {
                float aheadA = Vector3.Dot(a.bounds.center - position, travelDirection);
                float aheadB = Vector3.Dot(b.bounds.center - position, travelDirection);
                da += Mathf.Max(0f, -aheadA) * settings.tileSize * 2f;
                db += Mathf.Max(0f, -aheadB) * settings.tileSize * 2f;
            }
            return da.CompareTo(db);
        });
        for (int i = 0; i < candidates.Count; i++)
        {
            if (priority) pendingPriorityTerrainLoads.Enqueue(candidates[i]);
            else pendingTerrainLoads.Enqueue(candidates[i]);
            pendingTerrainCoordinates.Add(candidates[i].coordinate);
        }
    }

    IEnumerator ProcessTerrainLoads(TerrainChunkSettings settings)
    {
        try
        {
        // Spread IO, initialization and destruction across frames instead
        // of blocking the camera's frame when crossing a streaming cell.
        yield return null;
        while (pendingPriorityTerrainLoads.Count > 0 || pendingTerrainLoads.Count > 0 || pendingTerrainUnloads.Count > 0)
        {
            // Forward terrain and its colliders must win the streaming budget.
            // Destroying an old tile first can trigger MeshCollider broadphase
            // work exactly at a boundary and make the vehicle hitch.
            // A continuously moving vehicle can keep the load queues non-empty
            // indefinitely. Do not let stale tiles accumulate until the queue
            // drains: retire one old tile every few frames while preserving a
            // strict priority for forward terrain.
            bool unloadSlot = (Time.frameCount & 3) == 0;
            if (pendingTerrainUnloads.Count > 0 &&
                ((pendingPriorityTerrainLoads.Count == 0 && pendingTerrainLoads.Count == 0) || unloadSlot))
            {
                while (!TryBeginTerrainWork()) yield return null;
                using (UnloadTileMarker.Auto())
                    Destroy(pendingTerrainUnloads.Dequeue());
                yield return null;
            }
            if (pendingPriorityTerrainLoads.Count == 0 && pendingTerrainLoads.Count == 0) continue;
            TerrainTileRecord record = pendingPriorityTerrainLoads.Count > 0
                ? pendingPriorityTerrainLoads.Dequeue()
                : pendingTerrainLoads.Dequeue();
            // Clear this as soon as the record leaves its queue so an
            // aborted async operation can be retried on the next stream pass.
            pendingTerrainCoordinates.Remove(record.coordinate);
            Vector3 viewer = ControlledTarget != null ? ControlledTarget.position : new Vector3(-24f, 0f, -24f);
            Vector2Int currentCenter = settings.WorldToTile(viewer);
            int loadRadius = settings.GetPreloadRadius();
            int distance = Mathf.Max(Mathf.Abs(record.coordinate.x - currentCenter.x), Mathf.Abs(record.coordinate.y - currentCenter.y));
            if (distance <= loadRadius && !loadedTerrainTiles.ContainsKey(record.coordinate))
            {
                GameObject prefab = null;
                yield return TerrainPrefabStore.LoadAsync(record.resourcePath, value => prefab = value);
                // Recheck after IO: the player may already be in another cell.
                viewer = ControlledTarget != null ? ControlledTarget.position : new Vector3(-24f, 0f, -24f);
                currentCenter = settings.WorldToTile(viewer);
                distance = Mathf.Max(Mathf.Abs(record.coordinate.x - currentCenter.x), Mathf.Abs(record.coordinate.y - currentCenter.y));
                if (prefab != null && distance <= loadRadius && !loadedTerrainTiles.ContainsKey(record.coordinate))
                {
                    // Unity's synchronous Instantiate still performs prefab
                    // deserialization, hierarchy creation and Awake on the
                    // streaming frame. InstantiateAsync moves the expensive
                    // serialization work off the main thread; only the
                    // completed object integration remains in the budgeted
                    // section below.
                    Vector3 tilePosition = terrainIndex.source != null
                        ? terrainIndex.source.GetRuntimePosition(record.bounds.center)
                        : record.bounds.center;
                    AsyncInstantiateOperation<GameObject> instantiate =
                        Object.InstantiateAsync(prefab, tilePosition, Quaternion.identity);
                    // Awake and hierarchy integration can still touch the
                    // main thread. Keep that work below a small per-frame
                    // budget so crossing a cell cannot consume a whole
                    // render frame.
                    // Keep hierarchy integration below a millisecond so a
                    // prefetched tile cannot consume the whole frame while
                    // the vehicle is still moving at speed.
                    AsyncInstantiateOperation.SetIntegrationTimeMS(0.75f);
                    yield return instantiate;
                    while (!TryBeginTerrainWork()) yield return null;
                    if (instantiate.isDone && instantiate.Result != null && instantiate.Result.Length > 0)
                    {
                        using (InstantiateTileMarker.Auto())
                        {
                            GameObject tileObject = instantiate.Result[0];
                            tileObject.name = "FBX TERRAIN BLOCK " + record.coordinate;
                            TerrainTileRuntime tile = tileObject.GetComponent<TerrainTileRuntime>();
                            if (tile != null)
                            {
                                tile.SetCollisionEnabled(false);
                                tile.Initialize(record, settings, false, viewer);
                                // WheelCollider vehicles can cross a tile seam in
                                // a single physics step.  Do not leave a newly
                                // integrated tile visible but non-colliding until
                                // the throttled LOD scan reaches it: that creates
                                // a brief drop/recontact impulse at the seam.
                                if (tile.WantsCollision(viewer, settings))
                                    tile.SetCollisionEnabled(true);
                            }
                            loadedTerrainTiles.Add(record.coordinate, tileObject);
                        }
                    }
                }
            }
            pendingTerrainCoordinates.Remove(record.coordinate);
            yield return null;
        }
        }
        finally
        {
            // A failed async load/instantiate must never leave startup waiting
            // forever on a Coroutine handle that has already stopped.
            terrainLoadRoutine = null;
            terrainLoadRoutineRunning = false;
        }
    }

    void OnDestroy()
    {
        if (loadingBudgetApplied && Application.backgroundLoadingPriority == ThreadPriority.Low)
            Application.backgroundLoadingPriority = previousLoadingPriority;
        Shader.SetGlobalVector("_VoyageTerrainView", Vector4.zero);
        foreach (GameObject tile in loadedTerrainTiles.Values)
            if (tile != null) Destroy(tile);
        while (pendingTerrainUnloads.Count > 0) Destroy(pendingTerrainUnloads.Dequeue());
        if (Instance == this) Instance = null;
    }

    void UpdateLoadedTerrainLods(Vector3 position, TerrainChunkSettings settings, Vector2Int center)
    {
        using var scanScope = LodCollisionScanMarker.Auto();
        // LOD transitions do not need render-frame precision, so update them
        // every other frame. Collision readiness is scanned every frame so
        // a tile arriving at the boundary cannot wait for frame parity.
        bool updateVisualLods = (Time.frameCount & 1) == 0;
        TerrainTileRuntime nextActivation = null;
        TerrainTileRuntime nextGrass = null;
        float activationDistance = float.MaxValue;
        float grassDistance = float.MaxValue;
        // GrassFlow patch loading creates GPU buffers and uploads density
        // textures. Do not do that for every preloaded terrain tile: the
        // player cannot see grass beyond the authored fade band anyway.
        float grassPreparationDistance = Mathf.Min(settings.grassFadeEnd + 32f, 180f);
        float grassPreparationDistanceSq = grassPreparationDistance * grassPreparationDistance;
        // GrassFlow patch assignment can upload a sizeable GPU buffer. Never
        // schedule that upload while terrain IO/instantiation is still queued;
        // doing both on a streaming boundary creates a visible catch-up hitch
        // that feels like the vehicle moved backwards and forwards.
        bool allowGrassPreparation = pendingPriorityTerrainLoads.Count == 0 && pendingTerrainLoads.Count == 0 && pendingTerrainUnloads.Count == 0;
        foreach (KeyValuePair<Vector2Int, GameObject> pair in loadedTerrainTiles)
        {
            TerrainTileRuntime tile = pair.Value == null ? null : pair.Value.GetComponent<TerrainTileRuntime>();
            if (tile == null) continue;
            if (updateVisualLods) tile.UpdateVisualLod(position);
            float distance = tile.Bounds.SqrDistance(position);
            bool collisionWanted = tile.WantsCollision(position, settings);
            if (collisionWanted && !tile.CollisionEnabled && distance < activationDistance)
            {
                nextActivation = tile;
                activationDistance = distance;
            }
            if (allowGrassPreparation && collisionWanted && tile.NeedsGrassInitialization &&
                distance <= grassPreparationDistanceSq && distance < grassDistance)
            {
                nextGrass = tile;
                grassDistance = distance;
            }
        }
        // One expensive action across the loader and activation loop per frame.
        // Near collision/grass takes priority over distant activation.
        if (nextActivation != null && activationDistance <= grassDistance)
        {
            if (TryBeginTerrainWork())
                using (ActivateCollisionMarker.Auto()) nextActivation.SetCollisionEnabled(true);
        }
        else if (nextGrass != null)
        {
            if (TryBeginTerrainWork())
                using (InitializeGrassMarker.Auto()) nextGrass.InitializeGrass();
        }
        // Never disable an already-cooked static collider while it remains
        // loaded. Removing it from PhysX broadphase at the collision radius
        // creates the same periodic boundary spike as enabling a new tile;
        // the unload queue removes the object once it is genuinely distant.
    }

    bool ReadKeyDown(KeyCode key)
    {
        if (Input.GetKeyDown(key)) return true;
        if (Keyboard.current == null) return false;
        if (key == KeyCode.P) return Keyboard.current.pKey.wasPressedThisFrame;
        if (key == KeyCode.R) return Keyboard.current.rKey.wasPressedThisFrame;
        return key == KeyCode.Escape && Keyboard.current.escapeKey.wasPressedThisFrame;
    }

    void CreateVehicleMaterials()
    {
        vehicleBody = Material("Vehicle Body", new Color(0.03f, 0.34f, 0.8f));
        vehicleGlass = Material("Vehicle Glass", new Color(0.08f, 0.2f, 0.28f));
        vehicleTail = Material("Vehicle Tail", new Color(0.8f, 0.04f, 0.02f));
        vehicleHead = Material("Vehicle Headlights", new Color(0.9f, 0.95f, 0.8f));
    }

    Material Material(string name, Color color)
    {
        Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.name = name;
        material.color = color;
        // Procedural fallback materials must remain readable when the key
        // light is fully shadowed. This is still a Lit material, so realtime
        // shadows continue to darken it instead of flattening the lighting.
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_EmissionColor"))
        {
            material.SetColor("_EmissionColor", color * 0.32f);
            material.EnableKeyword("_EMISSION");
        }
        return material;
    }

    public void SetHeadlights(bool enabled) { if (Player != null) Player.SetHeadlights(enabled); }
    public void ShakeGameplayCamera(float strength) { if (cameraFollow != null) cameraFollow.Shake(strength); }
}
