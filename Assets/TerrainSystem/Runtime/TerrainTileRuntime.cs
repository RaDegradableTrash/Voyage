using UnityEngine;
using UnityEngine.Rendering;

namespace Voyage.TerrainSystem
{
    [DisallowMultipleComponent]
    public sealed class TerrainTileRuntime : MonoBehaviour
    {
        [SerializeField] private Vector2Int coordinate;
        [SerializeField] private Bounds bounds;
        [SerializeField] private GameObject[] lodRoots = new GameObject[4];
        [SerializeField] private bool hlod;
        private Collider[] colliders;
        private static Material grasslandFallbackMaterial;
        private static bool terrainShaderDiagnosticLogged;
        private int currentLod = -1;
        private TerrainChunkSettings settings;
        private bool collisionStateKnown;
        private bool collisionState;

        public Vector2Int Coordinate => coordinate;
        public Bounds Bounds => bounds;
        public int CurrentLod => currentLod;
        public bool IsHlod => currentLod >= 3;
        public bool GrassBuildFinished
        {
            get
            {
                InteractiveGrassTile grass = GetComponent<InteractiveGrassTile>();
                return grass == null || grass.BuildFinished;
            }
        }
        public bool CollisionEnabled
        {
            get
            {
                if (colliders == null) colliders = GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++) if (colliders[i] != null && colliders[i].enabled) return true;
                return false;
            }
        }

        public void LimitLod(int maximumLod)
        {
            if (currentLod > maximumLod) SetLod(maximumLod);
        }

        private void Awake()
        {
            DisableGeneratedSkirts();
            EnsureCollisionCollider();
            // Bind a valid terrain shader before the streaming system selects
            // its first LOD. Generated/imported prefabs can serialize empty
            // material slots, which otherwise render the first visible frame
            // as a black terrain tile.
            ConfigureLighting();
            if (colliders == null) colliders = GetComponentsInChildren<Collider>(true);
            // Generated prefabs may have all LOD roots active in serialized
            // legacy data. Normalize immediately so there is never a frame of
            // overlapping LOD meshes before the streaming system initializes.
            SetLod(0);
            SetCollisionEnabled(!IsHlod);
        }

        public void Initialize(TerrainTileRecord record, TerrainChunkSettings chunkSettings, bool useHlod)
        {
            Initialize(record, chunkSettings, useHlod, transform.position);
        }

        public void Initialize(TerrainTileRecord record, TerrainChunkSettings chunkSettings, bool useHlod, Vector3 viewerPosition)
        {
            coordinate = record.coordinate;
            bounds = record.bounds;
            settings = chunkSettings;
            hlod = useHlod;
            // Existing baked prefabs may contain legacy skirts with duplicate
            // windings. Keep the terrain surface authoritative until those
            // prefabs are rebaked with the corrected skirt builder.
            DisableGeneratedSkirts();
            EnsureCollisionCollider();
            ConfigureLighting();
            colliders = GetComponentsInChildren<Collider>(true);
            SetCollisionEnabled(!useHlod);
            int initialLod = useHlod ? 3 : CalculateLod(viewerPosition);
            SetLod(initialLod);
            EnsureGrassForCurrentLod(record.bounds);
            UpdateGrassInteractionProximity();
        }

        public void UpdateLod(Vector3 cameraPosition)
        {
            if (settings == null)
            {
                SetLod(0);
                EnsureGrassForCurrentLod(bounds);
                UpdateGrassInteractionProximity();
                return;
            }

            float distance = Vector3.Distance(bounds.ClosestPoint(cameraPosition), cameraPosition);
            int lod = distance < settings.lod0Distance ? 0 : distance < settings.lod1Distance ? 1 : distance < settings.lod2Distance ? 2 : 3;
            SetLod(lod);
            EnsureGrassForCurrentLod(bounds);
            UpdateGrassInteractionProximity();
        }

        private int CalculateLod(Vector3 viewerPosition)
        {
            if (settings == null) return 0;
            float distance = Vector3.Distance(bounds.ClosestPoint(viewerPosition), viewerPosition);
            return distance < settings.lod1Distance ? 0 : distance < settings.lod2Distance ? 1 : distance < settings.lod3Distance ? 2 : 3;
        }

        private void EnsureGrassForCurrentLod(Bounds grassBounds)
        {
            InteractiveGrassTile grass = GetComponent<InteractiveGrassTile>();
            if (grass == null && currentLod < 3)
                grass = gameObject.AddComponent<InteractiveGrassTile>();
            if (grass == null) return;
            if (settings != null)
            {
                Shader.SetGlobalVector("_VoyageGrassWind", new Vector4(
                    settings.grassWindDirection.x, settings.grassWindDirection.y,
                    settings.grassWindSpeed, settings.grassWindGust));
                grass.baseColor = settings.grassBaseColor;
                grass.rootColor = settings.grassRootColor;
                grass.shadowColor = settings.grassShadowColor;
                grass.tipColor = settings.grassTipColor;
                grass.backsideColor = settings.grassBacksideColor;
                grass.fadeColor = settings.grassFadeColor;
                grass.macroScale = settings.grassMacroScale;
                grass.macroStrength = settings.grassMacroStrength;
                grass.alphaClip = settings.grassAlphaClip;
                grass.fadeStart = settings.grassFadeStart;
                grass.fadeEnd = settings.grassFadeEnd;
                grass.useIndirectRendering = settings.useIndirectGrass;
            }
            // Legacy prefabs do not contain the baked cluster asset. Configure
            // their runtime fallback from the current settings so they do not
            // silently fall back to the sparse component defaults. Keep the
            // fallback budget bounded because baking is the preferred path and
            // runtime sampling performs terrain raycasts.
            if (settings != null && grass.prototype == null && !grass.useLegacyBakedClusters)
            {
                grass.clusterSpacing = settings.grassClusterSpacing;
                grass.bladesPerCluster = settings.grassBladesPerCluster;
                grass.clusterRadius = settings.grassClusterRadius;
                grass.bladeHeight = settings.grassBladeHeight;
                grass.density = settings.grassDensity;
                // The old 6000 cap was useful for the first sparse prototype,
                // but made the streamed fallback visibly empty. Keep the
                // budget authored in settings while still protecting against
                // an accidental unbounded value.
                grass.runtimeClusterBudget = Mathf.Min(settings.grassClusterBudget, 24000);
                grass.clustersPerFrame = Mathf.Max(grass.clustersPerFrame, 768);
                grass.fullDensityBelowSlope = settings.grassFullDensityBelowSlope;
                grass.noGrassAboveSlope = settings.grassNoGrassAboveSlope;
            }
            grass.Initialize(grassBounds);
            grass.SetLod(currentLod);
        }

        private void UpdateGrassInteractionProximity()
        {
            InteractiveGrassTile grass = GetComponent<InteractiveGrassTile>();
            if (grass == null) return;
            GrassInteractionSystem interaction = GrassInteractionSystem.Instance;
            bool nearby = interaction == null || interaction.followTarget == null ||
                          Vector3.Distance(bounds.ClosestPoint(interaction.followTarget.position), interaction.followTarget.position) <= interaction.worldSize * 0.5f;
            grass.SetInteractionProximity(nearby);
        }

        public void SetCollisionEnabled(bool enabled)
        {
            if (collisionStateKnown && collisionState == enabled) return;
            if (colliders == null) colliders = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null) colliders[i].enabled = enabled;
            collisionState = enabled;
            collisionStateKnown = true;
        }

        private void EnsureCollisionCollider()
        {
            Transform collisionRoot = transform.Find("Collision");
            MeshCollider meshCollider = collisionRoot == null ? null : collisionRoot.GetComponent<MeshCollider>();
            GameObject root = collisionRoot == null ? new GameObject("Collision") : collisionRoot.gameObject;
            root.transform.SetParent(transform, false);
            meshCollider = root.GetComponent<MeshCollider>();
            if (meshCollider == null) meshCollider = root.AddComponent<MeshCollider>();
            Transform lod0 = transform.Find("LOD0");
            MeshFilter filter = lod0 == null ? null : lod0.GetComponent<MeshFilter>();
            // Always synchronize with LOD0. This prevents old/generated prefabs
            // from accidentally using a simplified visual mesh for physics.
            if (filter != null && filter.sharedMesh != null && meshCollider.sharedMesh != filter.sharedMesh)
            {
                meshCollider.sharedMesh = null;
                meshCollider.sharedMesh = filter.sharedMesh;
            }
            meshCollider.convex = false;
            meshCollider.isTrigger = false;
            meshCollider.cookingOptions = MeshColliderCookingOptions.EnableMeshCleaning |
                                           MeshColliderCookingOptions.WeldColocatedVertices |
                                           MeshColliderCookingOptions.CookForFasterSimulation;
            meshCollider.enabled = true;
        }

        private void ConfigureLighting()
        {
            MeshRenderer[] renderers = GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                ApplyTerrainPalette(renderer);
                if (!terrainShaderDiagnosticLogged && Application.isPlaying)
                {
                    Material diagnosticMaterial = renderer.sharedMaterial;
                    Debug.Log("TERRAIN SHADER // renderer=" + renderer.name +
                              " shader=" + (diagnosticMaterial == null || diagnosticMaterial.shader == null ? "NULL" : diagnosticMaterial.shader.name) +
                              " base=" + (diagnosticMaterial != null && diagnosticMaterial.HasProperty("_BaseColor") ? diagnosticMaterial.GetColor("_BaseColor").ToString() : "n/a"));
                    terrainShaderDiagnosticLogged = true;
                }
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                renderer.renderingLayerMask = 1u;
                renderer.allowOcclusionWhenDynamic = true;
            }
        }

        private static void ApplyTerrainPalette(MeshRenderer renderer)
        {
            Material[] materials = renderer.sharedMaterials;
            // Some imported/generated LOD renderers serialize an empty or
            // null material array. Leaving that slot empty renders the whole
            // terrain silhouette black in URP.
            if (materials == null || materials.Length == 0)
                materials = new Material[1];
            if (grasslandFallbackMaterial == null)
            {
                Shader shader = Shader.Find("Voyage/Terrain/Stylized");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                if (shader == null) return;
                grasslandFallbackMaterial = new Material(shader) { name = "Runtime Grassland Terrain Material" };
                // Keep the terrain under the warm meadow grass. A deep
                // green fallback makes every density gap read as a hole.
                // Keep the bare terrain in the same warm palette as grass so
                // streamed density gaps read as ground, never as black holes.
                Color baseColor = new Color(0.64f, 0.42f, 0.14f, 1f);
                Color shadowColor = new Color(0.48f, 0.33f, 0.12f, 1f);
                Color ridgeColor = new Color(0.78f, 0.56f, 0.22f, 1f);
                if (grasslandFallbackMaterial.HasProperty("_BaseColor")) grasslandFallbackMaterial.SetColor("_BaseColor", baseColor);
                if (grasslandFallbackMaterial.HasProperty("_Color")) grasslandFallbackMaterial.SetColor("_Color", baseColor);
                if (grasslandFallbackMaterial.HasProperty("_ShadowColor")) grasslandFallbackMaterial.SetColor("_ShadowColor", shadowColor);
                if (grasslandFallbackMaterial.HasProperty("_RidgeColor")) grasslandFallbackMaterial.SetColor("_RidgeColor", ridgeColor);
                if (grasslandFallbackMaterial.HasProperty("_MacroStrength")) grasslandFallbackMaterial.SetFloat("_MacroStrength", 0.22f);
                if (grasslandFallbackMaterial.HasProperty("_HeightTint")) grasslandFallbackMaterial.SetFloat("_HeightTint", 0.10f);
                if (grasslandFallbackMaterial.HasProperty("_Metallic")) grasslandFallbackMaterial.SetFloat("_Metallic", 0f);
                if (grasslandFallbackMaterial.HasProperty("_Smoothness")) grasslandFallbackMaterial.SetFloat("_Smoothness", 0.15f);
                grasslandFallbackMaterial.enableInstancing = true;
            }
            bool changed = false;
            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = grasslandFallbackMaterial;
                changed = true;
            }
            if (changed) renderer.sharedMaterials = materials;
        }

        private void DisableGeneratedSkirts()
        {
            Transform[] children = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                if (!children[i].name.Equals("Skirt", System.StringComparison.Ordinal)) continue;
                MeshRenderer renderer = children[i].GetComponent<MeshRenderer>();
                if (renderer != null) renderer.enabled = false;
            }
        }

        private void SetLod(int lod)
        {
            lod = Mathf.Clamp(lod, 0, 3);
            // UpdateLod is evaluated every frame for every loaded tile. Do
            // not deactivate/reactivate the same LOD hierarchy or reapply
            // lighting to every renderer unless the selected LOD actually
            // changed; that creates periodic render-thread spikes while the
            // vehicle is moving and makes its wheels appear to step.
            if (currentLod == lod) return;
            currentLod = lod;
            for (int i = 0; i < lodRoots.Length; i++)
                if (lodRoots[i] != null) lodRoots[i].SetActive(i == lod);
            InteractiveGrassTile grass = GetComponent<InteractiveGrassTile>();
            if (grass != null) grass.SetLod(lod);
        }
    }
}
