using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace Voyage.Tests.Editor
{
    public sealed class TerrainCacheValidationTests
    {
        static Type Cache => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("VoyageTerrainBuildCache")).First(t => t != null);

        static void RequirePackage()
        {
            string folder = "Library/VoyageTerrainBuildCache/" + EditorUserBuildSettings.activeBuildTarget;
            if (!File.Exists(Path.Combine(folder, "voyage-terrain")) || !File.Exists(Path.Combine(folder, "fingerprint.txt")))
                Assert.Ignore("Requires an existing terrain build cache; tests never build or modify it.");
        }

        [Test]
        public void PackageValidationYieldsBeforeWalkingTileDependencies()
        {
            RequirePackage();
            bool completed = false;
            var routine = (IEnumerator)Cache.GetMethod("ValidatePreparedEditorPlayBundle")
                .Invoke(null, new object[] { (Action<string>)(_ => completed = true) });
            try
            {
                Assert.That(routine.MoveNext(), Is.True);
                Assert.That(completed, Is.False, "Entering Play must not scan the whole world synchronously.");
            }
            finally { (routine as IDisposable)?.Dispose(); }
        }

        [Test]
        public void IncrementalValidationMatchesBuildFingerprint()
        {
            RequirePackage();
            object[] syncArgs = { null };
            bool valid = (bool)Cache.GetMethod("TryGetPreparedEditorPlayBundle").Invoke(null, syncArgs);
            bool completed = false;
            string actual = null;
            var routine = (IEnumerator)Cache.GetMethod("ValidatePreparedEditorPlayBundle")
                .Invoke(null, new object[] { (Action<string>)(path => { completed = true; actual = path; }) });
            int slices = 0;
            try { while (routine.MoveNext()) slices++; }
            finally { (routine as IDisposable)?.Dispose(); }
            Assert.That(completed, Is.True);
            Assert.That(slices, Is.GreaterThan(1), "Terrain dependency traversal must be resumable.");
            Assert.That(actual, Is.EqualTo(valid ? syncArgs[0] as string : null),
                "Both paths must accept/reject exactly the same cache fingerprint.");
        }
    }
}
