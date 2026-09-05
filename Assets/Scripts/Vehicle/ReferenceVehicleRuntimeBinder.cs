using UnityEngine;

/// <summary>
/// Binds the compiled RVcode scripts to the Cementery RV1.0
/// prefab. The prefab already contains the authoritative six WheelColliders,
/// suspension values, chassis collider and wheel positions.
/// </summary>
[DisallowMultipleComponent]
public sealed class ReferenceVehicleRuntimeBinder : MonoBehaviour
{
    bool bound;

    public void Bind()
    {
        if (bound) return;
        bound = true;

        DisableEmbeddedCameras();
        NormalizeWheelPhysics();

        CarControl car = GetComponent<CarControl>();
        if (car == null) car = gameObject.AddComponent<CarControl>();
        StartCoroutine(ActivateAfterReferenceStart(car));
    }

    void NormalizeWheelPhysics()
    {
        WheelControl[] wheels = GetComponentsInChildren<WheelControl>(true);
        Transform physicsRoot = new GameObject("Runtime Wheel Physics").transform;
        physicsRoot.SetParent(transform, false);
        foreach (WheelControl wheel in wheels)
        {
            WheelCollider collider = wheel.GetComponent<WheelCollider>();
            if (collider == null) continue;
            // The imported chassis has a 100x scale and a -90 degree X
            // rotation. Its wheel nodes cannot supply a suspension frame:
            // WheelCollider requires local Y up and local Z rolling forward.
            Vector3 scale = collider.transform.lossyScale;
            float worldRadius = collider.radius * Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            float worldTravel = collider.suspensionDistance * Mathf.Abs(scale.y);
            float worldForceOffset = collider.forceAppPointDistance * Mathf.Abs(scale.y);
            Vector3 worldCenter = collider.transform.TransformPoint(collider.center);
            collider.transform.SetParent(physicsRoot, true);
            collider.transform.localRotation = Quaternion.identity;
            collider.transform.localScale = Vector3.one;
            collider.transform.position = worldCenter;
            collider.center = Vector3.zero;
            Vector3 physicsScale = collider.transform.lossyScale;
            collider.radius = worldRadius / Mathf.Max(0.001f, Mathf.Max(Mathf.Abs(physicsScale.y), Mathf.Abs(physicsScale.z)));
            collider.suspensionDistance = worldTravel / Mathf.Max(0.001f, Mathf.Abs(physicsScale.y));
            collider.forceAppPointDistance = worldForceOffset / Mathf.Max(0.001f, Mathf.Abs(physicsScale.y));
            wheel.BindCollider(collider);
        }
    }

    System.Collections.IEnumerator ActivateAfterReferenceStart(CarControl car)
    {
        yield return null;
        StartProcedure start = GetComponent<StartProcedure>();
        if (start != null) start.ForceStartVehicle();
        car.ActiveControl = true;
        car.SetElectricalPower(true);
        car.SetEngineOn(true);
        car.SetGear(CarControl.GearMode.Drive);
    }

    void DisableEmbeddedCameras()
    {
        Camera[] cameras = GetComponentsInChildren<Camera>(true);
        for (int i = 0; i < cameras.Length; i++)
        {
            cameras[i].enabled = false;
            AudioListener listener = cameras[i].GetComponent<AudioListener>();
            if (listener != null) listener.enabled = false;
        }

        AudioListener[] listeners = GetComponentsInChildren<AudioListener>(true);
        for (int i = 0; i < listeners.Length; i++) listeners[i].enabled = false;

        // The reference prefab also contains CockpitCam. In third-person
        // mode its exit path would continuously set CarControl.ActiveControl
        // back to false after the binder enables vehicle control.
        MonoBehaviour[] behaviours = GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] != null && behaviours[i].GetType().Name == "CockpitCam")
                behaviours[i].enabled = false;
        }
    }

}
