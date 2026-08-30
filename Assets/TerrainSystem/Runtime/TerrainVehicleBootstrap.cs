using System.Collections;
using UnityEngine;

namespace Voyage.TerrainSystem
{
    [DisallowMultipleComponent]
    public sealed class TerrainVehicleBootstrap : MonoBehaviour
    {
        [SerializeField] private GameObject vehiclePrefab;
        [SerializeField] private TerrainSourceAsset source = null;
        [SerializeField] private Camera vehicleCamera;
        [SerializeField] private Vector3 spawnOffset = new Vector3(0f, 8f, 0f);
        [SerializeField] private float vehicleSpawnHeight = 3.2f;
        [SerializeField] private bool spawnOnStart = true;
        private GameObject vehicleInstance;

        public GameObject VehicleInstance => vehicleInstance;

        public void SetSource(TerrainSourceAsset value)
        {
            source = value;
        }

        private IEnumerator Start()
        {
            // TerrainSystemDemo is a standalone entry point and does not run
            // DrivingCore.Start(). Keep its physics clock aligned with the
            // render cadence so Rigidbody interpolation and wheel animation do
            // not alternate between uneven fixed-step intervals. Use two
            // physics samples per rendered frame for smoother tire contact.
            Application.targetFrameRate = 60;
            // VSync overrides targetFrameRate on high-refresh displays. Keep
            // rendering on the same cadence as the 60Hz vehicle solver.
            QualitySettings.vSyncCount = 0;
            Time.fixedDeltaTime = 1f / 120f;
            Time.maximumDeltaTime = 0.1f;

            if (source == null) source = Resources.Load<TerrainSourceAsset>("TerrainSystem/TerrainSource");
            if (!spawnOnStart) yield break;

            // Terrain collision is streamed asynchronously. Do not drop the
            // vehicle through the world while the first nearby tile is cooking.
            float wait = 0f;
            RaycastHit groundHit;
            while (wait < 6f && !TryFindSpawnGround(out groundHit))
            {
                wait += Time.unscaledDeltaTime;
                yield return null;
            }
            SpawnVehicle();
        }

        public void SpawnVehicle()
        {
            if (vehicleInstance != null) return;
            if (vehiclePrefab == null) vehiclePrefab = Resources.Load<GameObject>("Prefabs/PlayerCar");
            if (vehiclePrefab == null)
            {
                Debug.LogError("TerrainSystem vehicle bootstrap: Resources/Prefabs/PlayerCar was not found.");
                return;
            }

            Vector3 spawnBase = source != null ? source.sourceBounds.center : transform.position;
            Vector3 spawn = spawnBase + new Vector3(spawnOffset.x, vehicleSpawnHeight, spawnOffset.z);
            RaycastHit groundHit;
            if (TryFindSpawnGround(out groundHit))
            {
                // The FBX wheel centers sit roughly two metres below the
                // vehicle root. An 8m lift lets a streamed mesh disappear
                // between physics ticks before the suspension can catch it.
                spawn = groundHit.point + Vector3.up * Mathf.Clamp(vehicleSpawnHeight, 2.2f, 4.5f);
                Debug.Log("TERRAIN VEHICLE SPAWN // ground=" + groundHit.point.ToString("F2") + " spawn=" + spawn.ToString("F2"));
            }
            vehicleInstance = Instantiate(vehiclePrefab, spawn, Quaternion.identity);
            vehicleInstance.name = "TerrainSystem Player Vehicle";
            PlayerCar car = vehicleInstance.GetComponent<PlayerCar>();
            if (car != null) car.BuildVisuals(CreateMaterial(new Color(0.08f, 0.3f, 0.75f)), CreateMaterial(new Color(0.15f, 0.7f, 0.85f)), CreateMaterial(new Color(0.8f, 0.05f, 0.04f)), CreateMaterial(new Color(1f, 0.75f, 0.25f)));

            if (vehicleCamera == null) vehicleCamera = Camera.main;
            if (vehicleCamera == null) vehicleCamera = FindAnyObjectByType<Camera>();
            if (vehicleCamera != null && vehicleCamera.transform.IsChildOf(vehicleInstance.transform))
            {
                Debug.LogWarning("TERRAIN CAMERA // ignored embedded vehicle camera " + vehicleCamera.name + "; searching scene camera");
                vehicleCamera = null;
                Camera[] cameras = FindObjectsByType<Camera>(FindObjectsInactive.Include);
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (cameras[i] == null || cameras[i].transform.IsChildOf(vehicleInstance.transform)) continue;
                    vehicleCamera = cameras[i];
                    break;
                }
            }
            if (vehicleCamera != null)
            {
                // TerrainSystemDemo must use the same camera behavior as the original
                // generated vehicle. The simplified TerrainVehicleCamera is intentionally
                // disabled here so it cannot override the original orbit/hood/zoom logic.
                TerrainVehicleCamera simplifiedCamera = vehicleCamera.GetComponent<TerrainVehicleCamera>();
                if (simplifiedCamera != null) simplifiedCamera.enabled = false;

                FollowCamera follow = vehicleCamera.GetComponent<FollowCamera>();
                if (follow == null) follow = vehicleCamera.gameObject.AddComponent<FollowCamera>();
                follow.enabled = true;
                follow.SetOnFoot(false);
                follow.ApplyOriginalVehicleFraming();

                // Start from the original generated vehicle-camera framing so the first
                // frame is not spent looking from the scene's placeholder camera pose.
                Vector3 cameraOffset = new Vector3(0f, 7.3f, -10.5f);
                Quaternion vehicleYaw = Quaternion.Euler(0f, vehicleInstance.transform.eulerAngles.y, 0f);
                Vector3 cameraPosition = vehicleInstance.transform.position + vehicleYaw * cameraOffset;
                Vector3 cameraLookPoint = vehicleInstance.transform.position + Vector3.up * 0.8f;
                vehicleCamera.transform.SetPositionAndRotation(cameraPosition, Quaternion.LookRotation(cameraLookPoint - cameraPosition, Vector3.up));
                Debug.Log("TERRAIN CAMERA // using original FollowCamera on " + vehicleCamera.name + " for TerrainSystem Player Vehicle");
                follow.SetTarget(vehicleInstance.transform);
            }
            else
            {
                Debug.LogError("TERRAIN CAMERA // no camera found for TerrainSystem vehicle");
            }
        }

        private bool TryFindSpawnGround(out RaycastHit hit)
        {
            Vector3 center = source != null ? source.sourceBounds.center : transform.position;
            Vector3 origin = center + Vector3.up * 1000f;
            return Physics.Raycast(origin, Vector3.down, out hit, 2000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        }

        private static Material CreateMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Material material = new Material(shader) { color = color };
            return material;
        }
    }
}
