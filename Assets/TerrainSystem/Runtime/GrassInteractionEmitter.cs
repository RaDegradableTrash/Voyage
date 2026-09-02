using UnityEngine;

namespace Voyage.TerrainSystem
{
    /// <summary>
    /// Optional broad interaction source for moving bodies that do not have
    /// WheelColliders. Add it to a character, animal, prop, or vehicle body
    /// to create a continuous pressed-grass segment while it moves.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GrassInteractionEmitter : MonoBehaviour
    {
        [Min(0.05f)] public float radius = 0.8f;
        [Min(0f)] public float minimumTravel = 0.015f;

        void OnEnable()
        {
            GrassInteractionSystem system = GrassInteractionSystem.Instance;
            if (system != null) system.RegisterEmitter(transform, radius, minimumTravel);
        }

        void OnDisable()
        {
            GrassInteractionSystem system = GrassInteractionSystem.Instance;
            if (system != null) system.UnregisterEmitter(transform);
        }

        void OnValidate()
        {
            radius = Mathf.Max(0.05f, radius);
            minimumTravel = Mathf.Max(0f, minimumTravel);
        }
    }
}
