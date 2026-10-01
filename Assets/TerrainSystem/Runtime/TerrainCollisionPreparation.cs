using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voyage.TerrainSystem
{
    // Runs before prefab instantiation, never while a tile is supporting a car.
    public static class TerrainCollisionPreparation
    {
        public const float MaximumEdgeLength = 450f;
        static readonly Dictionary<EntityId, Task> cooks = new Dictionary<EntityId, Task>();
        static readonly List<Mesh> ownedMeshes = new List<Mesh>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RegisterCleanup()
        {
            Application.quitting -= Reset;
            Application.quitting += Reset;
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Reset;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Reset;
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
        }

#if UNITY_EDITOR
        static void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) Reset();
        }
#endif

        public sealed class MeshData
        {
            public Vector3[] vertices;
            public int[] triangles;
            public bool changed;
        }

        struct Triangle
        {
            public int a, b, c;
            public Triangle(int a, int b, int c) { this.a = a; this.b = b; this.c = c; }
        }

        // Pure managed work: midpoint subdivision retains the original plane,
        // winding and outer edges. Visual LOD meshes are never modified.
        public static MeshData Subdivide(Vector3[] vertices, int[] triangles, float maxEdge)
        {
            if (!(maxEdge > 0) || float.IsInfinity(maxEdge)) throw new ArgumentOutOfRangeException(nameof(maxEdge));
            var points = new List<Vector3>(vertices);
            var indices = new List<int>(triangles.Length);
            var midpoints = new Dictionary<long, int>();
            var pending = new Stack<Triangle>();
            float limitSq = maxEdge * maxEdge;
            int Midpoint(int a, int b)
            {
                long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                if (midpoints.TryGetValue(key, out int index)) return index;
                index = points.Count;
                points.Add((points[a] + points[b]) * .5f);
                midpoints.Add(key, index);
                return index;
            }
            for (int i = 0; i < triangles.Length; i += 3)
            {
                pending.Push(new Triangle(triangles[i], triangles[i + 1], triangles[i + 2]));
                while (pending.Count > 0)
                {
                    Triangle t = pending.Pop();
                    float ab = (points[t.a] - points[t.b]).sqrMagnitude;
                    float bc = (points[t.b] - points[t.c]).sqrMagnitude;
                    float ca = (points[t.c] - points[t.a]).sqrMagnitude;
                    if (Math.Max(ab, Math.Max(bc, ca)) <= limitSq)
                    {
                        indices.Add(t.a); indices.Add(t.b); indices.Add(t.c);
                        continue;
                    }
                    if (bc > ab && bc >= ca) t = new Triangle(t.b, t.c, t.a);
                    else if (ca > ab && ca > bc) t = new Triangle(t.c, t.a, t.b);
                    int mid = Midpoint(t.a, t.b);
                    pending.Push(new Triangle(t.a, mid, t.c));
                    pending.Push(new Triangle(mid, t.b, t.c));
                }
            }
            bool changed = points.Count != vertices.Length;
            return new MeshData { vertices = changed ? points.ToArray() : vertices,
                triangles = changed ? indices.ToArray() : triangles, changed = changed };
        }

        public static IEnumerator Prepare(GameObject prefab)
        {
            foreach (MeshCollider collider in prefab.GetComponentsInChildren<MeshCollider>(true))
            {
                Mesh mesh = collider.sharedMesh;
                if (mesh == null) continue;
                Vector3 scale = collider.transform.lossyScale;
                float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                float maxEdge = MaximumEdgeLength / Mathf.Max(.0001f, maxScale);
                if (mesh.bounds.size.sqrMagnitude > maxEdge * maxEdge)
                {
                    Vector3[] vertices = mesh.vertices;
                    int[] triangles = mesh.triangles;
                    Task<MeshData> subdivide = Task.Run(() => Subdivide(vertices, triangles, maxEdge));
                    while (!subdivide.IsCompleted) yield return null;
                    MeshData data = subdivide.GetAwaiter().GetResult();
                    if (data.changed)
                    {
                        mesh = new Mesh { name = mesh.name + "_Collision", indexFormat = IndexFormat.UInt32 };
                        mesh.vertices = data.vertices;
                        mesh.triangles = data.triangles;
                        mesh.RecalculateBounds();
                        ownedMeshes.Add(mesh);
                    }
                }
                EntityId meshId = mesh.GetEntityId();
                if (!cooks.TryGetValue(meshId, out Task cook))
                {
                    cook = Task.Run(() => Physics.BakeMesh(meshId, false, TerrainTileRuntime.CollisionCookingOptions));
                    cooks.Add(meshId, cook);
                }
                while (!cook.IsCompleted) yield return null;
                cook.GetAwaiter().GetResult();
                // Detach before changing cooking flags; assigning the prepared
                // mesh last lets PhysX reuse the exact worker-cooked data.
                collider.enabled = false;
                if (collider.sharedMesh != mesh || collider.convex ||
                    collider.cookingOptions != TerrainTileRuntime.CollisionCookingOptions)
                {
                    collider.sharedMesh = null;
                    if (collider.convex) collider.convex = false;
                    collider.cookingOptions = TerrainTileRuntime.CollisionCookingOptions;
                    collider.sharedMesh = mesh;
                }
                if (collider.isTrigger) collider.isTrigger = false;
            }
        }

        public static void Reset()
        {
            // A stopped coroutine may leave a bake running. Join before bundle
            // unload destroys its meshes (also covers disabled domain reload).
            foreach (Task cook in cooks.Values)
            {
                try { cook.GetAwaiter().GetResult(); }
                catch (Exception error) { Debug.LogException(error); }
            }
            cooks.Clear();
            foreach (Mesh mesh in ownedMeshes)
                if (mesh != null) UnityEngine.Object.Destroy(mesh);
            ownedMeshes.Clear();
        }
    }
}
