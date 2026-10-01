using UnityEditor;
using UnityEngine;
using Voyage.Exploration;
using Voyage.TerrainSystem;

[InitializeOnLoad]
public static class ExplorerPrefabBuilder
{
    static ExplorerPrefabBuilder()=>EditorApplication.delayCall+=Ensure;
    [MenuItem("Voyage/Generate Explorer Prefabs")]
    public static void Ensure()
    {
        if(EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer)return;
        bool created = Create(true) | Create(false);
        if (created) AssetDatabase.SaveAssets();
    }
    static bool Create(bool player)
    {
        string name=player?"ExplorerPlayer":"NpcCapsule";
        string path="Assets/Resources/Prefabs/"+name+".prefab";
        if(AssetDatabase.LoadAssetAtPath<GameObject>(path)!=null)return false;
        var root=new GameObject(name);root.SetActive(false);
        try
        {
            var visual=GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name="Capsule visual";visual.transform.SetParent(root.transform,false);
            visual.transform.localPosition=Vector3.up;visual.transform.localScale=new Vector3(.8f,1,.8f);
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            string materialPath="Assets/Resources/Prefabs/"+name+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if(material==null)
            {
                material=new Material(Shader.Find("Universal Render Pipeline/Lit"));
                material.SetColor("_BaseColor",player?new Color(.55f,.68f,.75f):new Color(.72f,.64f,.49f));
                AssetDatabase.CreateAsset(material,materialPath);
            }
            visual.GetComponent<Renderer>().sharedMaterial=material;
            if(player)
            {
                var controller=root.AddComponent<CharacterController>();controller.center=Vector3.up;
                controller.height=2;controller.radius=.4f;controller.stepOffset=.3f;controller.slopeLimit=45;
                root.AddComponent<ExplorerPlayer>();
            }
            else
            {
                var collider=root.AddComponent<CapsuleCollider>();collider.center=Vector3.up;collider.height=2;collider.radius=.4f;
                var actor=root.AddComponent<NpcActor>();actor.displayName="Traveller";actor.prompt="Talk";
            }
            root.AddComponent<GrassInteractionEmitter>().radius=.45f;
            // Save active prefab without invoking gameplay Awake in the editor.
            root.SetActive(true);PrefabUtility.SaveAsPrefabAsset(root,path);
        }
        finally { Object.DestroyImmediate(root); }
        return true;
    }
}
