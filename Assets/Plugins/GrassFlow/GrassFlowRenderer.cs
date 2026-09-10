// Adapted from Mithzzx/Project-GrassFlow (MIT); see LICENSE.txt and UPSTREAM.md.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GrassFlow
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class GrassFlowRenderer : MonoBehaviour
    {
        public GrassFlowPatch patch;
        ComputeShader compute;
        Material material;
        ComputeBuffer source;
        readonly Mesh[] meshes = new Mesh[3];
        readonly Dictionary<Camera, CameraBuffers> cameras = new Dictionary<Camera, CameraBuffers>();
        readonly Plane[] planes = new Plane[6];
        readonly Vector4[] planeVectors = new Vector4[6];
        int generate, cull, capacity, revision = -1;
        GrassFlowPatch.GrassStyle activeStyle;
        static int preparationFrame = -1;
        static readonly Unity.Profiling.ProfilerMarker PrepareMarker = new Unity.Profiling.ProfilerMarker("Voyage.Grass.Prepare");
        public bool Ready => source != null;

        sealed class CameraBuffers
        {
            public readonly ComputeBuffer[] visible = new ComputeBuffer[3];
            public readonly ComputeBuffer[] args = new ComputeBuffer[3];
            public readonly MaterialPropertyBlock[] properties = new MaterialPropertyBlock[3];
            public void Release() { for (int i = 0; i < 3; i++) { visible[i]?.Release(); args[i]?.Release(); } }
        }

        void OnEnable() => RenderPipelineManager.beginCameraRendering += Render;
        void OnDisable() { RenderPipelineManager.beginCameraRendering -= Render; Release(); }
        void OnValidate() => revision = -1;

        void Update()
        {
            if (!Application.isPlaying || patch == null) return;
            var camera = Camera.main;
            if (camera == null || patch.bounds.SqrDistance(camera.transform.position) > Mathf.Pow(patch.drawDistance + 128f, 2)) return;
            PrepareForCamera(camera);
        }

        // Prepare before visibility, with one patch per frame across all tiles.
        // Avoid several megabytes of allocation/upload in beginCameraRendering.
        public bool PrepareForCamera(Camera camera)
        {
            using var prepareScope = PrepareMarker.Auto();
            if (patch == null || camera == null) return false;
            bool ready = source != null && capacity == patch.Capacity && activeStyle == patch.style && revision == patch.revision && cameras.ContainsKey(camera);
            if (ready) return true;
            if (Application.isPlaying && preparationFrame == Time.frameCount) return false;
            if (Application.isPlaying) preparationFrame = Time.frameCount;
            Initialize();
            if (source == null) return false;
            if (revision != patch.revision) Generate();
            EnsureCameraBuffers(camera);
            return true;
        }

        void Initialize()
        {
            if (patch == null || patch.surface == null || patch.density == null || !SystemInfo.supportsComputeShaders) return;
            if (source != null && capacity == patch.Capacity && activeStyle == patch.style) return;
            Release();
            var template = Resources.Load<ComputeShader>("GrassFlow/GrassCompute");
            var shader = Resources.Load<Shader>("GrassFlow/GrassShader");
            if (template == null || shader == null) return;
            compute = Instantiate(template);
            material = new Material(shader) { enableInstancing = true, hideFlags = HideFlags.HideAndDontSave };
            generate = compute.FindKernel("CSMain");
            cull = compute.FindKernel("CSCull");
            capacity = patch.Capacity;
            activeStyle = patch.style;
            source = new ComputeBuffer(capacity, 24);
            for (int i = 0; i < 3; i++) meshes[i] = activeStyle == GrassFlowPatch.GrassStyle.Aloe ? TuftMesh(i) : MeadowMesh(i);
        }

        void Generate()
        {
            bool meadow = patch.style == GrassFlowPatch.GrassStyle.GoldenMeadow;
            material.SetColor("_RootColor", meadow ? patch.meadowRoot : patch.rootColor);
            material.SetColor("_MidColor", meadow ? patch.meadowLeaf : patch.leafColor);
            material.SetColor("_TipColor", meadow ? patch.meadowTip : patch.tipColor);
            if (meadow)
            {
                // Use the same explicit linear data convention as the HLSL ground palette.
                material.SetVector("_MeadowLeafData", patch.meadowLeaf);
                material.SetVector("_MeadowTipData", patch.meadowTip);
            }
            material.SetFloat("_WindGustStrength", patch.windStrength);
            material.SetFloat("_MeadowStyle", meadow ? 1 : 0);
            material.SetTexture("_Surface", patch.surface);
            material.SetTexture("_PaintDensity", patch.density);
            material.SetVector("_PatchMin", patch.bounds.min);
            material.SetVector("_PatchSize", patch.bounds.size);
            compute.SetFloat("_NaturalDensityVariation", meadow ? 1 : 0);
            compute.SetInt("_GrassCount", capacity);
            compute.SetInt("_GrassPerRow", Mathf.RoundToInt(Mathf.Sqrt(capacity)));
            compute.SetFloat("_TerrainSize", patch.bounds.size.x);
            compute.SetVector("_ObjectPosition", patch.bounds.center);
            compute.SetVector("_TerrainPos", patch.bounds.min);
            compute.SetVector("_TerrainSizeData", patch.bounds.size);
            compute.SetFloat("_MinHeight", patch.minHeight);
            compute.SetFloat("_MaxHeight", Mathf.Max(patch.minHeight, patch.maxHeight));
            compute.SetFloat("_ClumpScale", .05f);
            compute.SetFloat("_ClumpStrength", .3f);
            compute.SetFloat("_HeightScale", .05f);
            compute.SetFloat("_HeightFactor", .7f);
            compute.SetFloat("_AngleVariation", 1f);
            compute.SetBool("_UseDensityMap", true);
            compute.SetFloat("_DensityThreshold", .001f);
            compute.SetTexture(generate, "_HeightMap", patch.surface);
            compute.SetTexture(generate, "_DensityMap", patch.density);
            compute.SetBuffer(generate, "grassDataBuffer", source);
            compute.Dispatch(generate, (capacity + 255) / 256, 1, 1);
            revision = patch.revision;
        }

        void Render(ScriptableRenderContext context, Camera camera)
        {
            if (patch == null || camera == null || (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)) return;
            if ((camera.cullingMask & (1 << gameObject.layer)) == 0) return;
            Bounds bounds = patch.bounds;
            bounds.Expand(Mathf.Max(patch.maxHeight * 3f, 5f));
            if (bounds.SqrDistance(camera.transform.position) > patch.drawDistance * patch.drawDistance) return;
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            if (!GeometryUtility.TestPlanesAABB(planes, bounds)) return;
            if (Application.isPlaying && camera.cameraType == CameraType.Game)
            {
                if (camera != Camera.main) PrepareForCamera(camera);
                if (source == null || revision != patch.revision || !cameras.ContainsKey(camera)) return;
            }
            else if (!PrepareForCamera(camera)) return;
            var buffers = cameras[camera];
            Draw(camera, bounds, buffers);
        }

        void EnsureCameraBuffers(Camera camera)
        {
            if (!cameras.TryGetValue(camera, out var buffers))
            {
                buffers = new CameraBuffers();
                for (int i = 0; i < 3; i++)
                {
                    buffers.visible[i] = new ComputeBuffer(capacity, 24, ComputeBufferType.Append);
                    buffers.args[i] = new ComputeBuffer(1, 20, ComputeBufferType.IndirectArguments);
                    buffers.args[i].SetData(new uint[] { meshes[i].GetIndexCount(0), 0, 0, 0, 0 });
                    buffers.properties[i] = new MaterialPropertyBlock();
                    buffers.properties[i].SetBuffer("_GrassDataBuffer", buffers.visible[i]);
                }
                cameras.Add(camera, buffers);
            }
        }

        void Draw(Camera camera, Bounds bounds, CameraBuffers buffers)
        {
            for (int i = 0; i < 6; i++) planeVectors[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
            compute.SetVectorArray("_FrustumPlanes", planeVectors);
            compute.SetVector("_CameraPosition", camera.transform.position);
            compute.SetInt("_GrassCount", capacity);
            float range = Mathf.Min(patch.drawDistance, 90f);
            compute.SetFloat("_MaxDrawDistanceSq", range * range);
            compute.SetFloat("_MaxDrawDistance", range);
            compute.SetFloat("_LOD0DistanceSq", 16f * 16f);
            compute.SetFloat("_LOD1DistanceSq", 38f * 38f);
            compute.SetBool("_EnableDensityScaling", true);
            compute.SetFloat("_MinDensity", .22f);
            compute.SetFloat("_DensityFalloffStart", Mathf.Min(30f, patch.drawDistance * .5f));
            compute.SetFloat("_WidthCompensation", .25f);
            // Do not use stale depth from an unrelated camera for Hi-Z.
            compute.SetBool("_UseOcclusionCulling", false);
            compute.SetTexture(cull, "_HiZTexture", Texture2D.blackTexture);
            compute.SetBuffer(cull, "_SourceGrassBuffer", source);
            for (int i = 0; i < 3; i++)
            {
                buffers.visible[i].SetCounterValue(0);
                compute.SetBuffer(cull, "_CulledGrassBufferLOD" + i, buffers.visible[i]);
            }
            compute.Dispatch(cull, (capacity + 255) / 256, 1, 1);
            for (int i = 0; i < 3; i++)
            {
                ComputeBuffer.CopyCount(buffers.visible[i], buffers.args[i], 4);
                int grassLayer=LayerMask.NameToLayer("VoyageGrass");
                Graphics.DrawMeshInstancedIndirect(meshes[i], 0, material, bounds, buffers.args[i], 0, buffers.properties[i], ShadowCastingMode.Off, true, grassLayer<0 ? gameObject.layer : grassLayer, camera);
            }
        }

        public static Mesh BladeMesh(int segments)
        {
            int count = segments * 2 + 1;
            var vertices = new Vector3[count]; var uv = new Vector2[count]; var normals = new Vector3[count];
            var indices = new List<int>();
            for (int i = 0; i < segments; i++)
            {
                float t = i / (float)segments, width = .025f * (1f - t);
                vertices[i * 2] = new Vector3(-width, t, 0); vertices[i * 2 + 1] = new Vector3(width, t, 0);
                uv[i * 2] = new Vector2(0, t); uv[i * 2 + 1] = new Vector2(1, t);
                normals[i * 2] = normals[i * 2 + 1] = Vector3.forward;
                int b = i * 2;
                indices.AddRange(new[] { b, b + 2, b + 1 });
                if (i < segments - 1) indices.AddRange(new[] { b + 1, b + 2, b + 3 });
            }
            vertices[count - 1] = Vector3.up; uv[count - 1] = new Vector2(.5f, 1); normals[count - 1] = Vector3.forward;
            var mesh = new Mesh { name = "GrassFlow blade LOD", vertices = vertices, uv = uv, normals = normals, triangles = indices.ToArray(), hideFlags = HideFlags.HideAndDontSave };
            mesh.RecalculateBounds(); return mesh;
        }

        // All LODs keep the same three silhouette leaves. Extra leaves fill the near tuft.
        // Folded, curved mesh silhouettes need no alpha texture or per-blade GameObjects.
        public static Mesh TuftMesh(int lod)
        {
            int leaves = lod == 0 ? 9 : lod == 1 ? 5 : 3;
            int segments = lod == 0 ? 3 : 2;
            var vertices = new List<Vector3>(); var uv = new List<Vector2>();
            var colors = new List<Color>(); var indices = new List<int>();
            for (int leaf = 0; leaf < leaves; leaf++)
            {
                float angle = leaf * 2.39996323f;
                Vector3 outward = new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle));
                Vector3 right = new Vector3(outward.z, 0, -outward.x);
                float height = .95f + .40f * Mathf.Repeat(leaf * .618f + .7f, 1);
                float halfWidth = .14f + .05f * Mathf.Repeat(leaf * .37f, 1);
                int start = vertices.Count;
                for (int level = 0; level <= segments; level++)
                {
                    float t = level / (float)segments;
                    Vector3 spine = outward * (.30f + .36f*t*t) + Vector3.up * (t*height);
                    float width = halfWidth * (1-t) * (.45f+1.8f*t);
                    for (int side = 0; side < 3; side++)
                    {
                        vertices.Add(spine + right * ((side-1)*width) + outward * (side == 1 ? -.08f*Mathf.Sin(t*Mathf.PI) : 0));
                        uv.Add(new Vector2(side*.5f,t));
                        colors.Add(new Color(side == 0 ? 0 : 1, Mathf.Repeat(leaf*.31f,1),0,1));
                    }
                    if (level == segments) continue;
                    int b = start+level*3;
                    for (int side = 0; side < 2; side++)
                    {
                        indices.AddRange(new[] { b+side,b+side+3,b+side+1 });
                        if (level < segments-1) indices.AddRange(new[] { b+side+1,b+side+3,b+side+4 });
                    }
                }
            }
            var mesh = new Mesh { name = "GrassFlow stylized tuft LOD " + lod, hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices); mesh.SetUVs(0,uv); mesh.SetColors(colors); mesh.SetTriangles(indices,0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }

        // Individually rooted narrow ribbons spread across a square metre, not a radial rosette.
        public static Mesh MeadowMesh(int lod)
        {
            int leaves = lod == 0 ? 44 : lod == 1 ? 15 : 5;
            int segments = lod == 0 ? 3 : lod == 1 ? 2 : 1;
            var vertices = new List<Vector3>(); var uv = new List<Vector2>();
            var roots = new List<Vector2>(); var colors = new List<Color>(); var indices = new List<int>();
            for (int leaf = 0; leaf < leaves; leaf++)
            {
                // Stable sequence shared by LODs; retained leaves do not jump on transition.
                float a = leaf * 2.39996323f;
                float radius = .92f * Mathf.Sqrt(Mathf.Repeat(leaf * .754877f + .17f, 1));
                Vector3 root = new Vector3(Mathf.Cos(a),0,Mathf.Sin(a)) * radius;
                float turn = leaf * 1.713f;
                Vector3 right = new Vector3(Mathf.Cos(turn),0,Mathf.Sin(turn));
                Vector3 lean = new Vector3(-right.z,0,right.x) * (.10f+.16f*Mathf.Repeat(leaf*.31f,1));
                float lengthVariation = Mathf.Repeat(leaf*.618f+.2f,1);
                float height = .48f + .78f * lengthVariation * lengthVariation;
                float width = (.032f+.015f*Mathf.Repeat(leaf*.37f,1)) * (lod == 2 ? 2.8f : lod == 1 ? 1.7f : 1);
                int start = vertices.Count;
                for (int level = 0; level <= segments; level++)
                {
                    float t = level/(float)segments;
                    Vector3 spine = root + Vector3.up * (t*height) + lean*t*t;
                    int sides = level == segments ? 1 : 2;
                    for (int side = 0; side < sides; side++)
                    {
                        vertices.Add(spine + right * (sides == 1 ? 0 : (side*2-1)*width*(1-t)));
                        uv.Add(new Vector2(sides == 1 ? .5f : side,t)); roots.Add(new Vector2(root.x,root.z));
                        colors.Add(new Color(.7f,Mathf.Repeat(leaf*.31f,1),0,1));
                    }
                    if (level == segments) continue;
                    int b = start+level*2;
                    indices.AddRange(new[] { b,b+2,b+1 });
                    if (level < segments-1) indices.AddRange(new[] { b+1,b+2,b+3 });
                }
            }
            var mesh = new Mesh { name = "GrassFlow fine golden meadow LOD " + lod, hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices); mesh.SetUVs(0,uv); mesh.SetUVs(1,roots); mesh.SetColors(colors); mesh.SetTriangles(indices,0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }

        void Release()
        {
            source?.Release(); source = null;
            foreach (var value in cameras.Values) value.Release(); cameras.Clear();
            foreach (var mesh in meshes) Delete(mesh);
            Delete(material); Delete(compute); material = null; compute = null; revision = -1;
        }
        static void Delete(Object value) { if (value == null) return; if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
    }
}
