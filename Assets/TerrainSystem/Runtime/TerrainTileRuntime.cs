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
        private int currentLod = -1;
        private TerrainChunkSettings settings;
        private bool collisionStateKnown;
        private bool collisionState;

        public Vector2Int Coordinate => coordinate;
        public Bounds Bounds => bounds;
        public int CurrentLod => currentLod;
        public bool IsHlod => currentLod >= 3;
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
            if (colliders == null) colliders = GetComponentsInChildren<Collider>(true);
            SetCollisionEnabled(!IsHlod);
        }

        public void Initialize(TerrainTileRecord record, TerrainChunkSettings chunkSettings, bool useHlod)
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
            SetLod(useHlod ? 3 : 0);
        }

        public void UpdateLod(Vector3 cameraPosition)
        {
            if (settings == null)
            {
                SetLod(3);
                return;
            }

            float distance = Vector3.Distance(bounds.ClosestPoint(cameraPosition), cameraPosition);
            int lod = distance < settings.lod1Distance ? 0 : distance < settings.lod2Distance ? 1 : distance < settings.lod3Distance ? 2 : 3;
            SetLod(lod);
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
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                renderer.renderingLayerMask = 1u;
                renderer.allowOcclusionWhenDynamic = true;
            }
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
            ConfigureLighting();
        }
    }
}
