using UnityEngine;

/// <summary>
/// Explicit wheel reference layout for the modeled vehicle.
/// The four references are intentionally manual: no name sorting or axis
/// inference is used once this component is assigned to the vehicle prefab.
/// </summary>
[DisallowMultipleComponent]
public sealed class VehicleWheelMarkers : MonoBehaviour
{
    [Header("Model")]
    public Transform modelRoot;

    [Header("Manual wheel positions")]
    public Transform frontLeft;
    public Transform frontRight;
    public Transform rearLeft;
    public Transform rearRight;

    public bool IsComplete
    {
        get
        {
            return frontLeft != null && frontRight != null &&
                   rearLeft != null && rearRight != null;
        }
    }

    public Transform[] GetOrderedWheels()
    {
        return new[] { frontLeft, frontRight, rearLeft, rearRight };
    }

    void OnDrawGizmosSelected()
    {
        DrawMarker(frontLeft, Color.red, "FL");
        DrawMarker(frontRight, Color.red, "FR");
        DrawMarker(rearLeft, Color.cyan, "RL");
        DrawMarker(rearRight, Color.cyan, "RR");
    }

    static void DrawMarker(Transform marker, Color color, string label)
    {
        if (marker == null) return;
        Gizmos.color = color;
        Gizmos.DrawWireSphere(marker.position, 0.12f);
        Gizmos.DrawLine(marker.position - marker.up * 0.28f, marker.position + marker.up * 0.28f);
        Gizmos.DrawLine(marker.position - marker.right * 0.22f, marker.position + marker.right * 0.22f);
        Gizmos.DrawLine(marker.position - marker.forward * 0.22f, marker.position + marker.forward * 0.22f);
    }
}
