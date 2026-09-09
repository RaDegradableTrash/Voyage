using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Voyage.Tests.Editor
{
    public class TerrainPackageTests
    {
        static Type Store => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Voyage.TerrainSystem.TerrainPrefabStore")).First(t => t != null);

        [TestCase("TerrainSystem/GeneratedTiles/Terrain_0_0")]
        [TestCase("TerrainSystem/GeneratedTiles/Terrain_8_8")]
        [TestCase("TerrainSystem/GeneratedTiles/Terrain_-20_-30")]
        public void EditorLoadsMigratedTileWithCollisionAndLods(string resource)
        {
            var prefab = (GameObject)Store.GetMethod("LoadInEditor").Invoke(null, new object[] { resource });
            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.transform.Find("LOD0"), Is.Not.Null);
            Assert.That(prefab.GetComponentsInChildren<MeshCollider>(true).Length, Is.GreaterThan(0));
            Assert.That(prefab.GetComponentsInChildren<MeshFilter>(true).Any(f => f.sharedMesh != null), Is.True);
        }

        [Test]
        public void EveryIndexedTileRemainsAvailableOutsideResources()
        {
            var index = Resources.Load("TerrainSystem/TerrainTileIndex");
            Assert.That(index, Is.Not.Null);
            var records = (System.Collections.IEnumerable)index.GetType().GetField("tiles").GetValue(index);
            int count = 0;
            foreach (var record in records)
            {
                var resource = (string)record.GetType().GetField("resourcePath").GetValue(record);
                var path = (string)Store.GetMethod("AssetPath").Invoke(null, new object[] { resource });
                Assert.That(path, Does.Not.Contain("/Resources/"));
                Assert.That(File.Exists(path), Is.True, path);
                Assert.That(File.Exists(path + ".meta"), Is.True, path);
                count++;
            }
            Assert.That(count, Is.EqualTo(9792), "Migration must retain the complete authored map.");
        }
    }
}
