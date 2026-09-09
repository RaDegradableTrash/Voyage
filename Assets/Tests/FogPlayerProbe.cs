using System;
using System.Collections;
using System.IO;
using UnityEngine;
namespace Voyage.Tests
{
    // Only attached to the standalone validation scene, never to the game scene.
    public class FogPlayerProbe : MonoBehaviour
    {
        public Shader terrainShader;
        public Shader litShader;
        IEnumerator Start()
        {
            yield return null;
            var day=Voyage.Lighting.DayNightSystem.Instance;
            day.advanceTime=false; day.SetTime(12f);
            var fog=Voyage.Lighting.FogSystem.Instance;
            fog.enableFog=true; fog.contribution=1f; fog.Apply();
            var camera=new GameObject("Probe camera").AddComponent<Camera>();
            camera.enabled=false; camera.farClipPlane=12000;
            camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=fog.CurrentColor;
            var quad=GameObject.CreatePrimitive(PrimitiveType.Quad);
            var target=new RenderTexture(320,180,24);
            camera.targetTexture=target;
            var pixels=new Texture2D(320,180,TextureFormat.RGB24,false);
            string output=Path.Combine(Application.dataPath,"..","fog-result.txt");
            bool passed=true; string report="Fog enabled by runtime bootstrap: "+RenderSettings.fog+"\n";
            Shader.SetGlobalVector("_VoyageTerrainView",Vector4.zero);
            foreach(var shader in new[]{terrainShader,litShader})
            {
                var material=new Material(shader); quad.GetComponent<Renderer>().sharedMaterial=material;
                material.SetColor("_BaseColor",new Color(.65f,.24f,.08f));
                foreach(float distance in new[]{10f,3000f})
                {
                    quad.transform.position=Vector3.forward*distance;
                    quad.transform.localScale=Vector3.one*distance*2;
                    RenderSettings.fog=false; var clear=Capture(camera,target,pixels);
                    fog.Apply(); var fogged=Capture(camera,target,pixels);
                    float difference=Mathf.Abs(clear.r-fogged.r)+Mathf.Abs(clear.g-fogged.g)+Mathf.Abs(clear.b-fogged.b);
                    bool valid=distance>1800 ? difference>.1f : difference<.025f;
                    report+=$"{shader.name} {distance}m: difference={difference:F5}, pass={valid}\n";
                    File.WriteAllBytes(Path.Combine(Application.dataPath,"..",shader==terrainShader?$"terrain-{distance}.png":$"lit-{distance}.png"),pixels.EncodeToPNG());
                    passed&=valid;
                }
                Destroy(material);
            }
            quad.SetActive(false);
            passed &= RuntimeFeatureProbe.Run(camera,target,pixels,out string features);
            report += features;
            File.WriteAllText(output,report+"PASS="+passed);
            Debug.Log(report);
            Application.Quit(passed?0:1);
        }
        static Color Capture(Camera camera,RenderTexture target,Texture2D pixels)
        {
            camera.Render(); RenderTexture.active=target;
            pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0); pixels.Apply();
            return pixels.GetPixel(target.width/2,target.height/2);
        }
    }
}
