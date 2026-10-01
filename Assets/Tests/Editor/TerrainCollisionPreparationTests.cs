using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Voyage.Tests.Editor
{
    public class TerrainCollisionPreparationTests
    {
        [UnityTest]
        public IEnumerator ReportedTerrainTileKeepsPreparedCollisionAcrossAwakeAndLods()
        {
            Type preparation = Type.GetType("Voyage.TerrainSystem.TerrainCollisionPreparation, Assembly-CSharp", true);
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/TerrainSystem/GeneratedTiles/RuntimeTiles/Terrain_-1_16.prefab");
            Assert.That(asset, Is.Not.Null);
            // Clone inactive so the unprepared source cannot run Awake first.
            GameObject parent = new GameObject("Collision test parent");
            parent.SetActive(false);
            GameObject root = UnityEngine.Object.Instantiate(asset, parent.transform);
            try
            {
                Mesh visual = root.transform.Find("LOD0").GetComponent<MeshFilter>().sharedMesh;
                yield return (IEnumerator)preparation.GetMethod("Prepare").Invoke(null, new object[] { root });
                MeshCollider collider = root.transform.Find("Collision").GetComponent<MeshCollider>();
                Mesh prepared = collider.sharedMesh;
                Assert.That(prepared, Is.Not.SameAs(visual));
                Vector3[] points = prepared.vertices;
                int[] indices = prepared.triangles;
                for (int i=0;i<indices.Length;i+=3)
                    for (int j=0;j<3;j++)
                        Assert.That(Vector3.Distance(points[indices[i+j]],points[indices[i+(j+1)%3]]),
                            Is.LessThanOrEqualTo(450.001f));
                Type tileType = Type.GetType("Voyage.TerrainSystem.TerrainTileRuntime, Assembly-CSharp",true);
                Component tile = root.GetComponent(tileType);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                tileType.GetMethod("Awake",flags).Invoke(tile,null);
                foreach(int lod in new[] {0,1,2,3,0})
                {
                    tileType.GetMethod("SetLod",flags).Invoke(tile,new object[]{lod,true});
                    Assert.That(collider.sharedMesh,Is.SameAs(prepared));
                    Assert.That(root.transform.Find("LOD0").GetComponent<MeshFilter>().sharedMesh,Is.SameAs(visual));
                }
                yield return (IEnumerator)preparation.GetMethod("Prepare").Invoke(null,new object[]{root});
                Assert.That(collider.sharedMesh,Is.SameAs(prepared),"Reload must reuse prepared collision.");
            }
            finally { UnityEngine.Object.DestroyImmediate(parent); }
        }

        static object Subdivide(Vector3[] vertices, int[] triangles, float edge) =>
            Type.GetType("Voyage.TerrainSystem.TerrainCollisionPreparation, Assembly-CSharp", true)
                .GetMethod("Subdivide").Invoke(null, new object[] { vertices, triangles, edge });
        static T Field<T>(object data, string name) => (T)data.GetType().GetField(name).GetValue(data);

        [Test]
        public void LargeSlopedTrianglesKeepSurfaceAndWindingWithBoundedEdges()
        {
            Vector3[] original = { new Vector3(0,0,0), new Vector3(0,600,800), new Vector3(1000,200,0) };
            int[] sourceIndices = { 0, 1, 2 };
            object result = Subdivide(original, sourceIndices, 450f);
            Vector3[] points = Field<Vector3[]>(result, "vertices");
            int[] indices = Field<int[]>(result, "triangles");
            Vector3 normal = Vector3.Cross(original[1], original[2]);
            double area = 0;
            Assert.That(Field<bool>(result, "changed"), Is.True);
            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3 a = points[indices[i]], b = points[indices[i+1]], c = points[indices[i+2]];
                Assert.That(Vector3.Distance(a,b), Is.LessThanOrEqualTo(450.001f));
                Assert.That(Vector3.Distance(b,c), Is.LessThanOrEqualTo(450.001f));
                Assert.That(Vector3.Distance(c,a), Is.LessThanOrEqualTo(450.001f));
                Vector3 cross = Vector3.Cross(b-a,c-a);
                Assert.That(Vector3.Dot(cross,normal), Is.GreaterThan(0));
                area += cross.magnitude;
            }
            foreach (Vector3 point in points)
                Assert.That(Mathf.Abs(Vector3.Dot(point,normal.normalized)), Is.LessThan(.001f));
            Assert.That(area, Is.EqualTo(normal.magnitude).Within(.2));
            CollectionAssert.AreEqual(new[] {0,1,2}, sourceIndices);
        }

        [Test]
        public void SmallTrianglesReuseOriginalArrays()
        {
            Vector3[] vertices = { Vector3.zero, Vector3.forward, Vector3.right };
            int[] indices = {0,1,2};
            object result = Subdivide(vertices, indices, 450f);
            Assert.That(Field<bool>(result,"changed"), Is.False);
            Assert.That(Field<Vector3[]>(result,"vertices"), Is.SameAs(vertices));
            Assert.That(Field<int[]>(result,"triangles"), Is.SameAs(indices));
        }
    }
}
