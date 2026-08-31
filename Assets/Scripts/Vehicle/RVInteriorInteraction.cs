using System.Collections.Generic;
using UnityEngine;

namespace RVSystem
{
    public sealed class RVInteriorInteraction : MonoBehaviour
    {
        public Transform playerParent;
        public string playerTag = "Player";
        readonly Dictionary<Collider, Transform> originalParents = new Dictionary<Collider, Transform>();

        void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag(playerTag) || playerParent == null) return;
            if (!originalParents.ContainsKey(other)) originalParents.Add(other, other.transform.parent);
            other.transform.SetParent(playerParent, true);
        }
        void OnTriggerExit(Collider other)
        {
            if (!other.CompareTag(playerTag)) return;
            if (originalParents.TryGetValue(other, out Transform original))
            {
                other.transform.SetParent(original, true);
                originalParents.Remove(other);
            }
        }
    }
}
