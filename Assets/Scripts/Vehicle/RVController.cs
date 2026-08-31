using System.Collections.Generic;
using UnityEngine;

namespace RVSystem
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed class RVController : MonoBehaviour
    {
        [System.Serializable]
        public class WheelPair
        {
            public WheelCollider collider;
            public Transform mesh;
            public bool isSteer;
            public bool isDrive;
            [HideInInspector] public Quaternion visualRotationOffset = Quaternion.identity;
        }

        public List<WheelPair> wheels = new List<WheelPair>();
        public Transform centerOfMass;
        public float motorTorque = 2500f;
        public float brakeTorque = 5000f;
        public float maxSteerAngle = 35f;
        PlayerCar playerCar;
        Rigidbody body;

        void Awake()
        {
            body = GetComponent<Rigidbody>();
            playerCar = GetComponent<PlayerCar>();
            ConfigureCenterOfMass();
        }

        void Start() { AutoBindWheels(); }

        [ContextMenu("Auto Bind Wheels")]
        public void AutoBindWheels()
        {
            if (wheels.Count > 0) return;
            Transform bodyMesh = transform.Find("UVBodyMesh");
            if (bodyMesh == null) bodyMesh = transform;
            string[] suffixes = { "L1", "L2", "L3", "R1", "R2", "R3" };
            WheelCollider[] colliders = GetComponentsInChildren<WheelCollider>(true);
            Transform[] meshes = bodyMesh.GetComponentsInChildren<Transform>(true);
            foreach (string suffix in suffixes)
            {
                WheelCollider wheelCollider = null;
                Transform mesh = null;
                foreach (WheelCollider candidate in colliders)
                    if (candidate.name.EndsWith(suffix) || candidate.name.Contains("WheelCollider" + suffix)) { wheelCollider = candidate; break; }
                foreach (Transform candidate in meshes)
                    if (candidate.name == "Wheel" + suffix) { mesh = candidate; break; }
                if (wheelCollider == null || mesh == null) continue;
                wheels.Add(new WheelPair { collider = wheelCollider, mesh = mesh,
                    isSteer = suffix.EndsWith("1"), isDrive = !suffix.EndsWith("1"),
                    visualRotationOffset = Quaternion.Inverse(wheelCollider.transform.rotation) * mesh.rotation });
            }
        }

        void ConfigureCenterOfMass()
        {
            if (body != null) body.centerOfMass = centerOfMass != null ? centerOfMass.localPosition : new Vector3(0f, -0.5f, 0f);
        }

        void Update()
        {
            foreach (WheelPair pair in wheels)
            {
                if (pair.collider == null || pair.mesh == null) continue;
                pair.collider.GetWorldPose(out Vector3 position, out Quaternion rotation);
                pair.mesh.SetPositionAndRotation(position, rotation * pair.visualRotationOffset);
            }
        }

        public void ApplyInputs(float throttle, float steer, bool braking)
        {
            if (playerCar != null)
            {
                playerCar.ApplyRVInputs(throttle, steer, braking);
                return;
            }
            foreach (WheelPair pair in wheels)
            {
                if (pair.collider == null) continue;
                if (pair.isDrive) pair.collider.motorTorque = throttle * motorTorque;
                if (pair.isSteer) pair.collider.steerAngle = steer * maxSteerAngle;
                pair.collider.brakeTorque = braking ? brakeTorque : 0f;
            }
        }

        public void StopVehicle()
        {
            if (playerCar != null) { playerCar.StopVehicle(); return; }
            foreach (WheelPair pair in wheels)
                if (pair.collider != null) { pair.collider.motorTorque = 0f; pair.collider.brakeTorque = brakeTorque; }
        }
    }
}
