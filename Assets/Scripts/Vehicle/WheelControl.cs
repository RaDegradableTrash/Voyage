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
    private Quaternion _modelRotationOffset = Quaternion.identity;

    public float WorldRadius => WheelCollider == null ? 0.35f : WheelCollider.radius *
        Mathf.Max(Mathf.Abs(WheelCollider.transform.lossyScale.y), Mathf.Abs(WheelCollider.transform.lossyScale.z));

    public void BindCollider(WheelCollider collider)
    {
        WheelCollider = collider;
        _wheelModelTransform = wheelModel;
        if (_wheelModelTransform != null)
            _modelRotationOffset = Quaternion.Inverse(collider.transform.rotation) * _wheelModelTransform.rotation;
        _hasLastPose = false;
    }

    // Start is called before the first frame update
    private void Start()
    {
        if (WheelCollider == null) WheelCollider = GetComponent<WheelCollider>();
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
        rotation *= _modelRotationOffset;

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

