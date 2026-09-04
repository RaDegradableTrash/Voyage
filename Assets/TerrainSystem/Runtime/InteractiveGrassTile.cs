using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Voyage.TerrainSystem
{
    /// <summary>Lightweight crossed-blade grass owned by one streamed terrain tile.</summary>
    [DisallowMultipleComponent]
    public sealed class InteractiveGrassTile : MonoBehaviour
    {
        static readonly Dictionary<int, Mesh> sharedClusterMeshes = new Dictionary<int, Mesh>();

        [Min(0.25f)] public float clusterSpacing = 0.38f;
        [Min(1)] public int bladesPerCluster = 18;
        [Min(0.05f)] public float clusterRadius = 0.70f;
        [Min(0.1f)] public float bladeHeight = 1.75f;
        [Range(0f, 1f)] public float density = 1f;
        [Min(1)] public int clustersPerFrame = 96;
        [Min(1)] public int runtimeClusterBudget = 100000;
        [Header("Stylized appearance")]
        public Color baseColor = new Color(0.64f, 0.42f, 0.14f, 1f);
        public Color rootColor = new Color(0.48f, 0.33f, 0.12f, 1f);
        public Color shadowColor = new Color(0.40f, 0.28f, 0.10f, 1f);
        public Color tipColor = new Color(0.78f, 0.56f, 0.22f, 1f);
        public Color backsideColor = new Color(0.57f, 0.37f, 0.12f, 1f);
        public Color fadeColor = new Color(0.36f, 0.24f, 0.09f, 1f);
        [Min(0.001f)] public float macroScale = 0.018f;
        [Range(0f, 1f)] public float macroStrength = 0.20f;
        [Range(0f, 1f)] public float alphaClip = 0.35f;
        [Min(0f)] public float fadeStart = 105f;
        [Min(0.01f)] public float fadeEnd = 495f;
        [Header("Local slope distribution")]
        [Range(0f, 89f)] public float fullDensityBelowSlope = 28f;
        [Range(1f, 90f)] public float noGrassAboveSlope = 58f;
        [Tooltip("Only colliders on these layers are considered grass ground.")]
        public LayerMask grassGroundMask = Physics.DefaultRaycastLayers;
        [Tooltip("Optional pre-baked mesh. When assigned, no runtime grass sampling or mesh generation is performed.")]
        public Mesh bakedMesh;
        [Tooltip("Optional clustered asset. It is drawn with GPU instancing and takes precedence over bakedMesh.")]
        public GrassChunkAsset bakedClusters;
        [Tooltip("Keep using the legacy per-tile cache. Disabled by default so old prefabs migrate to the dense runtime path.")]
        public bool useLegacyBakedClusters;
        [Tooltip("Shared cluster geometry. Tile placement is generated on demand and is not serialized per tile.")]
        public GrassPrototypeAsset prototype;
        public Vector2Int tileCoordinate;
        public Material material;
        Mesh mesh;
        GameObject grassObject;
        Material runtimeMaterial;
        MeshFilter meshFilter;
        MeshRenderer meshRenderer;
        Coroutine buildRoutine;
        int currentLod = 3;
        bool interactionNearby = true;
        Bounds debugWorldBounds;
        public GrassInteractionSystem.GrassDebugState DebugState { get; private set; } = GrassInteractionSystem.GrassDebugState.Outside;
        public float DebugNearestWheelDistance { get; private set; } = float.MaxValue;
        public int DebugPressingWheelCount { get; private set; }
        bool initialized;
        float tileFade;
        public bool BuildFinished { get; private set; }
        Matrix4x4[] instanceMatrices;
        Matrix4x4[] instanceBatch;
        MaterialPropertyBlock instanceProperties;
        Mesh runtimeClusterMesh;
        bool runtimeClusterMeshShared;
        Mesh runtimeDistantClusterMesh;
        public bool useIndirectRendering = true;
        [Tooltip("Keep grass visible in additional scene cameras and the editor Scene view. The main gameplay camera remains GPU culled.")]
        public bool renderInAdditionalCameras = true;
        [Tooltip("Collect per-tile wheel diagnostics even when the global grass debug UI is hidden.")]
        public bool collectDebugState;
        ComputeBuffer indirectSourceBuffer;
        ComputeBuffer indirectVisibleBuffer;
        ComputeBuffer indirectArgsBuffer;
        Mesh indirectMesh;
        int lastIndirectCullFrame = -100;
        int lastIndirectCullLod = -1;
        Vector3 lastIndirectCullCameraPosition;
        ComputeShader indirectCullingShader;
        int indirectKernel = -1;
        Bounds indirectBounds;
        RenderTexture boundInteractionField;
        RenderTexture boundPermanentInteractionField;
        Vector4 boundInteractionWorld;
        bool hasBoundInteractionWorld;
        static readonly Plane[] sharedFrustumPlanes = new Plane[6];
        static readonly Vector4[] sharedFrustumVectors = new Vector4[6];
        static Camera sharedFrustumCamera;
        static int sharedFrustumFrame = -1;
        static Camera[] sharedCameras = new Camera[4];

        void OnValidate()
        {
            clusterSpacing = Mathf.Max(0.25f, clusterSpacing);
            bladesPerCluster = Mathf.Clamp(bladesPerCluster, 1, 32);
            clusterRadius = Mathf.Max(0.05f, clusterRadius);
            bladeHeight = Mathf.Max(0.1f, bladeHeight);
            density = Mathf.Clamp01(density);
            clustersPerFrame = Mathf.Max(1, clustersPerFrame);
            runtimeClusterBudget = Mathf.Max(1, runtimeClusterBudget);
            fullDensityBelowSlope = Mathf.Clamp(fullDensityBelowSlope, 0f, 89f);
            noGrassAboveSlope = Mathf.Clamp(noGrassAboveSlope, fullDensityBelowSlope + 1f, 90f);
        }

        public void Initialize(Bounds worldBounds)
        {
            if (initialized || buildRoutine != null) return;
            initialized = true;
            tileFade = 0f;
            BuildFinished = false;
            debugWorldBounds = worldBounds;
            GameObject child = new GameObject("Interactive Grass");
            grassObject = child;
            child.transform.SetParent(transform, false);
            meshFilter = child.AddComponent<MeshFilter>();
            meshRenderer = child.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = true;
            Material sourceMaterial = material != null ? material : prototype != null ? prototype.material : null;
            runtimeMaterial = sourceMaterial != null ? new Material(sourceMaterial) : CreateDefaultMaterial();
            if (runtimeMaterial != null)
            {
                runtimeMaterial.enableInstancing = true;
                // Existing GrassMaterial assets may have retained the old
                // AlphaTest queue after the shader changed to distance blend.
                // Override both values on the per-tile copy so the fade is
                // actually composited over the terrain.
                runtimeMaterial.SetOverrideTag("RenderType", "Transparent");
                runtimeMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            meshRenderer.sharedMaterial = runtimeMaterial;
            meshRenderer.enabled = false;
            // A generated tile with bakedClusters already owns the exact
            // grass instances that are visible in the streamed world. Always
            // use that data when it is present; the old opt-in flag allowed
            // those visible instances to bypass this interactive draw path.
            if (bakedClusters != null && bakedClusters.clusterMesh != null && bakedClusters.Count > 0)
            {
                // Keep the baked placement data, but replace legacy sparse
                // cluster geometry with one shared mesh built from the current
                // density/height settings. This updates old streamed tiles
                // without creating a mesh for every tile.
                runtimeClusterMesh = GetSharedClusterMesh(4);
                runtimeClusterMeshShared = true;
                instanceMatrices = new Matrix4x4[bakedClusters.Count];
                for (int i = 0; i < instanceMatrices.Length; i++)
                {
                    Vector3 position = bakedClusters.positions[i];
                    Vector4 parameters = bakedClusters.parameters != null && i < bakedClusters.parameters.Length ? bakedClusters.parameters[i] : new Vector4(0f, 0f, 0f, 1f);
                    float scale = bakedClusters.scales != null && i < bakedClusters.scales.Length ? bakedClusters.scales[i] : 1f;
                    instanceMatrices[i] = transform.localToWorldMatrix * Matrix4x4.TRS(position, new Quaternion(parameters.x, parameters.y, parameters.z, parameters.w), Vector3.one * Mathf.Max(0.01f, scale));
                }
                instanceBatch = new Matrix4x4[1023];
                instanceProperties = new MaterialPropertyBlock();
                runtimeDistantClusterMesh = BuildClusterMesh(new System.Random(unchecked(tileCoordinate.x * 73856093 ^ tileCoordinate.y * 19349663 ^ 0x5F3759DF)), 2);
                ApplyMaterialState();
                BuildFinished = true;
                return;
            }
            if (bakedMesh != null)
            {
                mesh = bakedMesh;
                meshFilter.sharedMesh = bakedMesh;
                meshRenderer.enabled = true;
                ApplyMaterialState();
                BuildFinished = true;
                return;
            }
            Transform collisionRoot = transform.Find("Collision");
            Collider terrainCollider = collisionRoot == null ? null : collisionRoot.GetComponent<Collider>();
            buildRoutine = StartCoroutine(BuildMeshAsync(worldBounds, terrainCollider, grassGroundMask.value));
        }

        void LateUpdate()
        {
            GrassInteractionSystem interaction = GrassInteractionSystem.Instance;
            float nearest;
            int pressing;
            bool diagnosticsEnabled = collectDebugState ||
                                      (interaction != null && (interaction.debugGrassStateMachine || interaction.debugDrawTileStates));
            if (diagnosticsEnabled && interaction != null && interaction.IsReady)
                DebugState = interaction.GetDebugState(debugWorldBounds, out nearest, out pressing);
            else
            {
                DebugState = GrassInteractionSystem.GrassDebugState.Outside;
                nearest = float.MaxValue;
                pressing = 0;
            }
            DebugNearestWheelDistance = nearest;
            DebugPressingWheelCount = pressing;
            if (instanceProperties == null) instanceProperties = new MaterialPropertyBlock();
            // Bind interaction data before the draw eligibility checks. A
            // streamed tile can spend several frames with no generated
            // cluster mesh or at a non-drawing LOD, then become visible again;
            // waiting until after those checks leaves its material with stale
            // wheel data on the first visible frame.
            BindInteractionField();
            bool shouldBeVisible = currentLod < 3 && (instanceMatrices != null || meshRenderer != null);
            float previousTileFade = tileFade;
            tileFade = Mathf.MoveTowards(tileFade, shouldBeVisible ? 1f : 0f,
                                         Time.deltaTime * (shouldBeVisible ? 2.8f : 8f));
            if (Mathf.Abs(tileFade - previousTileFade) > 0.0001f)
                ApplyMaterialState();
            // Wheel slots and interaction textures are published globally.
            // Keeping the property block free of per-wheel overrides allows
            // close grass to use the same GPU-culled indirect path as the
            // middle and far LODs without losing live tyre deformation.
            instanceProperties.Clear();
            bool hasBakedClusters = bakedClusters != null && bakedClusters.clusterMesh != null && bakedClusters.Count > 0;
            Mesh sourceMesh = runtimeClusterMesh != null ? runtimeClusterMesh : hasBakedClusters ? bakedClusters.clusterMesh : prototype != null && prototype.clusterMesh != null ? prototype.clusterMesh : null;
            Mesh drawMesh = currentLod == 0 || runtimeDistantClusterMesh == null ? sourceMesh : runtimeDistantClusterMesh;
            if (drawMesh == null || instanceMatrices == null || currentLod >= 3 || runtimeMaterial == null) return;
            // Every drawable LOD uses compute culling when available. The
            // global wheel slots are compatible with procedural instancing,
            // so LOD0 no longer needs dozens of unculled 1023-instance draws.
            if (useIndirectRendering && TryDrawIndirect(drawMesh))
            {
                DrawAdditionalCameras(drawMesh, Camera.main);
                return;
            }
            // Generated prefabs may carry a material serialized before the
            // instanced grass path existed. Enforce this immediately before
            // drawing so stale material state cannot disable the whole pass.
            if (!runtimeMaterial.enableInstancing) runtimeMaterial.enableInstancing = true;
            if (!runtimeMaterial.enableInstancing) return;
            DrawDirect(drawMesh, null);
        }

        void DrawAdditionalCameras(Mesh drawMesh, Camera mainCamera)
        {
            if (!renderInAdditionalCameras) return;
            int cameraCount = Camera.allCamerasCount;
            if (sharedCameras.Length < cameraCount) sharedCameras = new Camera[Mathf.NextPowerOfTwo(cameraCount)];
            int found = Camera.GetAllCameras(sharedCameras);
            Camera sceneCamera = null;
#if UNITY_EDITOR
            if (UnityEditor.SceneView.lastActiveSceneView != null)
                sceneCamera = UnityEditor.SceneView.lastActiveSceneView.camera;
#endif
            bool drewSceneCamera = false;
            for (int i = 0; i < found; i++)
            {
                Camera camera = sharedCameras[i];
                if (camera == null || camera == mainCamera || !camera.enabled ||
                    camera.cameraType == CameraType.Preview)
                    continue;
                DrawDirect(drawMesh, camera);
                if (camera == sceneCamera) drewSceneCamera = true;
            }
            if (sceneCamera != null && sceneCamera != mainCamera && sceneCamera.enabled && !drewSceneCamera)
                DrawDirect(drawMesh, sceneCamera);
        }

        void DrawDirect(Mesh drawMesh, Camera targetCamera)
        {
            // Retain a continuous mid/far meadow; the shader handles the
            // color/alpha transition and very low LOD density becomes noise.
            float lodDensity = currentLod == 0 ? 1f : currentLod == 1 ? 0.42f : 0.04f;
            int visibleCount = Mathf.Clamp(Mathf.CeilToInt(instanceMatrices.Length * lodDensity), 1, instanceMatrices.Length);
            // The instance array is generated in grid order. Copying the first
            // visibleCount entries would therefore remove an entire spatial
            // section of the tile at lower LODs. Select evenly across the full
            // array so density reduction remains spatially uniform.
            for (int start = 0; start < visibleCount; start += 1023)
            {
                int count = Mathf.Min(1023, visibleCount - start);
                for (int batchIndex = 0; batchIndex < count; batchIndex++)
                {
                    int sampleIndex = Mathf.Min(instanceMatrices.Length - 1,
                        (int)(((long)(start + batchIndex) * instanceMatrices.Length) / visibleCount));
                    instanceBatch[batchIndex] = instanceMatrices[sampleIndex];
                }
                Graphics.DrawMeshInstanced(drawMesh, 0, runtimeMaterial, instanceBatch, count, instanceProperties,
                    UnityEngine.Rendering.ShadowCastingMode.Off, true, gameObject.layer, targetCamera, UnityEngine.Rendering.LightProbeUsage.BlendProbes);
            }
        }

        bool TryDrawIndirect(Mesh drawMesh)
        {
            if (!SystemInfo.supportsComputeShaders || instanceMatrices.Length == 0) return false;
            if (indirectCullingShader == null)
            {
                indirectCullingShader = Resources.Load<ComputeShader>("TerrainSystem/GrassCulling");
                if (indirectCullingShader == null) return false;
                indirectKernel = indirectCullingShader.FindKernel("CSMain");
            }
            if (indirectSourceBuffer == null || indirectSourceBuffer.count != instanceMatrices.Length || indirectMesh != drawMesh)
            {
                ReleaseIndirectBuffers();
                const int stride = sizeof(float) * 16;
                indirectSourceBuffer = new ComputeBuffer(instanceMatrices.Length, stride, ComputeBufferType.Structured);
                indirectVisibleBuffer = new ComputeBuffer(instanceMatrices.Length, stride, ComputeBufferType.Append);
                indirectArgsBuffer = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
                indirectSourceBuffer.SetData(instanceMatrices);
                indirectArgsBuffer.SetData(new uint[] { drawMesh.GetIndexCount(0), 0, drawMesh.GetIndexStart(0), drawMesh.GetBaseVertex(0), 0 });
                indirectMesh = drawMesh;
                // Keep the vertical extent conservative because terrain
                // bounds use a tall Y sentinel, but use the actual tile XZ
                // footprint so Unity can reject off-screen indirect draws
                // before dispatching the culling shader.
                Vector3 boundsCenter = debugWorldBounds.center;
                Vector3 boundsSize = debugWorldBounds.size;
                boundsSize.x = Mathf.Max(1f, boundsSize.x + (bladeHeight + clusterRadius) * 2f);
                boundsSize.z = Mathf.Max(1f, boundsSize.z + (bladeHeight + clusterRadius) * 2f);
                boundsSize.y = Mathf.Max(1000f, Mathf.Min(boundsSize.y, 100000f));
                indirectBounds = new Bounds(boundsCenter, boundsSize);
            }
            Camera camera = Camera.main;
            if (camera == null) return false;
            // Every indirect grass tile uses the same camera frustum in a
            // frame. Cache the six planes and their compute-friendly vectors
            // instead of allocating two arrays per tile per frame.
            if (sharedFrustumFrame != Time.frameCount || sharedFrustumCamera != camera)
            {
                GeometryUtility.CalculateFrustumPlanes(camera, sharedFrustumPlanes);
                for (int i = 0; i < sharedFrustumVectors.Length; i++)
                {
                    Plane plane = sharedFrustumPlanes[i];
                    sharedFrustumVectors[i] = new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
                }
                sharedFrustumCamera = camera;
                sharedFrustumFrame = Time.frameCount;
            }
            bool recull = currentLod != lastIndirectCullLod ||
                          Time.frameCount - lastIndirectCullFrame >= 3 ||
                          (camera.transform.position - lastIndirectCullCameraPosition).sqrMagnitude > 4f;
            if (recull)
            {
                indirectVisibleBuffer.SetCounterValue(0);
                indirectCullingShader.SetBuffer(indirectKernel, "_SourceMatrices", indirectSourceBuffer);
                indirectCullingShader.SetBuffer(indirectKernel, "_VisibleMatrices", indirectVisibleBuffer);
                indirectCullingShader.SetVector("_CameraPosition", camera.transform.position);
                indirectCullingShader.SetVectorArray("_FrustumPlanes", sharedFrustumVectors);
                indirectCullingShader.SetFloat("_MaxDistance", Mathf.Max(fadeEnd, 1f));
                indirectCullingShader.SetFloat("_InstanceDensity", currentLod == 0 ? 1f : currentLod == 1 ? 0.42f : 0.04f);
                indirectCullingShader.SetFloat("_InstanceRadius", Mathf.Max(1f, bladeHeight + clusterRadius));
                indirectCullingShader.Dispatch(indirectKernel, Mathf.CeilToInt(instanceMatrices.Length / 64f), 1, 1);
                ComputeBuffer.CopyCount(indirectVisibleBuffer, indirectArgsBuffer, sizeof(uint));
                lastIndirectCullFrame = Time.frameCount;
                lastIndirectCullLod = currentLod;
                lastIndirectCullCameraPosition = camera.transform.position;
            }
            runtimeMaterial.SetBuffer("_VoyageGrassMatrices", indirectVisibleBuffer);
            Graphics.DrawMeshInstancedIndirect(drawMesh, 0, runtimeMaterial, indirectBounds, indirectArgsBuffer, 0,
                instanceProperties, UnityEngine.Rendering.ShadowCastingMode.Off, true, gameObject.layer, camera,
                UnityEngine.Rendering.LightProbeUsage.BlendProbes);
            return true;
        }

        void BindInteractionField()
        {
            GrassInteractionSystem interaction = GrassInteractionSystem.Instance;
            if (interaction == null || !interaction.IsReady) return;

            // This is a per-tile material instance. Bind the live field
            // locally so a material-local texture slot cannot mask the
            // dynamic tire interaction data published by the system.
            Vector4 world = interaction.WorldToUv;
            if (boundInteractionField != interaction.Field)
            {
                runtimeMaterial.SetTexture("_VoyageGrassInteraction", interaction.Field);
                boundInteractionField = interaction.Field;
            }
            if (boundPermanentInteractionField != interaction.PermanentField)
            {
                runtimeMaterial.SetTexture("_VoyageGrassPermanentInteraction", interaction.PermanentField);
                boundPermanentInteractionField = interaction.PermanentField;
            }
            if (!hasBoundInteractionWorld || boundInteractionWorld != world)
            {
                runtimeMaterial.SetVector("_VoyageGrassInteractionWorld", world);
                boundInteractionWorld = world;
                hasBoundInteractionWorld = true;
            }
        }

        void ReleaseIndirectBuffers()
        {
            if (indirectSourceBuffer != null) indirectSourceBuffer.Release();
            if (indirectVisibleBuffer != null) indirectVisibleBuffer.Release();
            if (indirectArgsBuffer != null) indirectArgsBuffer.Release();
            indirectSourceBuffer = null;
            indirectVisibleBuffer = null;
            indirectArgsBuffer = null;
            indirectMesh = null;
            lastIndirectCullFrame = -100;
            lastIndirectCullLod = -1;
        }

        IEnumerator BuildMeshAsync(Bounds worldBounds, Collider terrainCollider, int groundMask)
        {
            float halfX = worldBounds.extents.x;
            float halfZ = worldBounds.extents.z;
            int countX = Mathf.Max(1, Mathf.FloorToInt(worldBounds.size.x / clusterSpacing));
            int countZ = Mathf.Max(1, Mathf.FloorToInt(worldBounds.size.z / clusterSpacing));
            int totalCandidates = countX * countZ;
            int candidateStep = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt((float)totalCandidates / Mathf.Max(1, runtimeClusterBudget))));
            // TerrainChunkSettings uses a very tall Y extent as an XZ-only
            // bounds sentinel. Use the real collision bounds when available
            // so each generated blade does not cast through an unnecessary
            // 100000-unit vertical range.
            Bounds rayBounds = terrainCollider != null ? terrainCollider.bounds : worldBounds;
            float rayTop = rayBounds.max.y + (terrainCollider != null ? 2f : 10f);
            float rayDistance = Mathf.Max(10f, rayTop - rayBounds.min.y + 2f);
            var clusterPositions = new List<Vector3>(Mathf.Min(runtimeClusterBudget, totalCandidates));
            var clusterRotations = new List<Quaternion>(clusterPositions.Capacity);
            var clusterScales = new List<float>(clusterPositions.Capacity);
            int seed = unchecked(tileCoordinate.x * 73856093 ^ tileCoordinate.y * 19349663);
            System.Random random = new System.Random(seed);
            // Publish geometry before the terrain sampling loop. The old path
            // created the mesh only after every raycast had completed, which
            // made a streamed tile look empty for several seconds.
            bool prototypeNeedsExpandedGeometry = prototype != null &&
                                                  prototype.clusterMesh != null &&
                                                  prototype.clusterMesh.vertexCount < bladesPerCluster * 4 * 8;
            int meshSeed = seed ^ unchecked((int)0x5F3759DF);
            if (runtimeDistantClusterMesh == null)
                runtimeDistantClusterMesh = BuildClusterMesh(new System.Random(meshSeed), 2);
            if (prototype == null || prototype.clusterMesh == null || prototypeNeedsExpandedGeometry)
            {
                runtimeClusterMesh = BuildClusterMesh(new System.Random(meshSeed), 4);
            }
            // Publish one early batch for responsive streaming, then use
            // larger batches to avoid repeatedly allocating/copying the full
            // instance matrix array while the tile is still being sampled.
            int nextPublishCount = 256;
            int processedClusters = 0;
            Vector3 viewerPosition = Camera.main != null ? Camera.main.transform.position : worldBounds.center;
            GrassInteractionSystem interaction = GrassInteractionSystem.Instance;
            if (interaction != null && interaction.followTarget != null)
                viewerPosition = interaction.followTarget.position;
            Vector3 viewerLocal = transform.InverseTransformPoint(viewerPosition);
            int viewerX = Mathf.Clamp(Mathf.FloorToInt((viewerLocal.x + halfX) / Mathf.Max(worldBounds.size.x, 0.001f) * countX), 0, countX - 1);
            int viewerZ = Mathf.Clamp(Mathf.FloorToInt((viewerLocal.z + halfZ) / Mathf.Max(worldBounds.size.z, 0.001f) * countZ), 0, countZ - 1);
            viewerX = (viewerX / candidateStep) * candidateStep;
            viewerZ = (viewerZ / candidateStep) * candidateStep;
            // Generate the grid from the viewer outward. The old fixed
            // top-left-to-bottom-right order left the grass under the vehicle
            // waiting behind thousands of unrelated terrain samples.
            for (int zOrder = 0; zOrder < countZ * 2 && clusterPositions.Count < runtimeClusterBudget; zOrder++)
            {
                int zOffset = zOrder == 0 ? 0 : zOrder % 2 == 1 ? -(zOrder + 1) / 2 : zOrder / 2;
                int z = viewerZ + zOffset * candidateStep;
                if (z < 0 || z >= countZ) continue;
                for (int xOrder = 0; xOrder < countX * 2 && clusterPositions.Count < runtimeClusterBudget; xOrder++)
                {
                    int xOffset = xOrder == 0 ? 0 : xOrder % 2 == 1 ? -(xOrder + 1) / 2 : xOrder / 2;
                    int x = viewerX + xOffset * candidateStep;
                    if (x < 0 || x >= countX) continue;
                processedClusters++;
                if (processedClusters >= clustersPerFrame)
                {
                    processedClusters = 0;
                    yield return null;
                }
                float px = Mathf.Lerp(-halfX, halfX, (x + 0.5f) / countX) + (float)(random.NextDouble() - 0.5) * clusterSpacing;
                float pz = Mathf.Lerp(-halfZ, halfZ, (z + 0.5f) / countZ) + (float)(random.NextDouble() - 0.5) * clusterSpacing;
                Vector3 clusterLocal = new Vector3(px, 0f, pz);
                Vector3 world = transform.TransformPoint(clusterLocal);
                // Keep the jittered grid as the coverage scaffold, then use a
                // stable world-space noise field to create natural density
                // patches without introducing visible rows or large holes.
                // Use noise for subtle clumping only. A 0.55 minimum created
                // broad bare patches even with authored density near 1.0,
                // which read as missing grass instead of natural variation.
                float densityNoise = Mathf.Lerp(0.88f, 1.08f, Mathf.PerlinNoise(world.x * 0.035f, world.z * 0.035f));
                if (random.NextDouble() > density * densityNoise) continue;
                Ray ray = new Ray(new Vector3(world.x, rayTop, world.z), Vector3.down);
                RaycastHit groundHit;
                bool foundHit = Physics.Raycast(ray, out groundHit, rayDistance, groundMask, QueryTriggerInteraction.Ignore);
                if (foundHit && terrainCollider != null && groundHit.collider != terrainCollider)
                {
                    RaycastHit[] hits = Physics.RaycastAll(ray, rayDistance, groundMask, QueryTriggerInteraction.Ignore);
                    foundHit = false;
                    for (int hitIndex = 0; hitIndex < hits.Length; hitIndex++)
                    {
                        RaycastHit candidate = hits[hitIndex];
                        if (candidate.collider == terrainCollider)
                        {
                            groundHit = candidate;
                            foundHit = true;
                            break;
                        }
                    }
                }
                // A missing hit is not a valid grass base. The generated
                // terrain normally has a collision mesh; if it does not,
                // avoid silently creating floating blades at local Y = 0.
                if (!foundHit) continue;
                if (foundHit)
                {
                    // Evaluate each candidate against its own ground normal.
                    // Flat ground keeps full density; the transition band
                    // fades continuously to zero on steep mountain walls.
                    float slope = Vector3.Angle(groundHit.normal, Vector3.up);
                    float slopeDensity = 1f - Mathf.InverseLerp(fullDensityBelowSlope, noGrassAboveSlope, slope);
                    if (random.NextDouble() > slopeDensity) continue;
                    // Convert the hit back through the exact tile transform.
                    // Subtracting transform.position.y only works for an
                    // identity transform; streamed tiles can be nested under
                    // a translated/scaled parent, which leaves every blade
                    // with an incorrect world-space root height.
                    Vector3 groundLocal = transform.InverseTransformPoint(groundHit.point);
                    clusterLocal.y = groundLocal.y + 0.015f;
                }
                clusterPositions.Add(clusterLocal);
                Vector3 groundNormalLocal = transform.InverseTransformDirection(groundHit.normal).normalized;
                Quaternion groundRotation = Quaternion.FromToRotation(Vector3.up, groundNormalLocal);
                Quaternion yawRotation = Quaternion.AngleAxis((float)random.NextDouble() * 360f, groundNormalLocal);
                clusterRotations.Add(yawRotation * groundRotation);
                clusterScales.Add(0.82f + (float)random.NextDouble() * 0.36f);
                if (clusterPositions.Count >= nextPublishCount)
                {
                    PublishRuntimeInstances(clusterPositions, clusterRotations, clusterScales);
                    nextPublishCount = clusterPositions.Count + 1024;
                }
                }
            }
            if (clusterPositions.Count == 0)
            {
                buildRoutine = null;
                BuildFinished = true;
                yield break;
            }
            PublishRuntimeInstances(clusterPositions, clusterRotations, clusterScales);
            meshRenderer.enabled = false;
            ApplyMaterialState();
            buildRoutine = null;
            BuildFinished = true;
        }

        void PublishRuntimeInstances(List<Vector3> positions, List<Quaternion> rotations, List<float> scales)
        {
            if (positions == null || positions.Count == 0) return;
            instanceMatrices = new Matrix4x4[positions.Count];
            for (int i = 0; i < instanceMatrices.Length; i++)
                instanceMatrices[i] = transform.localToWorldMatrix * Matrix4x4.TRS(positions[i], rotations[i], Vector3.one * scales[i]);
            instanceBatch = new Matrix4x4[1023];
            instanceProperties = new MaterialPropertyBlock();
            ApplyMaterialState();
        }

        Mesh BuildClusterMesh(System.Random random, int planeCount)
        {
            var vertices = new List<Vector3>(bladesPerCluster * 32);
            var normals = new List<Vector3>(vertices.Capacity);
            var uvs = new List<Vector2>(bladesPerCluster * 32);
            var randoms = new List<Vector2>(bladesPerCluster * 32);
            var bladeData = new List<Vector2>(bladesPerCluster * 32);
            var triangles = new List<int>(bladesPerCluster * 72);
            for (int blade = 0; blade < bladesPerCluster; blade++)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                float distance = Mathf.Sqrt((float)random.NextDouble()) * clusterRadius;
                Vector3 local = new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
                float h = bladeHeight * (0.72f + (float)random.NextDouble() * 0.56f);
                float w = h * (0.20f + (float)random.NextDouble() * 0.10f);
                float yaw = (float)random.NextDouble() * Mathf.PI;
                float variation = (float)random.NextDouble();
                AddBlade(vertices, normals, uvs, randoms, bladeData, triangles, local, h, w, yaw, variation);
                if (planeCount > 1)
                    AddBlade(vertices, normals, uvs, randoms, bladeData, triangles, local, h, w, yaw + Mathf.PI * 0.5f, variation * 0.73f + 0.11f);
                if (planeCount > 2)
                    AddBlade(vertices, normals, uvs, randoms, bladeData, triangles, local, h, w, yaw + Mathf.PI / 3f, variation * 0.51f + 0.23f);
                if (planeCount > 3)
                    AddBlade(vertices, normals, uvs, randoms, bladeData, triangles, local, h, w, yaw + Mathf.PI * 0.25f, variation * 0.37f + 0.47f);
            }
            var result = new Mesh { name = "Interactive Grass Cluster Mesh" };
            result.SetVertices(vertices); result.SetNormals(normals); result.SetUVs(0, uvs); result.SetUVs(1, randoms); result.SetUVs(2, bladeData); result.SetTriangles(triangles, 0, true); result.RecalculateBounds();
            result.UploadMeshData(true);
            return result;
        }

        static void AddBlade(List<Vector3> v, List<Vector3> normals, List<Vector2> uv, List<Vector2> randoms, List<Vector2> bladeData, List<int> t, Vector3 p, float h, float w, float yaw, float variation)
        {
            Vector3 side = new Vector3(Mathf.Cos(yaw), 0f, Mathf.Sin(yaw)) * w;
            Vector3 faceNormal = Vector3.Cross(side, Vector3.up).normalized;
            Vector2 instanceRandom = new Vector2(Mathf.Repeat(variation, 1f), Mathf.Repeat(variation * 2.17f + 0.37f, 1f));
            int first = v.Count;
            // Four rows make three actual bend segments. The shader receives
            // the row height through UV.y, so the curve remains visible even
            // when the blade is deformed by wind or a tire.
            for (int joint = 0; joint <= 3; joint++)
            {
                float normalized = joint / 3f;
                float rowWidth = Mathf.Lerp(w, w * 0.12f, normalized);
                Vector3 center = p + Vector3.up * (h * normalized);
                if (joint == 3) center += faceNormal * (w * 0.45f);
                v.Add(center - side.normalized * rowWidth);
                v.Add(center + side.normalized * rowWidth);
                normals.Add(faceNormal); normals.Add(faceNormal);
                uv.Add(new Vector2(0f, normalized)); uv.Add(new Vector2(1f, normalized));
                randoms.Add(instanceRandom); randoms.Add(instanceRandom);
                Vector2 authoredBladeData = new Vector2(h, joint);
                bladeData.Add(authoredBladeData); bladeData.Add(authoredBladeData);
            }
            for (int segment = 0; segment < 3; segment++)
            {
                int a = first + segment * 2;
                int b = a + 1;
                int c = a + 2;
                int d = a + 3;
                t.Add(a); t.Add(b); t.Add(c);
                t.Add(b); t.Add(d); t.Add(c);
            }
        }

        Mesh GetSharedClusterMesh(int planeCount)
        {
            int key = unchecked(bladesPerCluster * 1000000 + Mathf.RoundToInt(clusterRadius * 1000f) * 1000 + Mathf.RoundToInt(bladeHeight * 100f) * 10 + planeCount);
            Mesh shared;
            if (sharedClusterMeshes.TryGetValue(key, out shared) && shared != null) return shared;
            shared = BuildClusterMesh(new System.Random(0x5F3759DF ^ key), planeCount);
            shared.name = "Runtime Grass Cluster " + key;
            shared.hideFlags = HideFlags.HideAndDontSave;
            sharedClusterMeshes[key] = shared;
            return shared;
        }

        static Material CreateDefaultMaterial()
        {
            Shader shader = Shader.Find("Voyage/Grass/InteractiveLit");
            if (shader == null) shader = Shader.Find("Voyage/Grass/InteractiveUnlit");
            return shader == null ? null : new Material(shader) { color = new Color(0.28f, 0.38f, 0.14f, 1f) };
        }

        public void SetLod(int lod)
        {
            int nextLod = Mathf.Clamp(lod, 0, 3);
            if (currentLod != nextLod) tileFade = 0f;
            currentLod = nextLod;
            if (grassObject != null) grassObject.SetActive(currentLod < 3);
            ApplyMaterialState();
        }

        public void SetInteractionProximity(bool nearby)
        {
            if (interactionNearby == nearby) return;
            interactionNearby = nearby;
            ApplyMaterialState();
        }

        void ApplyMaterialState()
        {
            if (runtimeMaterial != null)
            {
                // The interaction field is already bounded in world space and
                // fades at its own edge. Do not gate it by the tile AABB:
                // streamed tiles can straddle that AABB while still drawing
                // grass inside the active field, which made visible grass
                // ignore tire stamps entirely. LOD3 has no grass draw anyway.
                runtimeMaterial.SetFloat("_InteractionEnabled", currentLod < 3 ? 1f : 0f);
                runtimeMaterial.SetFloat("_ImmediateInteractionEnabled", currentLod == 0 ? 1f : 0f);
                runtimeMaterial.SetFloat("_FieldInteractionEnabled", currentLod <= 1 ? 1f : 0f);
                // Far cards use ordered clipping to avoid blending every
                // overlapping transparent fragment. Close and middle grass
                // retain the smooth authored fade.
                runtimeMaterial.SetFloat("_DistantAlphaClip", currentLod >= 2 ? 1f : 0f);
                runtimeMaterial.SetFloat("_WindStrength", currentLod == 0 ? 0.48f : currentLod == 1 ? 0.28f : 0.12f);
                runtimeMaterial.SetFloat("_WindSpeed", currentLod == 0 ? 1.15f : currentLod == 1 ? 0.9f : 0.68f);
                runtimeMaterial.SetFloat("_BendStrength", currentLod == 0 ? 1.55f : currentLod == 1 ? 1.25f : 0.9f);
                // Density is resolved by placement and LOD instance count.
                // Clipping individual blades makes dense clumps look sparse.
                runtimeMaterial.SetFloat("_Density", 1f);
                runtimeMaterial.SetFloat("_TileFade", tileFade);
                // Existing generated prefabs serialized the previous, very
                // yellow palette. Migrate only those exact legacy defaults at
                // runtime so old tiles receive the softer root/ground blend
                // without overwriting deliberate per-tile art tuning.
                Color resolvedBaseColor = IsLegacyGrassColor(baseColor, 0.34f, 0.43f, 0.08f) ||
                                           IsLegacyGrassColor(baseColor, 0.25f, 0.36f, 0.09f)
                    ? new Color(0.72f, 0.38f, 0.10f, 1f) : baseColor;
                Color resolvedTipColor = IsLegacyGrassColor(tipColor, 0.58f, 0.48f, 0.10f) ||
                                          IsLegacyGrassColor(tipColor, 0.42f, 0.40f, 0.11f)
                    ? new Color(0.84f, 0.49f, 0.14f, 1f) : tipColor;
                Color resolvedBacksideColor = IsLegacyGrassColor(backsideColor, 0.43f, 0.36f, 0.07f) ||
                                               IsLegacyGrassColor(backsideColor, 0.30f, 0.32f, 0.085f)
                    ? new Color(0.65f, 0.32f, 0.08f, 1f) : backsideColor;
                runtimeMaterial.SetColor("_BaseColor", resolvedBaseColor);
                runtimeMaterial.SetColor("_RootColor", rootColor);
                runtimeMaterial.SetColor("_ShadowColor", shadowColor);
                runtimeMaterial.SetColor("_TipColor", resolvedTipColor);
                runtimeMaterial.SetColor("_BacksideColor", resolvedBacksideColor);
                runtimeMaterial.SetColor("_FadeColor", fadeColor);
                runtimeMaterial.SetFloat("_MacroScale", macroScale);
                runtimeMaterial.SetFloat("_MacroStrength", macroStrength);
                runtimeMaterial.SetFloat("_AlphaClip", alphaClip);
                runtimeMaterial.SetFloat("_FadeStart", fadeStart);
                runtimeMaterial.SetFloat("_FadeEnd", fadeEnd);
                runtimeMaterial.SetFloat("_BladeHeight", bladeHeight);
            }
        }

        static bool IsLegacyGrassColor(Color color, float r, float g, float b)
        {
            return Mathf.Abs(color.r - r) < 0.001f &&
                   Mathf.Abs(color.g - g) < 0.001f &&
                   Mathf.Abs(color.b - b) < 0.001f;
        }

        void OnDestroy()
        {
            if (buildRoutine != null) StopCoroutine(buildRoutine);
            if (grassObject != null)
            {
                if (meshFilter != null && meshFilter.sharedMesh == mesh) meshFilter.sharedMesh = null;
            }
            if (mesh != null && mesh != bakedMesh) Destroy(mesh);
            if (runtimeClusterMesh != null && !runtimeClusterMeshShared) Destroy(runtimeClusterMesh);
            if (runtimeDistantClusterMesh != null) Destroy(runtimeDistantClusterMesh);
            if (runtimeMaterial != null) Destroy(runtimeMaterial);
            ReleaseIndirectBuffers();
            if (grassObject != null) Destroy(grassObject);
        }

        void OnDrawGizmos()
        {
            if (!Application.isPlaying || GrassInteractionSystem.Instance == null || !GrassInteractionSystem.Instance.debugDrawTileStates) return;
            Color color;
            switch (DebugState)
            {
                case GrassInteractionSystem.GrassDebugState.Pressing: color = new Color(1f, 0.08f, 0.02f, 1f); break;
                case GrassInteractionSystem.GrassDebugState.Recovering: color = new Color(0.1f, 0.3f, 1f, 1f); break;
                case GrassInteractionSystem.GrassDebugState.NearbyIdle: color = Color.yellow; break;
                default: color = Color.gray; break;
            }
            Gizmos.color = color;
            Gizmos.DrawWireCube(debugWorldBounds.center, debugWorldBounds.size);
        }
    }
}
