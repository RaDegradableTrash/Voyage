using UnityEngine;

public class WheelControl : MonoBehaviour
{
    public Transform wheelModel;

    [HideInInspector] public WheelCollider WheelCollider;

    // Create properties for the CarControl script
    // (You should enable/disable these via the 
    // Editor Inspector window)
    public bool steerable;
    public bool motorized;
    public bool isFrontLeft;
    public bool isFrontRight;

    private Transform _wheelModelTransform;
    private Vector3 _lastPosition;
    private Quaternion _lastRotation = Quaternion.identity;
    private bool _hasLastPose;

    // Start is called before the first frame update
    private void Start()
    {
        WheelCollider = GetComponent<WheelCollider>();
        _wheelModelTransform = wheelModel != null ? wheelModel.transform : null;

        if (WheelCollider == null || _wheelModelTransform == null)
        {
            enabled = false;
        }
    }

    // Update is called once per frame
    void Update()
    {
        // Get the Wheel collider's world pose values and
        // use them to set the wheel model's position and rotation
        WheelCollider.GetWorldPose(out Vector3 position, out Quaternion rotation);

        if (_hasLastPose &&
            (position - _lastPosition).sqrMagnitude < 0.000001f &&
            Quaternion.Angle(_lastRotation, rotation) < 0.01f)
        {
            return;
        }

        _wheelModelTransform.SetPositionAndRotation(position, rotation);
        _lastPosition = position;
        _lastRotation = rotation;
        _hasLastPose = true;
    }
}


