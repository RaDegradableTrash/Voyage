using UnityEngine;

namespace RVSystem
{
    public sealed class RVCameraController : MonoBehaviour
    {
        public GameObject interiorCamera;
        public GameObject exteriorCamera;
        public KeyCode switchKey = KeyCode.C;
        bool interiorActive;
        bool camerasEnabled;
        FollowCamera followCamera;

        void Awake() { followCamera = FindAnyObjectByType<FollowCamera>(); }
        void Update()
        {
            if (camerasEnabled && Input.GetKeyDown(switchKey)) SwitchPerspective();
        }
        public void SwitchPerspective()
        {
            if (interiorCamera != null && exteriorCamera != null) SetInteriorActive(!interiorActive);
            else if (followCamera != null) followCamera.ToggleVehicleView();
        }
        public void SetCamerasEnabled(bool enabled)
        {
            camerasEnabled = enabled;
            if (interiorCamera != null) interiorCamera.SetActive(enabled && interiorActive);
            if (exteriorCamera != null) exteriorCamera.SetActive(enabled && !interiorActive);
        }
        public void SetInteriorActive(bool active)
        {
            interiorActive = active;
            if (interiorCamera != null) interiorCamera.SetActive(camerasEnabled && active);
            if (exteriorCamera != null) exteriorCamera.SetActive(camerasEnabled && !active);
        }
    }
}
