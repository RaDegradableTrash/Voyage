using UnityEngine;

namespace Voyage.Environment
{
    /// <summary>Routes actual water-trigger contacts to the local ripple field.</summary>
    [DisallowMultipleComponent]
    public sealed class WaterCollisionRelay : MonoBehaviour
    {
        public SeaLevelWaterSystem Owner { get; set; }

        void OnTriggerEnter(Collider other)
        {
            if (Owner != null) Owner.RegisterCollision(other, false);
        }

        void OnTriggerStay(Collider other)
        {
            if (Owner != null) Owner.RegisterCollision(other, true);
        }
    }
}
