using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Voyage.Tests.Editor
{
    // The game uses Unity's predefined Assembly-CSharp. Resolve it at runtime
    // so this isolated test asmdef does not force an assembly migration.
    public sealed class TerrainStreamingTests
    {
        const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        readonly List<Object> cleanup = new List<Object>();
        static Type GameType(string name) => Type.GetType(name + ", Assembly-CSharp", true);
        static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Members).Invoke(target, args);
        static void Set(object target, string field, object value) => target.GetType().GetField(field, Members).SetValue(target, value);
        static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Members).GetValue(target);
        T Keep<T>(T item) where T : Object { cleanup.Add(item); return item; }
        ScriptableObject Settings() => Keep(ScriptableObject.CreateInstance(GameType("Voyage.TerrainSystem.TerrainChunkSettings")));

        [TearDown]
        public void TearDown()
        {
            for (int i = cleanup.Count - 1; i >= 0; i--)
                if (cleanup[i] != null) Object.DestroyImmediate(cleanup[i]);
            cleanup.Clear();
        }

        [TestCase(50f, 0)]
        [TestCase(150f, 1)]
        [TestCase(350f, 2)]
        [TestCase(800f, 3)]
        public void InitialLodMatchesNextUpdate(float distance, int expected)
        {
            ScriptableObject settings = Settings();
            GameObject root = Keep(new GameObject("LOD regression"));
            root.SetActive(false);
            Component tile = root.AddComponent(GameType("Voyage.TerrainSystem.TerrainTileRuntime"));
            object record = Activator.CreateInstance(GameType("Voyage.TerrainSystem.TerrainTileRecord"));
            Set(record, "bounds", new Bounds(Vector3.zero, Vector3.zero));
            MethodInfo initialize = tile.GetType().GetMethod("Initialize", new[] { record.GetType(), settings.GetType(), typeof(bool), typeof(Vector3) });
            Vector3 viewer = Vector3.right * distance;
            initialize.Invoke(tile, new[] { record, settings, false, (object)viewer });
            Assert.That(Get<int>(tile, "currentLod"), Is.EqualTo(expected));
            Call(tile, "UpdateLod", viewer);
            Assert.That(Get<int>(tile, "currentLod"), Is.EqualTo(expected), "The first update must not switch a just-loaded tile.");
        }

        [Test]
        public void LodDeadBandDoesNotChatter()
        {
            GameObject root = Keep(new GameObject("LOD dead band"));
            root.SetActive(false);
            Component tile = root.AddComponent(GameType("Voyage.TerrainSystem.TerrainTileRuntime"));
            Set(tile, "settings", Settings());
            Set(tile, "bounds", new Bounds(Vector3.zero, Vector3.zero));
            Call(tile, "SetLod", 0, true);
            foreach (float distance in new[] { 89f, 91f, 88f, 95f })
            {
                Call(tile, "UpdateLod", Vector3.right * distance);
                Assert.That(Get<int>(tile, "currentLod"), Is.Zero);
            }
            Call(tile, "UpdateLod", Vector3.right * 110f);
            Assert.That(Get<int>(tile, "currentLod"), Is.EqualTo(1));
            Call(tile, "UpdateLod", Vector3.right * 89f);
            Assert.That(Get<int>(tile, "currentLod"), Is.EqualTo(1));
        }

        [Test]
        public void GrassLodChangesPreserveVisibility()
        {
            GameObject root = Keep(new GameObject("Grass visibility"));
            root.SetActive(false);
            Component grass = root.AddComponent(GameType("Voyage.TerrainSystem.InteractiveGrassTile"));
            Set(grass, "tileFade", 0.73f);
            foreach (int lod in new[] { 0, 1, 2, 1, 0 })
            {
                Call(grass, "SetLod", lod);
                Assert.That(Get<float>(grass, "tileFade"), Is.EqualTo(0.73f));
            }
        }

        [Test]
        public void PrefetchedGrassCanUpdateBeforeInitialization()
        {
            GameObject root = Keep(new GameObject("Deferred grass"));
            root.SetActive(false);
            Component grass = root.AddComponent(GameType("Voyage.TerrainSystem.InteractiveGrassTile"));
            Assert.DoesNotThrow(() => Call(grass, "LateUpdate"));
            Assert.That(Get<MaterialPropertyBlock>(grass, "instanceProperties"), Is.Null);
        }

        [TestCase(0f)]
        [TestCase(1500f)]
        public void PreloadSquareContainsFadeCircleAtBothCellEdges(float visualOverride)
        {
            ScriptableObject settings = Settings();
            Set(settings, "visualDistanceOverride", visualOverride);
            float radius = (float)Call(settings, "GetVisualDistance") + Get<float>(settings, "visualTileMargin");
            int preload = (int)Call(settings, "GetPreloadRadius");
            float tileSize = Get<float>(settings, "tileSize");
            foreach (float x in new[] { -256.001f, -255.999f, -0.001f, 0.001f, 255.999f, 256.001f })
            {
                Vector2Int cell = (Vector2Int)Call(settings, "WorldToTile", new Vector3(x, 0f, x));
                Assert.That((cell.x - preload) * tileSize, Is.LessThanOrEqualTo(x - radius));
                Assert.That((cell.x + preload + 1) * tileSize, Is.GreaterThanOrEqualTo(x + radius));
            }
        }

        [Test]
        public void DensityIsContinuousAndMonotonicAcrossFormerLodBoundaries()
        {
            MethodInfo density = GameType("Voyage.TerrainSystem.InteractiveGrassTile").GetMethod("DistanceDensity");
            float previous = 1f;
            for (float distance = 0f; distance <= 700f; distance += 0.25f)
            {
                float current = (float)density.Invoke(null, new object[] { distance, 105f, 495f });
                Assert.That(current, Is.InRange(0.03999f, previous + 0.00001f));
                Assert.That(previous - current, Is.LessThan(0.002f));
                previous = current;
            }
        }

        [Test]
        public void StreamingWorkerYieldsBeforeAnyBoundaryWork()
        {
            GameObject root = Keep(new GameObject("Streaming scheduler"));
            root.SetActive(false);
            Component core = root.AddComponent(GameType("DrivingCore"));
            IEnumerator worker = (IEnumerator)Call(core, "ProcessTerrainLoads", Settings());
            Assert.That(worker.MoveNext(), Is.True);
            Assert.That(worker.Current, Is.Null, "The cell-crossing frame must not perform IO or instantiate tiles.");
            Assert.That(worker.MoveNext(), Is.False);
        }

        [Test]
        public void VisualLodNeverChangesCollisionMeshOrContactHeight()
        {
            GameObject root = Keep(new GameObject("Terrain contact"));
            GameObject collision = new GameObject("Collision");
            collision.transform.SetParent(root.transform);
            Mesh mesh = Keep(new Mesh());
            mesh.vertices = new[] { new Vector3(-5, 0, -5), new Vector3(-5, 0, 5), new Vector3(5, 0, 5), new Vector3(5, 0, -5) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            MeshCollider collider = collision.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            Component tile = root.AddComponent(GameType("Voyage.TerrainSystem.TerrainTileRuntime"));
            Physics.SyncTransforms();
            foreach (int lod in new[] { 0, 1, 2, 3, 2, 0 })
            {
                Call(tile, "SetLod", lod, true);
                Assert.That(collider.enabled && collider.gameObject.activeInHierarchy, Is.True);
                Assert.That(collider.sharedMesh, Is.SameAs(mesh));
                RaycastHit hit;
                Assert.That(collider.Raycast(new Ray(Vector3.up * 3f, Vector3.down), out hit, 5f), Is.True);
                Assert.That(hit.point.y, Is.EqualTo(0f).Within(0.0001f));
            }
        }

        [Test]
        public void GpuGrassSelectionMatchesCpuIncludingNegativeCoordinates()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Compute shaders unavailable on this device.");
            ComputeShader shader = Resources.Load<ComputeShader>("TerrainSystem/GrassCulling");
            Assert.That(shader, Is.Not.Null);
            MethodInfo density = GameType("Voyage.TerrainSystem.InteractiveGrassTile").GetMethod("DistanceDensity");
            MethodInfo selection = GameType("Voyage.TerrainSystem.InteractiveGrassTile").GetMethod("DistanceSelection");
            Matrix4x4[] matrices = new Matrix4x4[1024];
            int expected = 0;
            for (int i = 0; i < matrices.Length; i++)
            {
                Vector3 position = new Vector3((i % 32 - 16) * 21.3f, 0f, (i / 32 - 16) * 19.7f);
                matrices[i] = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
                float value = (float)density.Invoke(null, new object[] { position.magnitude, 105f, 495f });
                if (position.magnitude <= 498f && (float)selection.Invoke(null, new object[] { position }) <= value + 0.08f) expected++;
            }
            using (ComputeBuffer source = new ComputeBuffer(matrices.Length, 64))
            using (ComputeBuffer visible = new ComputeBuffer(matrices.Length, 64, ComputeBufferType.Append))
            using (ComputeBuffer count = new ComputeBuffer(1, 4, ComputeBufferType.Raw))
            {
                source.SetData(matrices);
                visible.SetCounterValue(0);
                int kernel = shader.FindKernel("CSMain");
                shader.SetBuffer(kernel, "_SourceMatrices", source);
                shader.SetBuffer(kernel, "_VisibleMatrices", visible);
                shader.SetVector("_CameraPosition", Vector4.zero);
                Vector4[] planes = new Vector4[6];
                for (int i = 0; i < planes.Length; i++) planes[i] = new Vector4(0, 0, 0, 10000);
                shader.SetVectorArray("_FrustumPlanes", planes);
                shader.SetFloat("_MaxDistance", 495f);
                shader.SetFloat("_InstanceRadius", 3f);
                shader.SetFloat("_DensityNearDistance", 105f);
                shader.Dispatch(kernel, matrices.Length / 64, 1, 1);
                ComputeBuffer.CopyCount(visible, count, 0);
                int[] actual = new int[1];
                count.GetData(actual);
                Assert.That(actual[0], Is.EqualTo(expected));
            }
        }

        [TestCase("Voyage/Terrain/Stylized")]
        [TestCase("Voyage/Grass/InteractiveLit")]
        public void TerrainShadersCompile(string name)
        {
            Shader shader = Shader.Find(name);
            Assert.That(shader, Is.Not.Null);
            Material material = Keep(new Material(shader));
            ShaderUtil.CompilePass(material, 0, true);
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
                Assert.That(message.severity.ToString(), Is.Not.EqualTo("Error"), message.message);
            Assert.That(shader.isSupported, Is.True);
        }

        [Test]
        public void RenderedLodMasksAreComplementaryAndDistanceFadeIsGradual()
        {
            Material material = Keep(new Material(Shader.Find("Voyage/Terrain/Stylized")));
            Mesh quad = Keep(new Mesh());
            quad.vertices = new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) };
            quad.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            // Include both windings so this regression is independent of GPU projection flips.
            quad.triangles = new[] { 0, 1, 2, 0, 2, 3, 2, 1, 0, 3, 2, 0 };
            Vector4 oldView = Shader.GetGlobalVector("_VoyageTerrainView");
            try
            {
                Shader.SetGlobalVector("_VoyageTerrainView", Vector4.zero);
                material.SetFloat("_TerrainLodProgress", 1f);
                int full = RenderCoverage(material, quad);
                Assert.That(full, Is.GreaterThan(4000));
                material.SetFloat("_TerrainLodProgress", 0.5f);
                int incoming = RenderCoverage(material, quad);
                material.SetFloat("_TerrainLodOutgoing", 1f);
                int outgoing = RenderCoverage(material, quad);
                Assert.That(incoming, Is.InRange(full * 0.45f, full * 0.55f));
                Assert.That(incoming + outgoing, Is.EqualTo(full).Within(2));
                material.SetFloat("_TerrainLodProgress", 1f);
                material.SetFloat("_TerrainLodOutgoing", 0f);
                Shader.SetGlobalVector("_VoyageTerrainView", new Vector4(0, 0, 0.2f, 0.8f));
                int distanceFade = RenderCoverage(material, quad);
                Assert.That(distanceFade, Is.InRange(full * 0.35f, full * 0.65f));
            }
            finally { Shader.SetGlobalVector("_VoyageTerrainView", oldView); }
        }

        int RenderCoverage(Material material, Mesh quad)
        {
            RenderTexture target = RenderTexture.GetTemporary(64, 64, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            Texture2D pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = target;
                GL.Clear(true, true, Color.black);
                GL.PushMatrix();
                try
                {
                    GL.LoadOrtho();
                    Assert.That(material.SetPass(0), Is.True);
                    Graphics.DrawMeshNow(quad, Matrix4x4.identity);
                }
                finally { GL.PopMatrix(); }
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                pixels.Apply();
                int count = 0;
                foreach (Color pixel in pixels.GetPixels())
                    if (Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b)) > 0.025f) count++;
                return count;
            }
            finally
            {
                Object.DestroyImmediate(pixels);
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
