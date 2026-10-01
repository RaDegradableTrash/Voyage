using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Voyage.Tests.Editor
{
    public sealed class DrivingHitchRegressionTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static Type GameType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(name)).First(t => t != null);

        [Test]
        public void CommandHistoryRestoresDraftAndClampsAtOldestEntry()
        {
            Type type = GameType("VoyageCommandHistory");
            object history = Activator.CreateInstance(type);
            string Call(string method, string input) => (string)type.GetMethod(method).Invoke(history, new object[] { input });
            Assert.That(Call("Previous", "/draft"), Is.EqualTo("/draft"));
            Call("Record", "/time 18"); Call("Record", "/chunk border"); Call("Record", "/chunk border");
            Assert.That(Call("Previous", "/fuel 2"), Is.EqualTo("/chunk border"));
            Assert.That(Call("Previous", ""), Is.EqualTo("/time 18"));
            Assert.That(Call("Previous", ""), Is.EqualTo("/time 18"));
            Assert.That(Call("Next", ""), Is.EqualTo("/chunk border"));
            Assert.That(Call("Next", ""), Is.EqualTo("/fuel 2"));
            Assert.That(Call("Next", "/fuel 25"), Is.EqualTo("/fuel 25"));
        }

        [UnityTest]
        public IEnumerator BackgroundTrackSavePreservesNewSamplesAndRetriesFailedWrites()
        {
            Type type = GameType("Voyage.TerrainSystem.GrassPermanentTrackStore");
            var go = new GameObject("Track persistence regression");
            var store = (MonoBehaviour)go.AddComponent(type);
            store.enabled = false;
            string directory = Path.Combine(Path.GetTempPath(), "VoyageTrackTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "tracks.json");
            FieldInfo dirty = type.GetField("dirty", Private);
            FieldInfo pending = type.GetField("pendingSave", Private);
            void Invoke(string method, params object[] args) => type.GetMethod(method, Private).Invoke(store, args);
            void Record(float x) => type.GetMethod("RecordSegment").Invoke(store,
                new object[] { new Vector3(x, 0, 0), new Vector3(x, 0, 0), .4f, 1f, null });
            try
            {
                type.GetField("fileName").SetValue(store, path);
                Record(123);
                Invoke("BeginSave");
                var first = (Task)pending.GetValue(store);
                Record(456);
                Invoke("BeginSave");
                Assert.That(pending.GetValue(store), Is.SameAs(first), "Only one writer may own the temporary file.");
                float deadline = Time.realtimeSinceStartup + 10;
                while (!first.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(first.IsCompleted, Is.True);
                Invoke("CompletePendingSave", false);
                Assert.That(dirty.GetValue(store), Is.True, "Completion must preserve newer samples.");
                Invoke("Save");
                Assert.That(dirty.GetValue(store), Is.False);
                Assert.That(File.ReadAllText(path), Does.Contain("456"));

                type.GetField("fileName").SetValue(store, Path.Combine(directory, "missing", "tracks.json"));
                Record(789);
                Invoke("BeginSave");
                var failed = (Task)pending.GetValue(store);
                deadline = Time.realtimeSinceStartup + 10;
                while (!failed.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(failed.IsCompleted, Is.True);
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Grass tracks could not be saved:"));
                Invoke("CompletePendingSave", false);
                Assert.That(dirty.GetValue(store), Is.True);
                type.GetField("fileName").SetValue(store, path);
                Invoke("Save");
                Assert.That(dirty.GetValue(store), Is.False);
                Assert.That(File.ReadAllText(path), Does.Contain("789"));
            }
            finally
            {
                type.GetField("fileName").SetValue(store, path);
                Invoke("Save");
                UnityEngine.Object.DestroyImmediate(go);
                Directory.Delete(directory, true);
            }
        }
    }
}
