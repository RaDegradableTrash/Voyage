using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
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
    Vector2Int streamedCenter;
    bool hasStreamedCenter;

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
        StreamTerrain(spawnPoint, true);
        Physics.SyncTransforms();
        Debug.Log("FBX TERRAIN // loaded " + loadedTerrainTiles.Count + " nearby modeled blocks");
        yield return null;
    }

    void UpdateTerrainStreaming()
    {
        if (terrainIndex == null || terrainIndex.settings == null) return;
        Vector3 position = Player != null ? Player.transform.position : new Vector3(-24f, 0f, -24f);
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

        int loadRadius = Mathf.Max(settings.loadedRadius, settings.preloadRadius);
        int unloadRadius = Mathf.Max(loadRadius + 1, settings.unloadRadius);
        HashSet<Vector2Int> wanted = new HashSet<Vector2Int>();
        for (int y = center.y - loadRadius; y <= center.y + loadRadius; y++)
        for (int x = center.x - loadRadius; x <= center.x + loadRadius; x++)
            wanted.Add(new Vector2Int(x, y));

        for (int i = 0; i < terrainIndex.tiles.Count; i++)
        {
            TerrainTileRecord record = terrainIndex.tiles[i];
            if (record == null || !wanted.Contains(record.coordinate) || loadedTerrainTiles.ContainsKey(record.coordinate)) continue;
            GameObject prefab = Resources.Load<GameObject>(record.resourcePath);
            if (prefab == null) continue;
            // Generated tile meshes are centered around local (0, 0, 0); place
            // the root at the record center so neighboring tiles do not stack.
            GameObject tileObject = Instantiate(prefab, record.bounds.center, Quaternion.identity);
            tileObject.name = "FBX TERRAIN BLOCK " + record.coordinate;
            TerrainTileRuntime tile = tileObject.GetComponent<TerrainTileRuntime>();
            if (tile != null)
            {
                tile.Initialize(record, settings, false, position);
                int distance = Mathf.Max(Mathf.Abs(record.coordinate.x - center.x), Mathf.Abs(record.coordinate.y - center.y));
                tile.SetCollisionEnabled(settings.enableCollisionWhenLoaded && distance <= settings.collisionRadius);
            }
            loadedTerrainTiles.Add(record.coordinate, tileObject);
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
            if (tile != null) Destroy(tile);
            loadedTerrainTiles.Remove(stale[i]);
        }
    }

    void UpdateLoadedTerrainLods(Vector3 position, TerrainChunkSettings settings, Vector2Int center)
    {
        foreach (KeyValuePair<Vector2Int, GameObject> pair in loadedTerrainTiles)
        {
            TerrainTileRuntime tile = pair.Value == null ? null : pair.Value.GetComponent<TerrainTileRuntime>();
            if (tile == null) continue;
            int distance = Mathf.Max(Mathf.Abs(pair.Key.x - center.x), Mathf.Abs(pair.Key.y - center.y));
            tile.SetCollisionEnabled(settings.enableCollisionWhenLoaded && distance <= settings.collisionRadius);
            tile.UpdateLod(position);
        }
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
        return material;
    }

    public void SetHeadlights(bool enabled) { if (Player != null) Player.SetHeadlights(enabled); }
    public void ShakeGameplayCamera(float strength) { if (cameraFollow != null) cameraFollow.Shake(strength); }
}
