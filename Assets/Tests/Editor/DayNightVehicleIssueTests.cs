using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Voyage.Tests.Editor
{
    public sealed class DayNightVehicleIssueTests
    {
        const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static Type GameType(string name) => Type.GetType(name + ", Assembly-CSharp", true);
        static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Members).Invoke(target, args);
        static void Set(object target, string name, object value) => target.GetType().GetField(name, Members).SetValue(target, value);
        static object Get(object target, string name) => target.GetType().GetField(name, Members).GetValue(target);

        [TestCase(25f, 75f)]
        [TestCase(1000f, 100f)]
        [TestCase(-1f, 50f)]
        [TestCase(float.NaN, 50f)]
        [TestCase(float.PositiveInfinity, 50f)]
        public void SharedFuelAcceptsOnlyFinitePositiveAmountsAndClampsCapacity(float amount, float expected)
        {
            Type tank = GameType("FuelTank");
            PropertyInfo fuel = tank.GetProperty("SharedFuel");
            object before = fuel.GetValue(null);
            try
            {
                fuel.SetValue(null, 50f);
                tank.GetMethod("AddSharedFuel").Invoke(null, new object[] { amount });
                Assert.That(fuel.GetValue(null), Is.EqualTo(expected));
            }
            finally { fuel.SetValue(null, before); }
        }

        [Test]
        public void EmptyFuelShutdownPreservesDriveGearAndVelocityAndRefillRestarts()
        {
            var go = new GameObject("Fuel regression");
            PropertyInfo fuel = GameType("FuelTank").GetProperty("SharedFuel");
            object before = fuel.GetValue(null);
            try
            {
                go.SetActive(false);
                var body = go.AddComponent<Rigidbody>();
                var car = go.AddComponent(GameType("CarControl"));
                var start = go.AddComponent(GameType("StartProcedure"));
                Set(start, "carControl", car);
                Set(car, "startProcedure", start);
                Call(start, "ForceStartVehicle");
                Set(car, "currentGear", Enum.Parse(GameType("CarControl+GearMode"), "Drive"));
                go.SetActive(true);
                body.linearVelocity = new Vector3(0, 0, 12);
                Assert.That(body.linearVelocity.z, Is.EqualTo(12f), "Fixture must start with a moving rigidbody.");
                fuel.SetValue(null, 0f);
                Call(car, "HandleFuelConsumption", 1f);
                Assert.That(car.GetType().GetProperty("EngineOn").GetValue(car), Is.False);
                Assert.That(car.GetType().GetProperty("CurrentGear").GetValue(car).ToString(), Is.EqualTo("Drive"));
                Assert.That(body.linearVelocity.z, Is.EqualTo(12f));
                Call(car, "AddFuel", 25f);
                Assert.That(fuel.GetValue(null), Is.EqualTo(25f));
                Assert.That(car.GetType().GetProperty("EngineOn").GetValue(car), Is.True);
            }
            finally { Object.DestroyImmediate(go); fuel.SetValue(null, before); }
        }

        [Test]
        public void EngineAudioIsSilentWithoutThrottleEvenAtHighRpm()
        {
            var go = new GameObject("Audio regression");
            try
            {
                go.SetActive(false);
                var car = go.AddComponent(GameType("CarControl"));
                Set(car, "smoothEngineRpm", 2000f);
                Set(car, "engineLoad", 1f);
                Set(car, "samplingRate", 48000);
                var samples = new float[1024];
                for (int i = 0; i < samples.Length; i++) samples[i] = 1;
                Call(car, "OnAudioFilterRead", samples, 2);
                Assert.That(samples, Is.All.EqualTo(0f));
                Set(car, "engineSoundRequested", true);
                Call(car, "OnAudioFilterRead", samples, 2);
                Assert.That(Array.Exists(samples, x => Mathf.Abs(x) > .00001f), Is.True);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [TestCase("/time NaN")]
        [TestCase("/time Infinity")]
        [TestCase("/time -1")]
        [TestCase("/time 25")]
        [TestCase("/time")]
        [TestCase("/time 12 extra")]
        [TestCase("/fuel -5")]
        [TestCase("/unknown 12")]
        public void InvalidCommandsAreRejected(string command)
        {
            object[] args = { command, null };
            Assert.That(GameType("VoyageCommandConsole").GetMethod("Execute").Invoke(null, args), Is.False);
            Assert.That(args[1], Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void SkyAndTerrainRenderAcrossCycleWithoutCelestialMeshes()
        {
            var root = new GameObject("Lighting regression");
            var cameraObject = new GameObject("Main Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.tag = "MainCamera";
            Material oldSky = RenderSettings.skybox;
            RenderTexture oldActive = RenderTexture.active;
            var rt = new RenderTexture(320, 180, 24);
            var capture = new Texture2D(320, 180, TextureFormat.RGB24, false);
            GameObject ground = null;
            Material groundMaterial = null;
            GameObject occluder = null;
            Material occluderMaterial = null;
            Component lighting = null;
            try
            {
                root.SetActive(false);
                lighting = root.AddComponent(GameType("Voyage.Lighting.DayNightSystem"));
                Set(lighting, "advanceTime", false);
                Set(lighting, "manageOtherDirectionalLights", false);
                root.SetActive(true);
                // EditMode does not run lifecycle callbacks on ordinary MonoBehaviours.
                Call(lighting, "OnEnable");
                Assert.That(camera.clearFlags, Is.EqualTo(CameraClearFlags.Skybox));
                Assert.That(root.GetComponentsInChildren<MeshRenderer>(), Is.Empty);
                Assert.That(ShaderUtil.ShaderHasError(RenderSettings.skybox.shader), Is.False);
                Shader terrainShader = Shader.Find("Voyage/Terrain/Stylized");
                Assert.That(ShaderUtil.ShaderHasError(terrainShader), Is.False);
                camera.targetTexture = rt;
                camera.transform.SetPositionAndRotation(new Vector3(0, 3, 0), Quaternion.LookRotation(new Vector3(1, .3f, .18f)));
                Directory.CreateDirectory("Logs/IssueValidation");
                object[] commandArgs = { "/time 8", null };
                Assert.That(GameType("VoyageCommandConsole").GetMethod("Execute").Invoke(null, commandArgs), Is.True);
                Assert.That(Get(lighting, "currentTime"), Is.EqualTo(8f));
                Color[] first = Capture(camera, rt, capture);
                float brightest = 0, darkest = 1;
                foreach (Color pixel in first)
                {
                    brightest = Mathf.Max(brightest, pixel.grayscale);
                    darkest = Mathf.Min(darkest, pixel.grayscale);
                }
                Assert.That(brightest - darkest, Is.GreaterThan(.1f), "Sky must contain a visible solar disc, not a solid clear color.");
                camera.transform.position += new Vector3(1000, 200, -1000);
                Color[] moved = Capture(camera, rt, capture);
                float difference = 0f;
                for (int i = 0; i < first.Length; i++) difference += Mathf.Abs(first[i].r - moved[i].r);
                Assert.That(difference / first.Length, Is.LessThan(.001f), "Sky must have no translation parallax.");
                camera.transform.position = new Vector3(0, 3, 0);
                camera.transform.rotation = Quaternion.LookRotation(new Vector3(-1, .3f, -.18f));
                Color[] away = Capture(camera, rt, capture);
                float awayBrightest = 0;
                foreach (Color pixel in away) awayBrightest = Mathf.Max(awayBrightest, pixel.grayscale);
                Assert.That(awayBrightest, Is.LessThan(brightest * .8f), "Turning away must move the solar disc outside the view.");

                Call(lighting, "SetTime", 20f);
                Vector3 moonDirection = RenderSettings.skybox.GetVector("_MoonDirection");
                camera.transform.rotation = Quaternion.LookRotation(moonDirection);
                Capture(camera, rt, capture);
                float moonPixel = capture.GetPixel(160, 90).grayscale;
                Assert.That(moonPixel, Is.GreaterThan(.25f), "Moon must be visible in its world direction.");
                File.WriteAllBytes("Logs/IssueValidation/sky-moon.png", capture.EncodeToPNG());
                occluder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                occluder.transform.position = camera.transform.position + moonDirection * 5;
                occluder.transform.localScale = Vector3.one * 2;
                occluderMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                occluderMaterial.SetColor("_BaseColor", Color.black);
                occluder.GetComponent<Renderer>().sharedMaterial = occluderMaterial;
                Capture(camera, rt, capture);
                Assert.That(capture.GetPixel(160, 90).grayscale, Is.LessThan(moonPixel * .5f), "Foreground geometry must occlude celestial discs.");
                Object.DestroyImmediate(occluder);
                camera.transform.rotation = Quaternion.LookRotation(new Vector3(1, .3f, .18f));
                ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.transform.localScale = Vector3.one * 30;
                groundMaterial = new Material(terrainShader);
                ground.GetComponent<Renderer>().sharedMaterial = groundMaterial;
                float dayGround = 0, nightGround = 0;
                foreach (float hour in new[] { 8f, 12f, 18f, 0f })
                {
                    Call(lighting, "SetTime", hour);
                    Capture(camera, rt, capture);
                    File.WriteAllBytes($"Logs/IssueValidation/sky-{hour:00}.png", capture.EncodeToPNG());
                    if (hour == 12f) dayGround = capture.GetPixel(160, 10).grayscale;
                    if (hour == 0f) nightGround = capture.GetPixel(160, 10).grayscale;
                }
                Assert.That(dayGround, Is.GreaterThan(nightGround * 1.2f), "Ground must darken with grass at night.");
                Assert.That(Shader.GetGlobalFloat("_VoyageGrassEnvironmentLight"), Is.EqualTo(.48f).Within(.001f));
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = oldActive;
                Object.DestroyImmediate(ground);
                Object.DestroyImmediate(groundMaterial);
                Object.DestroyImmediate(occluder);
                Object.DestroyImmediate(occluderMaterial);
                if (lighting != null) Call(lighting, "OnDisable");
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(capture);
                RenderSettings.skybox = oldSky;
            }
        }

        static Color[] Capture(Camera camera, RenderTexture target, Texture2D image)
        {
            camera.Render();
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            return image.GetPixels();
        }
    }
}
