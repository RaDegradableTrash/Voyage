using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Voyage.Tests.Editor
{
    public sealed class GrassContactTests
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        GameObject host;
        Component system;
        static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
        void Set(string name, object value) => system.GetType().GetField(name, Flags).SetValue(system, value);
        RenderTexture Field => (RenderTexture)system.GetType().GetProperty("Field").GetValue(system);

        void Create(bool compute)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Requires GPU");
            host = new GameObject("Grass contact regression");
            host.SetActive(false);
            system = host.AddComponent(Type.GetType("Voyage.TerrainSystem.GrassInteractionSystem, Assembly-CSharp", true));
            Set("resolution", 128);
            Set("worldSize", 32f);
            Call(system, "Initialize");
            if (!compute) Set("contactCompute", null);
        }

        Color Sample(float x, float z)
        {
            var texture = new Texture2D(128, 128, TextureFormat.RGBAFloat, false, true);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = Field;
                texture.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                texture.Apply();
                return texture.GetPixel(Mathf.FloorToInt((x / 32 + .5f) * 128), Mathf.FloorToInt((z / 32 + .5f) * 128));
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(texture); }
        }

        [TearDown]
        public void Cleanup()
        {
            // These components stay inactive in EditMode, where Unity need
            // not invoke OnDestroy. Release their GPU allocations explicitly.
            if (system != null)
            {
                foreach (string name in new[] { "field", "scratch", "farField", "farScratch", "permanentField", "permanentScratch" })
                {
                    var texture = system.GetType().GetField(name, Flags).GetValue(system) as RenderTexture;
                    if (texture != null) { texture.Release(); Object.DestroyImmediate(texture); }
                }
                foreach (string name in new[] { "decayMaterial", "stampMaterial", "scrollMaterial" })
                {
                    var material = system.GetType().GetField(name, Flags).GetValue(system) as Material;
                    if (material != null) Object.DestroyImmediate(material);
                }
            }
            if (host != null) Object.DestroyImmediate(host);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ContactIsAlignedDirectionalAndContinuous(bool compute)
        {
            Create(compute);
            Call(system, "Stamp", new Vector3(-7, 0, -5), new Vector3(-3, 0, -5), 1f, 8f, null);
            Color center = Sample(-5, -5);
            Color edge = Sample(-5, -4.4f);
            Assert.That(center.b, Is.GreaterThan(.25f));
            Assert.That(center.r, Is.EqualTo(center.b).Within(.003f));
            Assert.That(Mathf.Abs(center.g), Is.LessThan(.003f), "Bend direction must not drift toward the clear color.");
            Assert.That(edge.b, Is.GreaterThan(.01f).And.LessThan(center.b * .8f));
            Assert.That(Sample(5, 5).b, Is.LessThan(.001f), "No mirrored or shifted footprint.");
            Call(system, "Stamp", new Vector3(-7, 0, -5), new Vector3(-3, 0, -5), 1f, 8f, null);
            Assert.That(Sample(-5, -5).b, Is.EqualTo(center.b).Within(.003f), "Repeated axles must not saturate every edge.");
        }

        [Test]
        public void DecayAndScrollPreserveWorldPositionAndDirection()
        {
            Create(true);
            Call(system, "Stamp", new Vector3(-7, 0, -5), new Vector3(-3, 0, -5), 1f, 8f, null);
            Color original = Sample(-5, -5);
            var decay = new Material(Shader.Find("Hidden/Voyage/GrassInteractionDecay"));
            try
            {
                var scratch = (RenderTexture)system.GetType().GetField("scratch", Flags).GetValue(system);
                decay.SetFloat("_Decay", .5f);
                Graphics.Blit(Field, scratch, decay);
                Call(system, "Swap");
                Assert.That(Sample(-5, -5).b, Is.EqualTo(original.b * .5f).Within(.003f));
                scratch = (RenderTexture)system.GetType().GetField("scratch", Flags).GetValue(system);
                Call(system, "ScrollField", Field, scratch, new Vector2(.25f, .125f));
                Call(system, "Swap");
                Color shifted = Sample(-13, -9);
                Assert.That(shifted.b, Is.EqualTo(original.b * .5f).Within(.003f));
                Assert.That(shifted.r, Is.EqualTo(shifted.b).Within(.003f));
            }
            finally { Object.DestroyImmediate(decay); }
        }

        [Test]
        public void HistoryRestoresTracksAfterLeavingGpuWindow()
        {
            Create(true);
            Call(system, "Stamp", new Vector3(-7, 0, -5), new Vector3(-3, 0, -5), 1f, 8f, null);
            Color original = Sample(-5, -5);
            Call(system, "Clear");
            Set("fieldCenter", new Vector3(500, 0, 500));
            Call(system, "QueueHistoryReplay", Vector3.zero, false);
            Call(system, "ProcessHistoryReplay");
            Assert.That(Sample(-5, -5).b, Is.LessThan(.001f));
            Set("fieldCenter", Vector3.zero);
            Call(system, "QueueHistoryReplay", Vector3.zero, false);
            Call(system, "ProcessHistoryReplay");
            Assert.That(Sample(-5, -5).b, Is.EqualTo(original.b).Within(.005f));
        }

        [Test]
        public void ReturningTrackRecoversByAgeNotCurrentVehicleMotion()
        {
            Create(true);
            Call(system, "Stamp", new Vector3(-7, 0, -5), new Vector3(-3, 0, -5), 1f, 8f, null);
            Color original = Sample(-5, -5);
            var history = (Array)system.GetType().GetField("contactHistory", Flags).GetValue(system);
            object entry = history.GetValue(0);
            entry.GetType().GetField("time").SetValue(entry, Time.time - 10f);
            history.SetValue(entry, 0);
            Call(system, "Clear");
            Call(system, "QueueHistoryReplay", Vector3.zero, false);
            Call(system, "ProcessHistoryReplay");
            Color restored = Sample(-5, -5);
            Assert.That(restored.b, Is.EqualTo(original.b * Mathf.Exp(-.6f)).Within(.004f));
            Assert.That(restored.r, Is.EqualTo(restored.b).Within(.003f));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void StrongCollisionGraduallyBrownsButEmptyFieldDoesNot(bool compute)
        {
            Create(compute);
            var vehicle = new GameObject("Heavy contact");
            var decay = new Material(Shader.Find("Hidden/Voyage/GrassInteractionDecay"));
            try
            {
                vehicle.AddComponent<Rigidbody>().mass=3000;
                Call(system,"Stamp",new Vector3(-7,0,-5),new Vector3(-3,0,-5),1f,12f,vehicle.transform);
                Assert.That(Sample(-5,-5).a,Is.LessThan(.001f),"Damage must fade in, not jump at contact.");
                decay.SetFloat("_Decay",1f); decay.SetFloat("_DamageDelta",.1f);
                var scratch=(RenderTexture)system.GetType().GetField("scratch",Flags).GetValue(system);
                Graphics.Blit(Field,scratch,decay); Call(system,"Swap");
                float first=Sample(-5,-5).a;
                Assert.That(first,Is.GreaterThan(.3f).And.LessThan(.8f));
                scratch=(RenderTexture)system.GetType().GetField("scratch",Flags).GetValue(system);
                Graphics.Blit(Field,scratch,decay); Call(system,"Swap");
                Assert.That(Sample(-5,-5).a,Is.GreaterThan(first));
                Assert.That(Sample(5,5).a,Is.LessThan(.001f));
            }
            finally { Object.DestroyImmediate(vehicle); Object.DestroyImmediate(decay); }
        }

        [Test]
        public void RemoteObjectCanPressAndBrownGrassWithoutEnteringNearWindow()
        {
            Create(true);
            var vehicle=new GameObject("Remote heavy object");
            var decay=new Material(Shader.Find("Hidden/Voyage/GrassInteractionDecay"));
            var pixels=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);
            RenderTexture previous=RenderTexture.active;
            try
            {
                vehicle.AddComponent<Rigidbody>().mass=3000;
                Call(system,"Stamp",new Vector3(195,0,0),new Vector3(205,0,0),3f,12f,vehicle.transform);
                var far=(RenderTexture)system.GetType().GetProperty("FarField").GetValue(system);
                var scratch=(RenderTexture)system.GetType().GetField("farScratch",Flags).GetValue(system);
                decay.SetFloat("_Decay",1);decay.SetFloat("_DamageDelta",.3f);
                Graphics.Blit(far,scratch,decay);Call(system,"SwapFar");
                far=(RenderTexture)system.GetType().GetProperty("FarField").GetValue(system);
                RenderTexture.active=far;
                pixels.ReadPixels(new Rect((int)((200f/1200+.5f)*far.width),far.height/2,1,1),0,0);pixels.Apply();
                Color contact=pixels.GetPixel(0,0);
                Assert.That(contact.b,Is.GreaterThan(.8f));
                Assert.That(contact.a,Is.GreaterThan(.8f),"A remote heavy object must leave the same brown impression.");
                Assert.That(Sample(0,0).b,Is.LessThan(.001f),"Remote contact must not wrap into the near map.");
            }
            finally { RenderTexture.active=previous;Object.DestroyImmediate(vehicle);Object.DestroyImmediate(decay);Object.DestroyImmediate(pixels); }
        }

        [Test]
        public void DistantFieldRetainsVisibleTracksOutsideNearWindow()
        {
            Create(true);
            Call(system, "UpdateFarField");
            Call(system, "Stamp", new Vector3(-7, 0, -5), new Vector3(-3, 0, -5), 1f, 8f, null);
            Set("fieldCenter", new Vector3(200, 0, 0));
            Call(system, "UpdateFarField");
            var far = (RenderTexture)system.GetType().GetProperty("FarField").GetValue(system);
            var world = (Vector4)system.GetType().GetProperty("FarWorldToUv").GetValue(system);
            var texture = new Texture2D(far.width, far.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = far;
                texture.ReadPixels(new Rect(0, 0, far.width, far.height), 0, 0);
                texture.Apply();
                int x = Mathf.FloorToInt(((-5-world.x)/world.z+.5f)*far.width);
                int y = Mathf.FloorToInt(((-5-world.y)/world.z+.5f)*far.height);
                Assert.That(texture.GetPixel(x, y).b, Is.GreaterThan(.2f));
                Assert.That(world.z * .5f, Is.GreaterThan(550f), "Distant state must cover the visible grass range plus the camera offset.");
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(texture); }
        }
    }
}
