using UnityEngine;

namespace Voyage.TerrainSystem
{
    /// <summary>Original generated high-rear vehicle-camera framing.</summary>
    [DefaultExecutionOrder(10000)]
    public sealed class TerrainVehicleCamera : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private Vector3 offset = new Vector3(0f, 7.5f, -11f);
        [SerializeField] private float positionSmooth = 6f;
        [SerializeField] private float lookHeight = 1f;
        bool initialized;
        bool reportedPosition;

        public void SetTarget(Transform value)
        {
            target = value;
            initialized = false;
            FollowCamera otherCamera = GetComponent<FollowCamera>();
            if (otherCamera != null) otherCamera.enabled = false;
            Debug.Log("TERRAIN CAMERA // generated follow offset " + offset + " target " + (target != null ? target.name : "NULL"));
        }

        private void LateUpdate()
        {
            if (target == null) return;
            Vector3 desired = target.TransformPoint(offset);
            Vector3 lookPoint = target.position + Vector3.up * lookHeight;
            Quaternion rotation = Quaternion.LookRotation(lookPoint - desired, Vector3.up);
            if (!initialized)
            {
                transform.SetPositionAndRotation(desired, rotation);
                initialized = true;
                if (!reportedPosition)
                {
                    Debug.Log("TERRAIN CAMERA // positioned " + transform.position + " looking at " + lookPoint);
                    reportedPosition = true;
                }
                return;
            }
            float positionBlend = 1f - Mathf.Exp(-positionSmooth * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, desired, positionBlend);
            transform.rotation = Quaternion.Slerp(transform.rotation, rotation, positionBlend);
        }
    }
}
