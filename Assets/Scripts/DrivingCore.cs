using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Jobs;
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
    public bool HudPaused { get; private set; }
    public float HudTargetDistance { get { return 0f; } }
    public string HudObjective { get { return "DRIVE"; } }
    public string HudMode { get { return "4X4"; } }
    public string HudStatus { get { return string.Empty; } }
    public bool HudStatusVisible { get { return false; } }
    public float HudSpeedKmh { get { return Player == null ? 0f : Player.speedKmh; } }

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
    readonly HashSet<Vector2Int> pendingTerrainCoordinates = new HashSet<Vector2Int>();
    readonly Queue<GameObject> pendingTerrainUnloads = new Queue<GameObject>();
    Coroutine terrainLoadRoutine;
    Vector2Int streamedCenter;
    bool hasStreamedCenter;
    int terrainWorkFrame = -1;
    JobHandle collisionBakeJob;
    bool collisionBakePending;
    static readonly ProfilerMarker InstantiateTileMarker = new ProfilerMarker("Voyage.Terrain.Instantiate");
    static readonly ProfilerMarker ActivateCollisionMarker = new ProfilerMarker("Voyage.Terrain.ActivateCollision");
    static readonly ProfilerMarker InitializeGrassMarker = new ProfilerMarker("Voyage.Terrain.InitializeGrass");

    struct CollisionBakeJob : IJob
    {
        public EntityId meshId;
        public void Execute() => Physics.BakeMesh(meshId, false, TerrainTileRuntime.CollisionCookingOptions);
    }

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
        GameObject carObject = PrefabRuntime.Spawn("RV1.0", "PLAYER VEHICLE", new Vector3(-24f, y, -24f), ReferenceVehicleRotation);
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
    }

    void Update()
    {
        UpdateTerrainStreaming();
        if (Player == null) return;
        if (ReadKeyDown(KeyCode.P) || ReadKeyDown(KeyCode.Escape))
        {
            HudPaused = !HudPaused;
            Time.timeScale = HudPaused ? 0f : 1f;
        }
        if (HudPaused) return;
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
        StreamTerrain(spawnPoint, true);
        // Do not spawn a controllable vehicle into an empty streaming bubble.
        // The visible radius must be ready before the player can outrun it.
        while (!AreVisibleTerrainTilesReady(spawnPoint) &&
               (pendingTerrainLoads.Count > 0 || terrainLoadRoutine != null))
            yield return null;
        Physics.SyncTransforms();
        Debug.Log("FBX TERRAIN // loaded " + loadedTerrainTiles.Count + " nearby modeled blocks");
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
            if (record == null || Mathf.Max(Mathf.Abs(record.coordinate.x - center.x),
                                            Mathf.Abs(record.coordinate.y - center.y)) > settings.loadedRadius)
                continue;
            found = true;
            GameObject tileObject;
            if (!loadedTerrainTiles.TryGetValue(record.coordinate, out tileObject) || tileObject == null)
                return false;
            TerrainTileRuntime tile = tileObject.GetComponent<TerrainTileRuntime>();
            if (tile != null && !tile.GrassBuildFinished) return false;
        }
        return found;
    }

    void UpdateTerrainStreaming()
    {
        if (terrainIndex == null || terrainIndex.settings == null) return;
        Vector3 position = Player != null ? Player.transform.position : new Vector3(-24f, 0f, -24f);
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
            UpdateLoadedTerrainLods(position, settings, center);
            return;
        }
        streamedCenter = center;
        hasStreamedCenter = true;

        int loadRadius = settings.GetPreloadRadius();
        int unloadRadius = Mathf.Max(loadRadius + 1, settings.unloadRadius);
        List<TerrainTileRecord> candidates = new List<TerrainTileRecord>();
        for (int y = center.y - loadRadius; y <= center.y + loadRadius; y++)
        for (int x = center.x - loadRadius; x <= center.x + loadRadius; x++)
        {
            TerrainTileRecord record;
            if (!terrainIndex.TryGet(new Vector2Int(x, y), out record) || record == null ||
                loadedTerrainTiles.ContainsKey(record.coordinate) ||
                pendingTerrainCoordinates.Contains(record.coordinate)) continue;
            candidates.Add(record);
        }
        candidates.Sort((a, b) =>
        {
            float da = (a.bounds.center - position).sqrMagnitude;
            float db = (b.bounds.center - position).sqrMagnitude;
            return da.CompareTo(db);
        });
        for (int i = 0; i < candidates.Count; i++)
        {
            pendingTerrainLoads.Enqueue(candidates[i]);
            pendingTerrainCoordinates.Add(candidates[i].coordinate);
        }
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
        if (terrainLoadRoutine == null && (pendingTerrainLoads.Count > 0 || pendingTerrainUnloads.Count > 0))
            terrainLoadRoutine = StartCoroutine(ProcessTerrainLoads(settings));
    }

    IEnumerator ProcessTerrainLoads(TerrainChunkSettings settings)
    {
        // Spread IO, initialization and destruction across frames instead
        // of blocking the camera's frame when crossing a streaming cell.
        yield return null;
        while (pendingTerrainLoads.Count > 0 || pendingTerrainUnloads.Count > 0)
        {
            if (pendingTerrainUnloads.Count > 0)
            {
                while (!TryBeginTerrainWork()) yield return null;
                Destroy(pendingTerrainUnloads.Dequeue());
                yield return null;
            }
            if (pendingTerrainLoads.Count == 0) continue;
            TerrainTileRecord record = pendingTerrainLoads.Dequeue();
            Vector3 viewer = Player != null ? Player.transform.position : new Vector3(-24f, 0f, -24f);
            Vector2Int currentCenter = settings.WorldToTile(viewer);
            int loadRadius = settings.GetPreloadRadius();
            int distance = Mathf.Max(Mathf.Abs(record.coordinate.x - currentCenter.x), Mathf.Abs(record.coordinate.y - currentCenter.y));
            if (distance <= loadRadius && !loadedTerrainTiles.ContainsKey(record.coordinate))
            {
                ResourceRequest request = Resources.LoadAsync<GameObject>(record.resourcePath);
                yield return request;
                GameObject prefab = request.asset as GameObject;
                if (prefab != null)
                {
                    Transform lod0 = prefab.transform.Find("LOD0");
                    MeshFilter filter = lod0 == null ? null : lod0.GetComponent<MeshFilter>();
                    Mesh mesh = filter == null ? null : filter.sharedMesh;
                    // Readable generated meshes can be cooked on a worker.
                    // Hold the prefab reference and finish the job before instantiation.
                    if (mesh != null && mesh.isReadable)
                    {
                        collisionBakeJob = new CollisionBakeJob { meshId = mesh.GetEntityId() }.Schedule();
                        collisionBakePending = true;
                        JobHandle.ScheduleBatchedJobs();
                        while (!collisionBakeJob.IsCompleted) yield return null;
                        collisionBakeJob.Complete();
                        collisionBakePending = false;
                    }
                }
                // Recheck after IO: the player may already be in another cell.
                viewer = Player != null ? Player.transform.position : new Vector3(-24f, 0f, -24f);
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
                    AsyncInstantiateOperation<GameObject> instantiate =
                        Object.InstantiateAsync(prefab, record.bounds.center, Quaternion.identity);
                    // Awake and hierarchy integration can still touch the
                    // main thread. Keep that work below a small per-frame
                    // budget so crossing a cell cannot consume a whole
                    // render frame.
                    AsyncInstantiateOperation.SetIntegrationTimeMS(1.5f);
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
                            }
                            loadedTerrainTiles.Add(record.coordinate, tileObject);
                        }
                    }
                }
            }
            pendingTerrainCoordinates.Remove(record.coordinate);
            yield return null;
        }
        terrainLoadRoutine = null;
    }

    void OnDestroy()
    {
        if (collisionBakePending) collisionBakeJob.Complete();
        Shader.SetGlobalVector("_VoyageTerrainView", Vector4.zero);
        foreach (GameObject tile in loadedTerrainTiles.Values)
            if (tile != null) Destroy(tile);
        while (pendingTerrainUnloads.Count > 0) Destroy(pendingTerrainUnloads.Dequeue());
        if (Instance == this) Instance = null;
    }

    void UpdateLoadedTerrainLods(Vector3 position, TerrainChunkSettings settings, Vector2Int center)
    {
        TerrainTileRuntime nextActivation = null;
        TerrainTileRuntime nextGrass = null;
        TerrainTileRuntime nextDeactivation = null;
        float activationDistance = float.MaxValue;
        float grassDistance = float.MaxValue;
        foreach (KeyValuePair<Vector2Int, GameObject> pair in loadedTerrainTiles)
        {
            TerrainTileRuntime tile = pair.Value == null ? null : pair.Value.GetComponent<TerrainTileRuntime>();
            if (tile == null) continue;
            tile.UpdateVisualLod(position);
            float distance = tile.Bounds.SqrDistance(position);
            bool collisionWanted = tile.WantsCollision(position, settings);
            if (collisionWanted && !tile.CollisionEnabled && distance < activationDistance)
            {
                nextActivation = tile;
                activationDistance = distance;
            }
            else if (!collisionWanted && tile.CollisionEnabled) nextDeactivation = tile;
            if (collisionWanted && tile.NeedsGrassInitialization && distance < grassDistance)
            {
                nextGrass = tile;
                grassDistance = distance;
            }
        }
        // One expensive action across the loader and activation loop per frame.
        // Near collision/grass takes priority over distant activation and cleanup.
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
        else if (nextDeactivation != null && TryBeginTerrainWork()) nextDeactivation.SetCollisionEnabled(false);
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
