using UnityEngine;

namespace Voyage.Exploration
{
    public sealed class NpcActor : WorldInteractable
    {
        [TextArea] public string[] dialogue = { "Hello, traveller.", "There is a long road ahead. Take your time." };
        public bool facePlayer = true;
        int line;
        public override void Interact(ExplorerPlayer player)
        {
            if (facePlayer)
            {
                Vector3 direction = Vector3.ProjectOnPlane(player.transform.position-transform.position,Vector3.up);
                if (direction.sqrMagnitude > .001f) transform.rotation = Quaternion.LookRotation(direction);
            }
            if (dialogue != null && dialogue.Length > 0)
            {
                player.ShowMessage(displayName + ": " + dialogue[line % dialogue.Length]);
                line = (line+1)%dialogue.Length;
            }
            base.Interact(player);
        }
    }
}
