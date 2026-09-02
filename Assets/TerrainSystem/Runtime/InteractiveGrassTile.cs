using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Voyage.TerrainSystem
{
    /// <summary>Lightweight crossed-blade grass owned by one streamed terrain tile.</summary>
    [DisallowMultipleComponent]
    public sealed class InteractiveGrassTile : MonoBehaviour
    {
        [Min(1f)] public float clusterSpacing = 1.6f;
        [Min(1)] public int bladesPerCluster = 8;
        [Min(0.05f)] public float clusterRadius = 0.48f;
        [Min(0.1f)] public float bladeHeight = 1.1f;
        [Range(0f, 1f)] public float density = 0.85f;
        [Min(1)] public int clustersPerFrame = 96;
        [Min(1)] public int runtimeClusterBudget = 2000;
        [Header("Local slope distribution")]
        [Range(0f, 89f)] public float fullDensityBelowSlope = 28f;
        [Range(1f, 90f)] public float noGrassAboveSlope = 58f;
        [Tooltip("Only colliders on these layers are considered grass ground.")]
        public LayerMask grassGroundMask = Physics.DefaultRaycastLayers;
        [Tooltip("Optional pre-baked mesh. When assigned, no runtime grass sampling or mesh generation is performed.")]
        public Mesh bakedMesh;
        [Tooltip("Optional clustered asset. It is drawn with GPU instancing and takes precedence over bakedMesh.")]
        public GrassChunkAsset bakedClusters;
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
        bool initialized;
        Matrix4x4[] instanceMatrices;
        Matrix4x4[] instanceBatch;
        MaterialPropertyBlock instanceProperties;
        Mesh runtimeClusterMesh;

        void OnValidate()
        {
            clusterSpacing = Mathf.Max(1f, clusterSpacing);
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
            GameObject child = new GameObject("Interactive Grass");
            grassObject = child;
            child.transform.SetParent(transform, false);
            meshFilter = child.AddComponent<MeshFilter>();
            meshRenderer = child.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            Material sourceMaterial = material != null ? material : prototype != null ? prototype.material : null;
            runtimeMaterial = sourceMaterial != null ? new Material(sourceMaterial) : CreateDefaultMaterial();
            if (runtimeMaterial != null) runtimeMaterial.enableInstancing = true;
            meshRenderer.sharedMaterial = runtimeMaterial;
            meshRenderer.enabled = false;
            if (bakedClusters != null && bakedClusters.clusterMesh != null && bakedClusters.Count > 0)
            {
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
                ApplyMaterialState();
                return;
            }
            if (bakedMesh != null)
            {
                mesh = bakedMesh;
                meshFilter.sharedMesh = bakedMesh;
                meshRenderer.enabled = true;
                ApplyMaterialState();
                return;
            }
            Transform collisionRoot = transform.Find("Collision");
            Collider terrainCollider = collisionRoot == null ? null : collisionRoot.GetComponent<Collider>();
            buildRoutine = StartCoroutine(BuildMeshAsync(worldBounds, terrainCollider, grassGroundMask.value));
        }

        void LateUpdate()
        {
            bool hasBakedClusters = bakedClusters != null && bakedClusters.clusterMesh != null && bakedClusters.Count > 0;
            Mesh drawMesh = hasBakedClusters ? bakedClusters.clusterMesh : prototype != null && prototype.clusterMesh != null ? prototype.clusterMesh : runtimeClusterMesh;
            if (drawMesh == null || instanceMatrices == null || currentLod >= 3 || runtimeMaterial == null) return;
            if (instanceProperties == null) instanceProperties = new MaterialPropertyBlock();
            float lodDensity = currentLod == 0 ? 1f : currentLod == 1 ? 0.55f : 0.2f;
            int visibleCount = Mathf.Clamp(Mathf.CeilToInt(instanceMatrices.Length * lodDensity), 1, instanceMatrices.Length);
            for (int start = 0; start < visibleCount; start += 1023)
            {
                int count = Mathf.Min(1023, visibleCount - start);
                System.Array.Copy(instanceMatrices, start, instanceBatch, 0, count);
                Graphics.DrawMeshInstanced(drawMesh, 0, runtimeMaterial, instanceBatch, count, instanceProperties,
                    UnityEngine.Rendering.ShadowCastingMode.Off, false, gameObject.layer, null, UnityEngine.Rendering.LightProbeUsage.Off);
            }
        }

        IEnumerator BuildMeshAsync(Bounds worldBounds, Collider terrainCollider, int groundMask)
        {
            float halfX = worldBounds.extents.x;
            float halfZ = worldBounds.extents.z;
            int countX = Mathf.Max(1, Mathf.FloorToInt(worldBounds.size.x / clusterSpacing));
            int countZ = Mathf.Max(1, Mathf.FloorToInt(worldBounds.size.z / clusterSpacing));
            int totalCandidates = countX * countZ;
            int candidateStep = totalCandidates <= runtimeClusterBudget * 4
                ? 1
                : Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt((float)totalCandidates / (runtimeClusterBudget * 4f))));
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
            int processedClusters = 0;
            for (int z = 0; z < countZ; z += candidateStep) for (int x = 0; x < countX; x += candidateStep)
            {
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
                float densityNoise = Mathf.Lerp(0.55f, 1.15f, Mathf.PerlinNoise(world.x * 0.035f, world.z * 0.035f));
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
                    clusterLocal.y = groundHit.point.y - transform.position.y + 0.015f;
                }
                clusterPositions.Add(clusterLocal);
                Quaternion groundRotation = Quaternion.FromToRotation(Vector3.up, groundHit.normal);
                Quaternion yawRotation = Quaternion.AngleAxis((float)random.NextDouble() * 360f, groundHit.normal);
                clusterRotations.Add(yawRotation * groundRotation);
                clusterScales.Add(0.82f + (float)random.NextDouble() * 0.36f);
            }
            if (clusterPositions.Count == 0)
            {
                buildRoutine = null;
                yield break;
            }
            if (prototype == null || prototype.clusterMesh == null)
                runtimeClusterMesh = BuildClusterMesh(random);
            instanceMatrices = new Matrix4x4[clusterPositions.Count];
            for (int i = 0; i < instanceMatrices.Length; i++)
                instanceMatrices[i] = transform.localToWorldMatrix * Matrix4x4.TRS(clusterPositions[i], clusterRotations[i], Vector3.one * clusterScales[i]);
            instanceBatch = new Matrix4x4[1023];
            instanceProperties = new MaterialPropertyBlock();
            meshRenderer.enabled = false;
            ApplyMaterialState();
            buildRoutine = null;
        }

        Mesh BuildClusterMesh(System.Random random)
        {
            var vertices = new List<Vector3>(bladesPerCluster * 8);
            var uvs = new List<Vector2>(bladesPerCluster * 8);
            var randoms = new List<Vector2>(bladesPerCluster * 8);
            var triangles = new List<int>(bladesPerCluster * 12);
            for (int blade = 0; blade < bladesPerCluster; blade++)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                float distance = Mathf.Sqrt((float)random.NextDouble()) * clusterRadius;
                Vector3 local = new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
                float h = bladeHeight * (0.72f + (float)random.NextDouble() * 0.56f);
                float w = h * (0.10f + (float)random.NextDouble() * 0.05f);
                float yaw = (float)random.NextDouble() * Mathf.PI;
                float variation = (float)random.NextDouble();
                AddBlade(vertices, uvs, randoms, triangles, local, h, w, yaw, variation);
                AddBlade(vertices, uvs, randoms, triangles, local, h, w, yaw + Mathf.PI * 0.5f, variation * 0.73f + 0.11f);
            }
            var result = new Mesh { name = "Interactive Grass Cluster Mesh" };
            result.SetVertices(vertices); result.SetUVs(0, uvs); result.SetUVs(1, randoms); result.SetTriangles(triangles, 0, true); result.RecalculateBounds();
            result.UploadMeshData(true);
            return result;
        }

        static void AddBlade(List<Vector3> v, List<Vector2> uv, List<Vector2> randoms, List<int> t, Vector3 p, float h, float w, float yaw, float variation)
        {
            Vector3 side = new Vector3(Mathf.Cos(yaw), 0f, Mathf.Sin(yaw)) * w;
            int start = v.Count;
            v.Add(p - side); v.Add(p + side); v.Add(p + Vector3.up * h);
            uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(0.5f, 1));
            Vector2 instanceRandom = new Vector2(Mathf.Repeat(variation, 1f), Mathf.Repeat(variation * 2.17f + 0.37f, 1f));
            randoms.Add(instanceRandom); randoms.Add(instanceRandom); randoms.Add(instanceRandom);
            t.Add(start); t.Add(start + 1); t.Add(start + 2);
            t.Add(start + 2); t.Add(start + 1); t.Add(start);
        }

        static Material CreateDefaultMaterial()
        {
            Shader shader = Shader.Find("Voyage/Grass/InteractiveUnlit");
            return shader == null ? null : new Material(shader) { color = new Color(0.18f, 0.42f, 0.12f, 1f) };
        }

        public void SetLod(int lod)
        {
            currentLod = Mathf.Clamp(lod, 0, 3);
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
                runtimeMaterial.SetFloat("_InteractionEnabled", interactionNearby && currentLod < 2 ? 1f : 0f);
                runtimeMaterial.SetFloat("_WindStrength", currentLod == 0 ? 0.08f : currentLod == 1 ? 0.035f : 0f);
                runtimeMaterial.SetFloat("_Density", currentLod == 0 ? 1f : currentLod == 1 ? 0.55f : 0.2f);
            }
        }

        void OnDestroy()
        {
            if (buildRoutine != null) StopCoroutine(buildRoutine);
            if (grassObject != null)
            {
                if (meshFilter != null && meshFilter.sharedMesh == mesh) meshFilter.sharedMesh = null;
            }
            if (mesh != null && mesh != bakedMesh) Destroy(mesh);
            if (runtimeClusterMesh != null) Destroy(runtimeClusterMesh);
            if (runtimeMaterial != null) Destroy(runtimeMaterial);
            if (grassObject != null) Destroy(grassObject);
        }
    }
}
