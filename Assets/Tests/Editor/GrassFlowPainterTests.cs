using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Voyage.Tests.Editor
{
    public sealed class GrassFlowPainterTests
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static Type Find(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) { var type = assembly.GetType(name); if (type != null) return type; }
            throw new Exception(name);
        }
        static object Get(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
        static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
        static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags).Invoke(value, args);
        static Mesh Surface(float offset = 0)
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(offset, 0, 0), new Vector3(offset, 0, 16), new Vector3(offset + 16, 0, 0), new Vector3(offset + 16, 0, 16) };
            mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 }; mesh.RecalculateBounds(); return mesh;
        }
        static ScriptableObject Patch(Mesh mesh, float offset = 0, float fill = 1) => (ScriptableObject)Find("GrassFlow.Editor.GrassFlowPainterWindow").GetMethod("CreatePatch").Invoke(null, new object[] { mesh, new Bounds(new Vector3(offset + 8, 0, 8), new Vector3(16, 1, 16)), fill });
        static void DeletePatch(ScriptableObject patch)
        {
            Object.DestroyImmediate((Object)Get(patch, "surface")); Object.DestroyImmediate((Object)Get(patch, "density")); Object.DestroyImmediate(patch);
        }

        static ScriptableObject streamedResult;
        static void ReceiveStreamed<T>(T value) { streamedResult = value as ScriptableObject; }
        [Test]
        public void UnpaintedFarTileBuildsGrassFromTransformedMesh()
        {
            var host = new GameObject("Far tile surface"); var mesh = Surface(); streamedResult = null;
            try
            {
                host.transform.position = new Vector3(2048, 25, 2048);
                var filter = host.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
                var patchType = Find("GrassFlow.GrassFlowPatch");
                var method = typeof(GrassFlowPainterTests).GetMethod("ReceiveStreamed", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(patchType);
                var callback = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(patchType), method);
                var routine = (System.Collections.IEnumerator)Find("Voyage.TerrainSystem.StreamedGrassSurface").GetMethod("Build").Invoke(null,
                    new object[] { new[] { filter }, new Bounds(new Vector3(2056,25,2056), new Vector3(16,10,16)), callback });
                while(routine.MoveNext()) { }
                Assert.That(streamedResult, Is.Not.Null);
                var surface = (Texture2D)Get(streamedResult,"surface");
                Assert.That(surface.GetPixel(64,64).g, Is.EqualTo(1));
                var bounds = (Bounds)Get(streamedResult,"bounds");
                Assert.That(bounds.min.y+surface.GetPixel(64,64).r*bounds.size.y,Is.EqualTo(25).Within(.01));
                Assert.That(((Texture2D)Get(streamedResult,"density")).GetPixel(64,64).r, Is.GreaterThan(.5f));
            }
            finally { if(streamedResult!=null) DeletePatch(streamedResult); Object.DestroyImmediate(host); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void BrushErasesAddsAndUndoRestoresTexture()
        {
            var mesh = Surface(); var patch = Patch(mesh); var density = (Texture2D)Get(patch, "density");
            try
            {
                Undo.RegisterCompleteObjectUndo(density, "Grass test");
                Call(patch, "Paint", new Vector3(8, 0, 8), 4f, 1f, 0f, .8f);
                Assert.That(density.GetPixel(128, 128).r, Is.LessThan(.01f));
                Assert.That(density.GetPixel(0, 0).r, Is.GreaterThan(.99f));
                Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
                Assert.That(density.GetPixel(128, 128).r, Is.GreaterThan(.99f));
                Call(patch, "Paint", new Vector3(8, 0, 8), 4f, 1f, 0f, .8f);
                Call(patch, "Paint", new Vector3(8, 0, 8), 4f, 1f, .5f, .8f);
                Assert.That(density.GetPixel(128, 128).r, Is.EqualTo(.5f).Within(.01f));
            }
            finally { Undo.ClearUndo(density); DeletePatch(patch); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void BrushCrossesTileBoundarySymmetrically()
        {
            var a = Surface(); var b = Surface(16); var first = Patch(a); var second = Patch(b, 16);
            try
            {
                foreach (var patch in new[] { first, second }) Call(patch, "Paint", new Vector3(16, 0, 8), 4f, 1f, 0f, .5f);
                float left = ((Texture2D)Get(first, "density")).GetPixel(250, 128).r;
                float right = ((Texture2D)Get(second, "density")).GetPixel(5, 128).r;
                Assert.That(left, Is.LessThan(.1f)); Assert.That(left, Is.EqualTo(right).Within(.01f));
            }
            finally { DeletePatch(first); DeletePatch(second); Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
        }

        [Test]
        public void PaintCannotCreateGrassOutsideMeshCoverage()
        {
            var mesh = Surface(); mesh.triangles = new[] { 0, 1, 2 }; var patch = Patch(mesh, 0, 0);
            try
            {
                Call(patch, "Paint", new Vector3(8, 0, 8), 32f, 1f, 1f, 1f);
                var density = (Texture2D)Get(patch, "density");
                Assert.That(density.GetPixel(230, 230).r, Is.LessThan(.01f));
                Assert.That(density.GetPixel(20, 20).r, Is.GreaterThan(.99f));
            }
            finally { DeletePatch(patch); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void StreamedTileUsesSavedPatchAndSkipsLegacyGeneration()
        {
            var go = new GameObject("Painted terrain streaming test");
            var settings = ScriptableObject.CreateInstance(Find("Voyage.TerrainSystem.TerrainChunkSettings"));
            try
            {
                go.SetActive(false);
                var oldGrass = go.AddComponent(Find("Voyage.TerrainSystem.InteractiveGrassTile"));
                var tile = go.AddComponent(Find("Voyage.TerrainSystem.TerrainTileRuntime"));
                Set(settings, "usePaintedGrass", true); Set(tile, "settings", settings); Set(tile, "coordinate", new Vector2Int(-1, -1));
                Call(tile, "InitializeGrass");
                Assert.That(((Behaviour)oldGrass).enabled, Is.False);
                Assert.That(go.GetComponent(Find("GrassFlow.GrassFlowRenderer")), Is.Not.Null);
                Assert.That(tile.GetType().GetProperty("GrassBuildFinished").GetValue(tile), Is.True);
                Assert.That(tile.GetType().GetProperty("NeedsGrassInitialization").GetValue(tile), Is.False);
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(settings); }
        }

        [Test]
        public void GpuRenderingRespondsToSavedDensityAndDayNight()
        {
            var mesh = Surface(); var patch = Patch(mesh);
            var host = new GameObject("GrassFlow GPU test"); var cameraObject = new GameObject("GrassFlow camera");
            var camera = cameraObject.AddComponent<Camera>(); var target = new RenderTexture(640, 360, 24);
            var image = new Texture2D(640, 360, TextureFormat.RGB24, false);
            var pressure = new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Set(patch, "bladesPerRow", 64);
                var component = host.AddComponent(Find("GrassFlow.GrassFlowRenderer")); Set(component, "patch", patch);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.transform.SetPositionAndRotation(new Vector3(8, 3, -3), Quaternion.LookRotation(new Vector3(0, -1, 4)));
                camera.targetTexture = target;
                Shader.SetGlobalVector("_VoyageGrassInteractionWorld", Vector4.zero);
                Shader.SetGlobalTexture("_VoyageGrassInteraction", Texture2D.blackTexture);
                Shader.SetGlobalTexture("_VoyageGrassPermanentInteraction", Texture2D.blackTexture);
                Shader.SetGlobalColor("_VoyageGrassEnvironmentColor", Color.white); Shader.SetGlobalFloat("_VoyageGrassEnvironmentLight", 1);
                Directory.CreateDirectory("Logs/GrassFlowValidation");
                float day = Capture(camera, target, image);
                File.WriteAllBytes("Logs/GrassFlowValidation/painted-day.png", image.EncodeToPNG());
                Assert.That(day, Is.GreaterThan(.002f), "Painted grass must actually render through URP.");
                Assert.That(ShaderUtil.ShaderHasError(Resources.Load<Shader>("GrassFlow/GrassShader")), Is.False);
                Set(patch, "windStrength", 0f); Call(patch, "Changed");
                Capture(camera, target, image); var upright = image.GetPixels();
                // Pressure-only contact (no velocity vector) must still flatten stationary grass.
                pressure.SetPixels(new[] { Color.blue, Color.blue, Color.blue, Color.blue }); pressure.Apply();
                Shader.SetGlobalTexture("_VoyageGrassInteraction", pressure);
                Shader.SetGlobalTexture("_VoyageGrassPermanentInteraction", Texture2D.blackTexture);
                Shader.SetGlobalVector("_VoyageGrassInteractionWorld", new Vector4(8, 8, 32, 0));
                Capture(camera, target, image);
                Assert.That(PixelDifference(upright, image.GetPixels()), Is.GreaterThan(.005f), "Stationary contact pressure must deform the actual rendered blades.");
                File.WriteAllBytes("Logs/GrassFlowValidation/pressed-meadow.png", image.EncodeToPNG());
                Shader.SetGlobalVector("_VoyageGrassInteractionWorld", Vector4.zero);
                Capture(camera, target, image);
                Assert.That(PixelDifference(upright, image.GetPixels()), Is.LessThan(.001f), "Clearing the contact field must restore the grass.");
                Set(patch, "windStrength", .6f); Call(patch, "Changed");
                Capture(camera, target, image);
                Assert.That(PixelDifference(upright, image.GetPixels()), Is.GreaterThan(.001f), "Wind must change blade silhouettes.");
                Shader.SetGlobalColor("_VoyageGrassEnvironmentColor", new Color(.46f, .54f, .72f)); Shader.SetGlobalFloat("_VoyageGrassEnvironmentLight", .48f);
                float night = Capture(camera, target, image);
                Assert.That(night, Is.LessThan(day * .75f));
                File.WriteAllBytes("Logs/GrassFlowValidation/painted-night.png", image.EncodeToPNG());
                Call(patch, "Paint", new Vector3(8, 0, 8), 32f, 1f, 0f, 1f);
                float erased = Capture(camera, target, image);
                Assert.That(erased, Is.LessThan(day * .01f), "Erasing must remove indirect draw instances.");
                File.WriteAllBytes("Logs/GrassFlowValidation/erased.png", image.EncodeToPNG());
                // Turning renderer off/on must safely recreate camera buffers.
                ((Behaviour)component).enabled = false; ((Behaviour)component).enabled = true;
                Call(patch, "Paint", new Vector3(8, 0, 8), 32f, 1f, 1f, 1f);
                Assert.That(Capture(camera, target, image), Is.GreaterThan(.001f));
            }
            finally
            {
                camera.targetTexture = null; RenderTexture.active = previous;
                Object.DestroyImmediate(host); Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(target); Object.DestroyImmediate(image);
                Object.DestroyImmediate(pressure);
                Shader.SetGlobalTexture("_VoyageGrassInteraction", null);
                Shader.SetGlobalTexture("_VoyageGrassPermanentInteraction", null);
                Shader.SetGlobalVector("_VoyageGrassInteractionWorld", Vector4.zero);
                DeletePatch(patch); Object.DestroyImmediate(mesh);
                Shader.SetGlobalColor("_VoyageGrassEnvironmentColor", Color.white); Shader.SetGlobalFloat("_VoyageGrassEnvironmentLight", 1);
            }
        }

        static float Capture(Camera camera, RenderTexture target, Texture2D image)
        {
            camera.Render(); RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            float sum = 0; foreach (var pixel in image.GetPixels()) sum += pixel.grayscale; return sum / (target.width * target.height);
        }
        static float PixelDifference(Color[] a, Color[] b)
        {
            float sum = 0;
            for (int i = 0; i < a.Length; i++) sum += Mathf.Abs(a[i].r-b[i].r)+Mathf.Abs(a[i].g-b[i].g)+Mathf.Abs(a[i].b-b[i].b);
            return sum/(a.Length*3);
        }

        [Test]
        public void PersistentHorizonContainsOnlyLowDetailGeometryBeyondStreamingRadius()
        {
            var horizon=Resources.Load<GameObject>("TerrainSystem/Horizon/TerrainHorizon");
            Assert.That(horizon,Is.Not.Null);
            Assert.That(horizon.GetComponentsInChildren<Collider>(true),Is.Empty);
            Assert.That(horizon.GetComponentsInChildren<MonoBehaviour>(true),Is.Empty);
            var renderers=horizon.GetComponentsInChildren<MeshRenderer>(true);
            Assert.That(renderers.Length,Is.GreaterThan(1));
            bool distant=false; long triangles=0;
            foreach(var renderer in renderers)
            {
                Assert.That(renderer.sharedMaterial.GetFloat("_Horizon"),Is.EqualTo(1));
                Assert.That(renderer.shadowCastingMode,Is.EqualTo(UnityEngine.Rendering.ShadowCastingMode.Off));
                var mesh=renderer.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(mesh,Is.Not.Null); triangles+=mesh.GetIndexCount(0)/3;
                distant|=mesh.bounds.center.sqrMagnitude>6000f*6000f;
            }
            Assert.That(distant,Is.True,"The backdrop must extend beyond the local 1 km streaming bubble.");
            Assert.That(triangles,Is.LessThan(1000000),"The entire backdrop must remain lightweight.");
        }

        [TestCase(false, 12f, false)]
        [TestCase(false, 180f, false)]
        [TestCase(false, 6000f, false)]
        [TestCase(true, 180f, false)]
        [TestCase(false, 180f, true)]
        public void DistantContoursPreserveSilhouettesWithoutStripingContinuousGround(bool continuousGround, float distance, bool foliage)
        {
            var features = new System.Collections.Generic.List<UnityEngine.Object>();
            var states = new System.Collections.Generic.List<bool>();
            foreach(var path in new[] { "Assets/Settings/PC_Renderer.asset", "Assets/Settings/Mobile_Renderer.asset" })
            {
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                var entries = new SerializedObject(asset).FindProperty("m_RendererFeatures");
                for(int i=0;i<entries.arraySize;i++)
                {
                    var feature = entries.GetArrayElementAtIndex(i).objectReferenceValue;
                    if(feature!=null && feature.GetType().Name=="DistantContourFeature")
                    { features.Add(feature); states.Add((bool)feature.GetType().GetProperty("isActive").GetValue(feature)); }
                }
            }
            Assert.That(features.Count,Is.GreaterThan(0));
            var host=new GameObject("Contour camera"); var camera=host.AddComponent<Camera>();
            var cube=GameObject.CreatePrimitive(continuousGround ? PrimitiveType.Plane : PrimitiveType.Cube);
            var material=new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            var target=new RenderTexture(320,180,24); var image=new Texture2D(320,180,TextureFormat.RGB24,false);
            var previous=RenderTexture.active; bool fog=RenderSettings.fog;
            var fogMode=RenderSettings.fogMode; var fogColor=RenderSettings.fogColor;
            float fogStart=RenderSettings.fogStartDistance, fogEnd=RenderSettings.fogEndDistance;
            try
            {
                RenderSettings.fog=false; camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.gray;
                if(distance>1800)
                {
                    RenderSettings.fog=true; RenderSettings.fogMode=FogMode.Linear;
                    RenderSettings.fogColor=Color.gray; RenderSettings.fogStartDistance=240; RenderSettings.fogEndDistance=1800;
                }
                camera.farClipPlane=12000; camera.targetTexture=target;
                cube.transform.position=new Vector3(0,0,distance); cube.transform.localScale=Vector3.one*(distance/6);
                if(foliage) { cube.layer=LayerMask.NameToLayer("VoyageGrass"); Assert.That(cube.layer,Is.GreaterThan(0)); }
                if(continuousGround)
                {
                    cube.transform.position=Vector3.zero; cube.transform.localScale=Vector3.one*1000;
                    camera.transform.position=new Vector3(0,20,0);
                }
                material.SetColor("_BaseColor",Color.gray); cube.GetComponent<Renderer>().sharedMaterial=material;
                foreach(var feature in features) feature.GetType().GetMethod("SetActive").Invoke(feature,new object[]{false});
                Capture(camera,target,image); var without=image.GetPixels();
                foreach(var feature in features) feature.GetType().GetMethod("SetActive").Invoke(feature,new object[]{true});
                Capture(camera,target,image);
                if(continuousGround)
                {
                    var withContours=image.GetPixels(); float error=0;
                    for(int y=0;y<image.height/2-4;y++) for(int x=8;x<image.width-8;x++)
                        error+=Mathf.Abs(withContours[y*image.width+x].r-without[y*image.width+x].r);
                    error/=(image.height/2-4)*(image.width-16);
                    File.WriteAllBytes("Logs/GrassFlowValidation/continuous-ground-contours.png",image.EncodeToPNG());
                    Assert.That(error,Is.LessThan(.0002f),"A continuous perspective plane must not acquire horizontal contour bands.");
                }
                else if(foliage) Assert.That(PixelDifference(without,image.GetPixels()),Is.LessThan(.00005f),"Foliage must not produce dark canopy contours.");
                else Assert.That(PixelDifference(without,image.GetPixels()),Is.GreaterThan(.00005f),"Depth contours must visibly separate same-color distant objects from the background.");
                Directory.CreateDirectory("Logs/GrassFlowValidation");
                File.WriteAllBytes($"Logs/GrassFlowValidation/contours-{distance}.png",image.EncodeToPNG());
                Assert.That(ShaderUtil.ShaderHasError(Shader.Find("Hidden/Voyage/DistantContours")),Is.False);
            }
            finally
            {
                for(int i=0;i<features.Count;i++) features[i].GetType().GetMethod("SetActive").Invoke(features[i],new object[]{states[i]});
                RenderSettings.fog=fog; RenderTexture.active=previous; camera.targetTexture=null;
                RenderSettings.fogMode=fogMode; RenderSettings.fogColor=fogColor;
                RenderSettings.fogStartDistance=fogStart; RenderSettings.fogEndDistance=fogEnd;
                Object.DestroyImmediate(cube); Object.DestroyImmediate(host); Object.DestroyImmediate(material); Object.DestroyImmediate(target); Object.DestroyImmediate(image);
            }
        }
    }
}
