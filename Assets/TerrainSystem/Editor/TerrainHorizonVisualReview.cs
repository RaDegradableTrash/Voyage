#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
public static class TerrainHorizonVisualReview
{
    public static void Run()
    {
        var prefab=Resources.Load<GameObject>("TerrainSystem/Horizon/TerrainHorizon");
        if(prefab==null) throw new System.InvalidOperationException("Horizon missing");
        Object.Instantiate(prefab);
        Shader.SetGlobalVector("_VoyageTerrainView",new Vector4(-24,-24,768,1024));
        var sky=new GameObject("Review light").AddComponent<Voyage.Lighting.DayNightSystem>();
        sky.advanceTime=false; sky.SendMessage("OnEnable"); sky.SetTime(10);
        RenderSettings.fog=true; RenderSettings.fogMode=FogMode.Linear;
        RenderSettings.fogStartDistance=240; RenderSettings.fogEndDistance=1800;
        RenderSettings.fogColor=new Color(.64f,.66f,.69f);
        var camera=new GameObject("Far terrain review camera").AddComponent<Camera>();
        camera.transform.SetPositionAndRotation(new Vector3(-24,350,-24),Quaternion.LookRotation(new Vector3(-.3f,-.08f,1)));
        camera.farClipPlane=12000; camera.fieldOfView=67;
        camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=RenderSettings.fogColor;
        var target=new RenderTexture(1200,720,24); camera.targetTexture=target;
        var image=new Texture2D(1200,720,TextureFormat.RGB24,false);
        int frames=0;
        EditorApplication.CallbackFunction update=null;
        update=()=>
        {
            camera.Render(); EditorApplication.QueuePlayerLoopUpdate();
            if(++frames<8) return;
            RenderTexture.active=target; image.ReadPixels(new Rect(0,0,1200,720),0,0); image.Apply();
            Directory.CreateDirectory("Logs/GrassFlowValidation");
            File.WriteAllBytes("Logs/GrassFlowValidation/horizon-fog.png",image.EncodeToPNG());
            EditorApplication.update-=update; EditorApplication.Exit(0);
        };
        EditorApplication.update+=update;
    }
}
#endif
