using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Voyage.Exploration
{
    // Cementery's designer-authored WorldObject events, with proximity selection.
    public class WorldInteractable : MonoBehaviour
    {
        public static readonly List<WorldInteractable> Active = new List<WorldInteractable>();
        public string displayName = "Object";
        public string prompt = "Interact";
        public bool interactable = true;
        public Vector3 anchorOffset = Vector3.up;
        public UnityEvent<GameObject> onInteract = new UnityEvent<GameObject>();
        public Vector3 Anchor => transform.TransformPoint(anchorOffset);
        protected virtual void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        protected virtual void OnDisable() => Active.Remove(this);
        public virtual void Interact(ExplorerPlayer player) => onInteract.Invoke(player.gameObject);
    }
}
