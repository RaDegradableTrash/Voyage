using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

namespace Voyage.Exploration
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class ExplorerPlayer : MonoBehaviour
    {
        public float moveSpeed = 4.5f;
        [Tooltip("Movement speed while holding Left Shift or Right Shift.")]
        public float sprintSpeed = 8f;
        public float jumpHeight = 1.1f;
        public float gravity = 24f;
        public float discoveryRange = 6f;
        public float interactionRange = 2.5f;
        public WorldInteractable Target { get; private set; }
        public bool CanInteract => Target != null && Vector3.Distance(transform.position+Vector3.up,Target.Anchor) <= interactionRange;
        public string Hint => Target == null ? "" : CanInteract ? "!" : "?";
        public Vector3 Velocity => controller != null ? controller.velocity : Vector3.zero;
        CharacterController controller;
        TextMeshPro indicator;
        readonly RaycastHit[] hits = new RaycastHit[32];
        float verticalSpeed, lastGrounded = -100, jumpUntil = -100;
        string message;
        float messageUntil;
        GUIStyle label;

        void Awake()
        {
            controller = GetComponent<CharacterController>();
            var icon = new GameObject("Awareness hint");
            icon.transform.SetParent(transform,false);
            icon.transform.localPosition = Vector3.up*2.65f;
            indicator = icon.AddComponent<TextMeshPro>();
            indicator.font = TMP_Settings.defaultFontAsset;
            indicator.fontSize = 7;
            indicator.alignment = TextAlignmentOptions.Center;
            indicator.text = "";
            indicator.rectTransform.sizeDelta = new Vector2(2,2);
            indicator.outlineWidth = .2f;
            indicator.outlineColor = new Color32(35,32,29,255);
        }

        public static bool InteractPressed => Keyboard.current != null
            ? Keyboard.current.fKey.wasPressedThisFrame : Input.GetKeyDown(KeyCode.F);
        void OnEnable() { verticalSpeed=0;lastGrounded=jumpUntil=-100;Target=null; }
        bool Blocked => !Application.isFocused || Time.timeScale <= 0 || VoyageCommandConsole.IsOpen || VoyageCommandConsole.ConsumedInputThisFrame;
        void Update()
        {
            if (Blocked) { if(indicator!=null)indicator.text=""; return; }
            Vector2 input;
            var keys=Keyboard.current;
            if(keys!=null) input=new Vector2((keys.dKey.isPressed?1:0)-(keys.aKey.isPressed?1:0),(keys.wKey.isPressed?1:0)-(keys.sKey.isPressed?1:0));
            else input=new Vector2(Input.GetAxisRaw("Horizontal"),Input.GetAxisRaw("Vertical"));
            bool jump=keys!=null ? keys.spaceKey.wasPressedThisFrame : Input.GetKeyDown(KeyCode.Space);
            bool sprint=keys!=null ? (keys.leftShiftKey.isPressed || keys.rightShiftKey.isPressed) : Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            Move(input,jump,Time.deltaTime,sprint);
            RefreshTarget();
            if(InteractPressed && (ExplorationSession.Instance==null || ExplorationSession.Instance.TransitionFrame!=Time.frameCount)) TryInteract();
        }

        public void Move(Vector2 input,bool jump,float delta,bool sprint=false)
        {
            if(controller==null)controller=GetComponent<CharacterController>();
            if(!controller.enabled || delta<=0)return;
            if(controller.isGrounded && verticalSpeed<=0) { lastGrounded=Time.time; verticalSpeed=-2; }
            if(jump)jumpUntil=Time.time+.12f;
            if(Time.time<=jumpUntil && Time.time-lastGrounded<=.08f)
            { verticalSpeed=Mathf.Sqrt(2*gravity*jumpHeight); jumpUntil=lastGrounded=-100; }
            Camera camera=Camera.main;
            Vector3 forward=camera!=null?Vector3.ProjectOnPlane(camera.transform.forward,Vector3.up).normalized:Vector3.forward;
            if(forward.sqrMagnitude<.01f)forward=Vector3.forward;
            Vector3 right=Vector3.Cross(Vector3.up,forward);
            input=Vector2.ClampMagnitude(input,1);
            float speed = sprint && input.sqrMagnitude > 0.001f ? Mathf.Max(moveSpeed, sprintSpeed) : moveSpeed;
            Vector3 move=(forward*input.y+right*input.x)*speed;
            if(move.sqrMagnitude>.01f)transform.rotation=Quaternion.Slerp(transform.rotation,Quaternion.LookRotation(move),1-Mathf.Exp(-12*delta));
            verticalSpeed-=gravity*delta;
            CollisionFlags collisions=controller.Move((move+Vector3.up*verticalSpeed)*delta);
            if((collisions&CollisionFlags.Above)!=0 && verticalSpeed>0)verticalSpeed=0;
        }

        public void RefreshTarget()
        {
            WorldInteractable best=null;float bestDistance=discoveryRange;
            Vector3 eye=transform.position+Vector3.up;
            foreach(var candidate in WorldInteractable.Active)
            {
                if(candidate==null || !candidate.isActiveAndEnabled || !candidate.interactable)continue;
                float distance=Vector3.Distance(eye,candidate.Anchor);
                if(distance>bestDistance || !Visible(candidate,eye))continue;
                best=candidate;bestDistance=distance;
            }
            Target=best;
        }
        bool Visible(WorldInteractable target,Vector3 eye)
        {
            Vector3 delta=target.Anchor-eye;
            if(delta.sqrMagnitude<.001f)return true;
            int count=Physics.RaycastNonAlloc(eye,delta.normalized,hits,delta.magnitude,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore);
            if(count==hits.Length)return false;
            for(int i=0;i<count;i++)
                if(!hits[i].transform.IsChildOf(transform) && !hits[i].transform.IsChildOf(target.transform))return false;
            return true;
        }
        public bool TryInteract()
        {
            RefreshTarget();
            if(!CanInteract)return false;
            Target.Interact(this);return true;
        }
        public void ShowMessage(string value) { message=value;messageUntil=Time.time+5; }
        void LateUpdate()
        {
            if(indicator==null)return;
            indicator.text=Time.timeScale<=0 || VoyageCommandConsole.IsOpen ? "" : Hint;
            indicator.color=CanInteract?new Color(1,.88f,.5f):new Color(.92f,.92f,.86f);
            indicator.transform.localPosition=Vector3.up*(2.65f+Mathf.Sin(Time.time*3)*.05f);
            if(Camera.main!=null)indicator.transform.rotation=Camera.main.transform.rotation;
        }
        void OnGUI()
        {
            if(Blocked)return;
            if(label==null)label=new GUIStyle(GUI.skin.box){fontSize=18,wordWrap=true};
            if(Time.time<messageUntil)GUI.Box(new Rect(Screen.width*.5f-260,Screen.height-145,520,70),message,label);
            else if(CanInteract)GUI.Box(new Rect(Screen.width*.5f-180,Screen.height-100,360,40),"F  "+Target.prompt+" — "+Target.displayName,label);
        }
    }
}
