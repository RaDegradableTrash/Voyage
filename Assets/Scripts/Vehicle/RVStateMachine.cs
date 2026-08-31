using UnityEngine;
using UnityEngine.InputSystem;

namespace RVSystem
{
    public enum RVState { Parked, Active }

    public sealed class RVStateMachine : MonoBehaviour
    {
        public RVState currentState = RVState.Parked;
        public RVController controller;
        public GameObject player;
        public Collider enterTrigger;
        public float throttle;
        public float steer;
        public bool braking;
        public float baseFuelConsumption = 0.5f;
        public float activeFuelConsumption = 1.5f;
        public float fuel = 100f;
        public static float SharedFuel { get; private set; } = 100f;
        RVCameraController cameraController;

        void Awake()
        {
            if (controller == null) controller = GetComponent<RVController>();
            cameraController = GetComponent<RVCameraController>();
            SharedFuel = fuel;
            enabled = currentState == RVState.Active;
        }

        void Update()
        {
            if (currentState != RVState.Active || controller == null) return;
            float vertical = Input.GetAxis("Vertical");
            float horizontal = Input.GetAxis("Horizontal");
            if (Keyboard.current != null)
            {
                vertical = Mathf.Abs(vertical) > 0.01f ? vertical : (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed ? 1f : 0f) - (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed ? 1f : 0f);
                horizontal = Mathf.Abs(horizontal) > 0.01f ? horizontal : (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed ? 1f : 0f) - (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed ? 1f : 0f);
            }
            throttle = Mathf.Clamp(vertical, -1f, 1f);
            steer = Mathf.Clamp(horizontal, -1f, 1f);
            braking = Input.GetKey(KeyCode.Space) || (Keyboard.current != null && Keyboard.current.spaceKey.isPressed);
            if (fuel > 0f)
            {
                fuel = Mathf.Max(0f, fuel - (baseFuelConsumption + Mathf.Abs(throttle) * activeFuelConsumption) * Time.deltaTime);
                SharedFuel = fuel;
            }
            else { throttle = 0f; braking = true; }
            controller.ApplyInputs(throttle, steer, braking);
            if (Input.GetKeyDown(KeyCode.E)) SetState(RVState.Parked);
        }

        public void SetState(RVState state)
        {
            currentState = state;
            enabled = state == RVState.Active;
            if (state == RVState.Parked)
            {
                if (controller != null) controller.StopVehicle();
                if (cameraController != null) cameraController.SetCamerasEnabled(false);
                if (player != null) player.SetActive(true);
            }
            else
            {
                if (cameraController != null) cameraController.SetCamerasEnabled(true);
                if (player != null) player.SetActive(false);
            }
        }
    }
}
