using System;
using System.IO;
using GrassFlow;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
public static class GrassMeadowVisualReview
{
    public static void Run()
    {
        var mesh=new Mesh();
        mesh.vertices=new[]{new Vector3(-48,-12,-48),new Vector3(-48,-12,48),new Vector3(48,12,-48),new Vector3(48,12,48)};
        mesh.triangles=new[]{0,1,2,2,1,3}; mesh.RecalculateNormals(); mesh.RecalculateBounds();
        var patch=GrassFlow.Editor.GrassFlowPainterWindow.CreatePatch(mesh,mesh.bounds,.55f); patch.bladesPerRow=96;
        var ground=new GameObject("Continuous 14 degree slope"); ground.AddComponent<MeshFilter>().sharedMesh=mesh;
        var mat=new Material(Shader.Find("Voyage/Terrain/Stylized")); ground.AddComponent<MeshRenderer>().sharedMaterial=mat;
        var grass=ground.AddComponent<GrassFlowRenderer>(); grass.patch=patch;
        var sky=new GameObject("Review lighting").AddComponent<Voyage.Lighting.DayNightSystem>(); sky.advanceTime=false; sky.SendMessage("OnEnable");
        var cameraObject=new GameObject("Slope review camera"); var camera=cameraObject.AddComponent<Camera>();
        camera.transform.SetPositionAndRotation(new Vector3(-10,3,-26),Quaternion.LookRotation(new Vector3(.2f,-.08f,1)));
        var rt=new RenderTexture(1200,720,24); var image=new Texture2D(1200,720,TextureFormat.RGB24,false); camera.targetTexture=rt; camera.fieldOfView=67;
        Directory.CreateDirectory("Logs/GrassFlowValidation");
        int frame=0,phase=0; sky.SetTime(10); Color[] dayPixels=null;
        EditorApplication.CallbackFunction update=null;
        update=()=>
        {
            try
            {
                EditorApplication.QueuePlayerLoopUpdate(); camera.Render();
                if(++frame<5) return;
                RenderTexture.active=rt; image.ReadPixels(new Rect(0,0,1200,720),0,0); image.Apply();
                string name=phase==0?"slope-day":phase==1?"slope-dusk":"slope-ground";
                File.WriteAllBytes("Logs/GrassFlowValidation/"+name+".png",image.EncodeToPNG());
                if(phase==0) dayPixels=image.GetPixels();
                if(phase==2)
                {
                    var bare=image.GetPixels(); var contrasts=new System.Collections.Generic.List<float>();
                    for(int i=0;i<bare.Length;i++)
                        if(bare[i].r>bare[i].b && bare[i].grayscale>.05f)
                            contrasts.Add(Mathf.Abs(dayPixels[i].grayscale-bare[i].grayscale)/bare[i].grayscale);
                    contrasts.Sort(); float p95=contrasts[(int)(contrasts.Count*.95f)];
                    File.WriteAllText("Logs/GrassFlowValidation/slope-palette-review.txt",$"14-degree slope, identical day lighting and camera, foliage versus bare ground. 95th-percentile relative luminance contrast: {p95:F4}.\n");
                    if(p95>.12f) throw new Exception("Grass-ground contrast is too high: "+p95);
                }
                if(phase==0) sky.SetTime(17.2f);
                if(phase==1) { sky.SetTime(10); grass.enabled=false; }
                if(++phase<3) {frame=0;return;}
                EditorApplication.update-=update;
                camera.targetTexture=null; RenderTexture.active=null;
                Object.DestroyImmediate(ground); Object.DestroyImmediate(mesh); Object.DestroyImmediate(mat);
                Object.DestroyImmediate(patch.surface); Object.DestroyImmediate(patch.density); Object.DestroyImmediate(patch);
                Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(rt); Object.DestroyImmediate(image);
                sky.SendMessage("OnDisable"); Object.DestroyImmediate(sky.gameObject); EditorApplication.Exit(0);
            }
            catch(Exception e) { Debug.LogException(e); EditorApplication.update-=update; EditorApplication.Exit(1); }
        };
        EditorApplication.update+=update;
    }
}
