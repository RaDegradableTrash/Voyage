using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Voyage.Tests.Editor
{
    public sealed class WindInteractionTests
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        GameObject host;
        Component wind, presentation;
        static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name,Flags).Invoke(target,args);
        void Set(string name, object value) => wind.GetType().GetField(name,Flags).SetValue(wind,value);
        void Create()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("GPU required");
            host = new GameObject("Wind tests"); host.SetActive(false);
            wind = host.AddComponent(Type.GetType("Voyage.Wind.WindSystem, Assembly-CSharp",true));
            presentation = host.AddComponent(Type.GetType("Voyage.Wind.WindPresentation, Assembly-CSharp",true));
            Call(presentation,"OnEnable");
        }
        Color Sample()
        {
            var rt = (RenderTexture)presentation.GetType().GetProperty("SlopeField").GetValue(presentation);
            var tex = new Texture2D(rt.width,rt.height,TextureFormat.RGBAFloat,false,true);
            var old = RenderTexture.active;
            try { RenderTexture.active=rt; tex.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); tex.Apply(); return tex.GetPixel(80,70); }
            finally { RenderTexture.active=old; Object.DestroyImmediate(tex); }
        }
        [TearDown] public void Cleanup()
        {
            if (presentation != null) Call(presentation,"OnDisable");
            if (host != null) Object.DestroyImmediate(host);
        }
        [Test] public void MapRespectsCalmDirectionAndDoesNotWriteCollisionDamage()
        {
            Create(); Set("force",0f); Call(presentation,"UpdateField",0f);
            Assert.That(Sample().maxColorComponent,Is.LessThan(.001f));
            Set("force",1f); Set("direction",Vector2.left); Set("gustStrength",0f);
            Call(presentation,"UpdateField",0f);
            Color a=Sample(); Assert.That(a.r,Is.LessThan(-.1f)); Assert.That(a.a,Is.Zero);
            Set("direction",Vector2.right); Call(presentation,"UpdateField",0f);
            Assert.That(Sample().r,Is.EqualTo(-a.r).Within(.003f));
            Set("speed",0f); Color before=Sample(); Call(presentation,"UpdateField",2f);
            Assert.That(Sample().r,Is.EqualTo(before.r).Within(.001f));
        }
        [Test] public void ForcedRibbonsArePooledAndShadersAreAvailable()
        {
            Create(); Set("force",0f);
            for(int i=0;i<20;i++) Assert.That(Call(presentation,"Spawn",Vector3.zero),Is.EqualTo(true));
            Assert.That(host.GetComponentsInChildren<LineRenderer>(true).Length,Is.EqualTo(8));
            foreach(string path in new[]{"Assets/Resources/WindLine.shader","Assets/Resources/WindSlope.shader",
                "Assets/Plugins/GrassFlow/Resources/GrassFlow/GrassShader.shader","Assets/Shaders/InteractiveGrass.shader"})
            {
                var shader=AssetDatabase.LoadAssetAtPath<Shader>(path);
                Assert.That(shader,Is.Not.Null,path);
                Assert.That(ShaderUtil.ShaderHasError(shader),Is.False,path);
            }
        }
    }
}
