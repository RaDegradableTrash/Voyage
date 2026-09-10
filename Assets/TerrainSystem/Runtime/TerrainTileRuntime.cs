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
        private static PhysicsMaterial terrainContactMaterial;
        private static bool terrainShaderDiagnosticLogged;
        private int currentLod = -1;
        private TerrainChunkSettings settings;
        private bool collisionStateKnown;
        private bool collisionState;
        private InteractiveGrassTile configuredGrass;
        private bool paintedGrassResolved;
        private GrassFlow.GrassFlowPatch generatedPatch;
        // Runtime fallback patches are generated when an authored GrassFlow
        // asset is missing. Keep them by tile coordinate so streaming the
        // same area again does not repeat raycasts, texture uploads and GPU
        // buffer creation after every unload/reload cycle.
        private static readonly System.Collections.Generic.Dictionary<Vector2Int, GrassFlow.GrassFlowPatch> runtimePatchCache =
            new System.Collections.Generic.Dictionary<Vector2Int, GrassFlow.GrassFlowPatch>();
        private MeshRenderer[][] lodRenderers;
        private MaterialPropertyBlock lodProperties;
        private int outgoingLod = -1;
        private float lodTransition = 1f;
        private const float LodTransitionSeconds = 0.4f;
        public const MeshColliderCookingOptions CollisionCookingOptions =
            MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices |
            MeshColliderCookingOptions.CookForFasterSimulation;

        public Vector2Int Coordinate => coordinate;
        public Bounds Bounds => bounds;
        public int CurrentLod => currentLod;
        public bool IsHlod => currentLod >= 3;
        public bool GrassBuildFinished
        {
            get
            {
                if (settings != null && settings.usePaintedGrass) return true;
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
            lodRenderers = new MeshRenderer[lodRoots.Length][];
            for (int i = 0; i < lodRoots.Length; i++)
                lodRenderers[i] = lodRoots[i] == null ? new MeshRenderer[0] : lodRoots[i].GetComponentsInChildren<MeshRenderer>(true);
            lodProperties = new MaterialPropertyBlock();
            if (colliders == null) colliders = GetComponentsInChildren<Collider>(true);
            // Generated prefabs may have all LOD roots active in serialized
            // legacy data. Normalize immediately so there is never a frame of
            // overlapping LOD meshes before the streaming system initializes.
            SetLod(0);
            // Streaming activates collision separately, within its frame budget.
            SetCollisionEnabled(false);
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
            // The baked LOD0 edges are already snapped to shared heights. Do
            // not add a second overlapping seam surface: WheelCollider can
            // alternate between coplanar contacts and create its own chatter.
            if (useHlod) SetCollisionEnabled(false);
            int initialLod = useHlod ? 3 : CalculateLod(viewerPosition);
            SetLod(initialLod, true);
            // Grass preparation is scheduled separately from prefab creation.
            UpdateGrassInteractionProximity();
        }

        public void UpdateLod(Vector3 cameraPosition)
        {
            UpdateVisualLod(cameraPosition);
            InitializeGrass();
        }

        public void UpdateVisualLod(Vector3 cameraPosition)
        {
            if (settings == null)
            {
                SetLod(0);
                UpdateGrassInteractionProximity();
                return;
            }

            int lod = CalculateLod(cameraPosition);
            float distance = Vector3.Distance(bounds.ClosestPoint(cameraPosition), cameraPosition);
            // A small dead band prevents back-and-forth switches at a threshold.
            if (currentLod >= 0 && lod > currentLod &&
                distance < settings.GetLodDistance(currentLod) + 10f) lod = currentLod;
            else if (currentLod > 0 && lod < currentLod &&
                distance > settings.GetLodDistance(currentLod - 1) - 10f) lod = currentLod;
            SetLod(lod);
            UpdateLodTransition();
            UpdateGrassInteractionProximity();
        }

        public bool WantsCollision(Vector3 viewerPosition, TerrainChunkSettings chunkSettings)
        {
            if (!chunkSettings.enableCollisionWhenLoaded) return false;
            float radius = Mathf.Max(chunkSettings.tileSize * chunkSettings.collisionRadius,
                                     chunkSettings.grassFadeEnd + 40f);
            // Evaluate distance continuously, and keep a retreat margin. A cell
            // boundary no longer toggles an entire row of static colliders.
            if (CollisionEnabled) radius += chunkSettings.tileSize * 0.5f;
            return bounds.SqrDistance(viewerPosition) <= radius * radius;
        }

        public bool NeedsGrassInitialization => settings != null && settings.usePaintedGrass
            ? !paintedGrassResolved && currentLod < 2
            : currentLod < 3 && configuredGrass == null && CollisionEnabled;

        public void InitializeGrass()
        {
            if (settings != null && settings.usePaintedGrass)
            {
                if (currentLod >= 2) return;
                if (paintedGrassResolved) return;
                paintedGrassResolved = true;
                var legacy = GetComponent<InteractiveGrassTile>();
                if (legacy != null) legacy.enabled = false;
                if (Application.isPlaying)
                {
                    StartCoroutine(LoadPaintedGrass());
                    return;
                }
                var patch = Resources.Load<GrassFlow.GrassFlowPatch>($"GrassFlow/Tiles/Grass_{coordinate.x}_{coordinate.y}");
                if (patch != null)
                {
                    var renderer = GetComponent<GrassFlow.GrassFlowRenderer>();
                    if (renderer == null) renderer = gameObject.AddComponent<GrassFlow.GrassFlowRenderer>();
                    renderer.patch = patch;
                }
                else if (Application.isPlaying && lodRoots[0] != null)
                    StartCoroutine(StreamedGrassSurface.Build(lodRoots[0].GetComponentsInChildren<MeshFilter>(true), bounds, value =>
                    {
                        generatedPatch = value;
                        var renderer = GetComponent<GrassFlow.GrassFlowRenderer>();
                        if (renderer == null) renderer = gameObject.AddComponent<GrassFlow.GrassFlowRenderer>();
                        renderer.patch = value;
                    }));
                return;
            }
            if (NeedsGrassInitialization) EnsureGrassForCurrentLod(bounds);
        }

        private void OnDestroy()
        {
            // Cached fallback patches intentionally outlive streamed tile
            // instances and are reused by coordinate on the next load.
        }

        private System.Collections.IEnumerator LoadPaintedGrass()
        {
            GrassFlow.GrassFlowPatch cached;
            if (runtimePatchCache.TryGetValue(coordinate, out cached) && cached != null)
            {
                AssignGrassPatch(cached);
                yield break;
            }
            var request = Resources.LoadAsync<GrassFlow.GrassFlowPatch>($"GrassFlow/Tiles/Grass_{coordinate.x}_{coordinate.y}");
            yield return request;
            var patch = request.asset as GrassFlow.GrassFlowPatch;
            if (patch != null)
            {
                AssignGrassPatch(patch);
            }
            else if (lodRoots[0] != null)
                yield return StreamedGrassSurface.Build(lodRoots[0].GetComponentsInChildren<MeshFilter>(true), bounds, value =>
                {
                    generatedPatch = value;
                    runtimePatchCache[coordinate] = value;
                    AssignGrassPatch(value);
                });
        }

        private void AssignGrassPatch(GrassFlow.GrassFlowPatch patch)
        {
            if (patch == null) return;
            generatedPatch = patch;
            var renderer = GetComponent<GrassFlow.GrassFlowRenderer>();
            if (renderer == null) renderer = gameObject.AddComponent<GrassFlow.GrassFlowRenderer>();
            renderer.patch = patch;
        }

        private int CalculateLod(Vector3 viewerPosition)
        {
            if (settings == null) return 0;
            float distance = Vector3.Distance(bounds.ClosestPoint(viewerPosition), viewerPosition);
            return distance < settings.lod0Distance ? 0 : distance < settings.lod1Distance ? 1 : distance < settings.lod2Distance ? 2 : 3;
        }

        private void EnsureGrassForCurrentLod(Bounds grassBounds)
        {
            InteractiveGrassTile grass = GetComponent<InteractiveGrassTile>();
            // Only runtime sampling needs physics; authored grass remains
            // drawable even when a tile's collision is disabled.
            if (!CollisionEnabled && (grass == null || (grass.bakedClusters == null && grass.bakedMesh == null))) return;
            if (grass == null && currentLod < 3)
                grass = gameObject.AddComponent<InteractiveGrassTile>();
            if (grass == null) return;
            if (configuredGrass == grass)
            {
                grass.SetLod(currentLod);
                return;
            }
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
                // A 24k-cluster cap creates large structured/visible buffers
                // for every streamed tile. Once several tiles are near the
                // camera, those allocations and culling dispatches become a
                // repeating GPU/GC spike. The shared four-blade cluster keeps
                // 12k placements visually dense while halving that pressure.
                grass.runtimeClusterBudget = Mathf.Min(settings.grassClusterBudget, 12000);
                grass.clustersPerFrame = Mathf.Clamp(grass.clustersPerFrame, 32, 96);
                grass.fullDensityBelowSlope = settings.grassFullDensityBelowSlope;
                grass.noGrassAboveSlope = settings.grassNoGrassAboveSlope;
            }
            grass.Initialize(grassBounds);
            configuredGrass = grass;
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
            // Configure before assigning a mesh, otherwise Unity cooks it once
            // with default flags and again after switching to the worker's flags.
            meshCollider.enabled = false;
            meshCollider.convex = false;
            meshCollider.isTrigger = false;
            if (terrainContactMaterial == null)
            {
                terrainContactMaterial = new PhysicsMaterial("Voyage Terrain Contact")
                {
                    staticFriction = 0.72f,
                    dynamicFriction = 0.58f,
                    bounciness = 0f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounceCombine = PhysicsMaterialCombine.Minimum
                };
            }
            // Keep the contact response identical on both sides of a streamed
            // seam so WheelCollider friction cannot jump at the boundary.
            meshCollider.material = terrainContactMaterial;
            meshCollider.contactOffset = 0.04f;
            if (meshCollider.cookingOptions != CollisionCookingOptions)
                meshCollider.cookingOptions = CollisionCookingOptions;
            Transform lod0 = transform.Find("LOD0");
            MeshFilter filter = lod0 == null ? null : lod0.GetComponent<MeshFilter>();
            // Always synchronize with LOD0. This prevents old/generated prefabs
            // from accidentally using a simplified visual mesh for physics.
            if (filter != null && filter.sharedMesh != null && meshCollider.sharedMesh != filter.sharedMesh)
            {
                meshCollider.sharedMesh = null;
                meshCollider.sharedMesh = filter.sharedMesh;
            }
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
                if (materials[i] == grasslandFallbackMaterial) continue;
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

        private void SetLod(int lod, bool immediate = false)
        {
            lod = Mathf.Clamp(lod, 0, 3);
            // UpdateLod is evaluated every frame for every loaded tile. Do
            // not deactivate/reactivate the same LOD hierarchy or reapply
            // lighting to every renderer unless the selected LOD actually
            // changed; that creates periodic render-thread spikes while the
            // vehicle is moving and makes its wheels appear to step.
            if (currentLod == lod && !immediate) return;
            // Cross-fading keeps two complete terrain hierarchies active and
            // writes a property block for every renderer during the fade.
            // With streamed tiles this creates a synchronized render spike
            // when several tiles cross an LOD boundary together. Runtime
            // terrain uses an immediate single-LOD switch; the editor still
            // keeps authored cross-fades for visual inspection.
            bool fade = !immediate && !Application.isPlaying && settings != null && settings.useCrossFade && currentLod >= 0;
            // Finish the previous pair before starting a new transition.
            outgoingLod = fade ? currentLod : -1;
            lodTransition = fade ? 0f : 1f;
            currentLod = lod;
            for (int i = 0; i < lodRoots.Length; i++)
                if (lodRoots[i] != null) lodRoots[i].SetActive(i == lod || i == outgoingLod);
            ApplyLodFade();
            InteractiveGrassTile grass = GetComponent<InteractiveGrassTile>();
            if (grass != null) grass.SetLod(lod);
        }

        private void UpdateLodTransition()
        {
            if (outgoingLod < 0) return;
            lodTransition = Mathf.MoveTowards(lodTransition, 1f, Time.deltaTime / LodTransitionSeconds);
            ApplyLodFade();
            if (lodTransition < 1f) return;
            if (lodRoots[outgoingLod] != null) lodRoots[outgoingLod].SetActive(false);
            outgoingLod = -1;
        }

        private void ApplyLodFade()
        {
            if (lodRenderers == null) return;
            for (int i = 0; i < lodRenderers.Length; i++)
            {
                if (i != currentLod && i != outgoingLod) continue;
                foreach (MeshRenderer renderer in lodRenderers[i])
                {
                    renderer.GetPropertyBlock(lodProperties);
                    lodProperties.SetFloat("_TerrainLodProgress", lodTransition);
                    lodProperties.SetFloat("_TerrainLodOutgoing", i == outgoingLod ? 1f : 0f);
                    renderer.SetPropertyBlock(lodProperties);
                }
            }
        }
    }
}
