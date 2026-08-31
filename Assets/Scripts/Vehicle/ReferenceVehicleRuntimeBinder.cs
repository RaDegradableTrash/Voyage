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

        CarControl car = GetComponent<CarControl>();
        if (car == null) car = gameObject.AddComponent<CarControl>();
        StartCoroutine(ActivateAfterReferenceStart(car));
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
