using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Voyage.TerrainSystem;

namespace GrassFlow.Editor
{
    public sealed class GrassFlowPainterWindow : EditorWindow
    {
        const string Folder = "Assets/GrassFlowPaint/Resources/GrassFlow/Tiles";
        readonly List<GameObject> previews = new List<GameObject>();
        readonly List<GrassFlowPatch> patches = new List<GrassFlowPatch>();
        float radius = 12f, opacity = .7f, targetDensity = .6f, hardness = .5f;
        bool painting;
        Vector3 lastPoint;
        bool dragging;
        readonly HashSet<GrassFlowPatch> touched = new HashSet<GrassFlowPatch>();
        int undoGroup;

        [MenuItem("Tools/Voyage/Grass Painter")]
        public static void Open() => GetWindow<GrassFlowPainterWindow>("Grass Painter");
        void OnEnable() { SceneView.duringSceneGui += SceneGUI; Undo.undoRedoPerformed += Refresh; EditorApplication.playModeStateChanged += PlayModeChanged; }
        void OnDisable() { SceneView.duringSceneGui -= SceneGUI; Undo.undoRedoPerformed -= Refresh; EditorApplication.playModeStateChanged -= PlayModeChanged; Save(); ClearPreview(); }
        void PlayModeChanged(PlayModeStateChange state) { if (state == PlayModeStateChange.ExitingEditMode) { Save(); ClearPreview(); } }

        void OnGUI()
        {
            EditorGUILayout.LabelField("GrassFlow · Mesh grass painter", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Edit Mode: load the 3×3 tiles around the Scene view pivot, then paint. Left drag adds grass; Shift erases. Ctrl+Z undoes. Unpainted tiles generate meadow in game; saved strokes take precedence.", MessageType.Info);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Load / create patches near Scene view")) LoadArea(SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : new Vector3(-24, 0, -24));
                if (GUILayout.Button("Load vehicle spawn area")) LoadArea(new Vector3(-24, 0, -24));
                painting = GUILayout.Toggle(painting, "Enable grass brush", "Button");
                radius = EditorGUILayout.Slider("Radius (m)", radius, 1, 64);
                opacity = EditorGUILayout.Slider("Strength", opacity, .01f, 1);
                hardness = EditorGUILayout.Slider("Hardness", hardness, 0, 1);
                targetDensity = EditorGUILayout.Slider("Target density", targetDensity, 0, 1);
                EditorGUILayout.LabelField($"Loaded patches: {patches.Count}");
                if (GUILayout.Button("Save painted patches")) Save();
                if (GUILayout.Button("Close preview")) { Save(); ClearPreview(); }
            }
        }

        void Refresh() { foreach (var patch in patches) if (patch != null) patch.Changed(); SceneView.RepaintAll(); }
        void Save() { AssetDatabase.SaveAssets(); }
        void ClearPreview()
        {
            foreach (var go in previews)
            {
                if (go == null) continue;
                var filter = go.GetComponent<MeshFilter>();
                if (filter != null) DestroyImmediate(filter.sharedMesh);
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null) DestroyImmediate(renderer.sharedMaterial);
                DestroyImmediate(go);
            }
            previews.Clear(); patches.Clear();
        }

        public void LoadArea(Vector3 position)
        {
            Save(); ClearPreview();
            var index = Resources.Load<TerrainTileIndex>("TerrainSystem/TerrainTileIndex");
            if (index == null) throw new InvalidOperationException("Terrain tile index is missing.");
            Vector2Int center = index.settings.WorldToTile(position);
            Directory.CreateDirectory(Folder); AssetDatabase.Refresh();
            for (int z = center.y - 1; z <= center.y + 1; z++)
            for (int x = center.x - 1; x <= center.x + 1; x++)
            {
                if (!index.TryGet(new Vector2Int(x, z), out var record)) continue;
                Mesh mesh = ReadWorldMesh(record);
                if (mesh == null) continue;
                var patch = LoadOrBake(record, index.settings, mesh);
                patches.Add(patch);
                var go = new GameObject($"Grass brush preview {x}, {z}") { hideFlags = HideFlags.HideAndDontSave };
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var material = new Material(Shader.Find("Voyage/Terrain/Stylized")) { hideFlags = HideFlags.HideAndDontSave };
                material.SetColor("_BaseColor", new Color(.64f, .42f, .14f));
                material.SetColor("_ShadowColor", new Color(.48f, .33f, .12f));
                material.SetColor("_RidgeColor", new Color(.78f, .56f, .22f));
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                go.AddComponent<GrassFlowRenderer>().patch = patch;
                previews.Add(go);
            }
            AssetDatabase.SaveAssets(); SceneView.RepaintAll(); Repaint();
        }

        public static Mesh ReadWorldMesh(TerrainTileRecord record)
        {
            var prefab = Resources.Load<GameObject>(record.resourcePath);
            if (prefab == null) return null;
            var runtime = prefab.GetComponent<TerrainTileRuntime>();
            var roots = new SerializedObject(runtime).FindProperty("lodRoots");
            var lod = roots.GetArrayElementAtIndex(0).objectReferenceValue as GameObject;
            if (lod == null) return null;
            var vertices = new List<Vector3>(); var indices = new List<int>();
            foreach (var filter in lod.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || filter.name.IndexOf("skirt", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                int offset = vertices.Count;
                Matrix4x4 matrix = Matrix4x4.Translate(record.bounds.center) * prefab.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                foreach (var vertex in filter.sharedMesh.vertices) vertices.Add(matrix.MultiplyPoint3x4(vertex));
                foreach (int index in filter.sharedMesh.triangles) indices.Add(index + offset);
            }
            var mesh = new Mesh { name = "GrassFlow mesh surface", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices); mesh.SetTriangles(indices, 0); mesh.RecalculateBounds(); mesh.RecalculateNormals(); return mesh;
        }

        public static GrassFlowPatch LoadOrBake(TerrainTileRecord record, TerrainChunkSettings settings, Mesh mesh)
        {
            string path = $"{Folder}/Grass_{record.coordinate.x}_{record.coordinate.y}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<GrassFlowPatch>(path);
            if (existing != null) return existing; // Never overwrite authored strokes on reopening.
            var patch = CreatePatch(mesh, settings.GetTileBounds(record.coordinate), .78f);
            patch.name = $"Grass_{record.coordinate.x}_{record.coordinate.y}";
            AssetDatabase.CreateAsset(patch, path);
            AssetDatabase.AddObjectToAsset(patch.surface, patch);
            AssetDatabase.AddObjectToAsset(patch.density, patch);
            EditorUtility.SetDirty(patch); return patch;
        }

        public static GrassFlowPatch CreatePatch(Mesh worldMesh, Bounds tileBounds, float fill)
        {
            const int resolution = 256;
            var patch = CreateInstance<GrassFlowPatch>();
            Vector3 min = tileBounds.min, size = tileBounds.size;
            min.y = worldMesh.bounds.min.y - .01f; size.y = Mathf.Max(.1f, worldMesh.bounds.size.y + .02f);
            patch.bounds = new Bounds(min + size * .5f, size);
            var surface = new Color[resolution * resolution];
            var vertices = worldMesh.vertices; var triangles = worldMesh.triangles;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                if (Mathf.Abs(normal.y) < .57f) continue;
                int x0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - min.x) / size.x * resolution), 0, resolution - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - min.x) / size.x * resolution), 0, resolution - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - min.z) / size.z * resolution), 0, resolution - 1);
                int z1 = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.z, Mathf.Max(b.z, c.z)) - min.z) / size.z * resolution), 0, resolution - 1);
                float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(denominator) < .000001f) continue;
                for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
                {
                    float px = min.x + (x + .5f) / resolution * size.x, pz = min.z + (z + .5f) / resolution * size.z;
                    float u = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / denominator;
                    float v = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / denominator;
                    if (u < -.00001f || v < -.00001f || u + v > 1.00001f) continue;
                    float height = (u * a.y + v * b.y + (1 - u - v) * c.y - min.y) / size.y;
                    int index = z * resolution + x;
                    if (surface[index].g == 0 || height > surface[index].r) surface[index] = new Color(height, 1, 0, 1);
                }
            }
            patch.surface = new Texture2D(resolution, resolution, TextureFormat.RGFloat, false, true) { name = "Mesh height and coverage", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            patch.surface.SetPixels(surface); patch.surface.Apply(false);
            var colors = new Color[surface.Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = Color.white * (surface[i].g > .5f ? fill : 0f);
            patch.density = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false, true) { name = "Painted density", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            patch.density.SetPixels(colors); patch.density.Apply(false); return patch;
        }

        void SceneGUI(SceneView view)
        {
            if (!painting || Application.isPlaying || patches.Count == 0) return;
            Event e = Event.current;
            if (e.alt || e.button == 1 || e.button == 2) return;
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            bool hitFound = false; RaycastHit hit = default; float nearest = float.MaxValue;
            foreach (var preview in previews)
                if (preview != null && preview.GetComponent<Collider>().Raycast(ray, out var candidate, 100000) && candidate.distance < nearest)
                { hit = candidate; nearest = candidate.distance; hitFound = true; }
            if (hitFound)
            {
                Handles.color = e.shift ? Color.red : Color.green;
                Handles.DrawWireDisc(hit.point, hit.normal, radius);
                if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0)
                {
                    if (!dragging)
                    {
                        Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Paint grass");
                        touched.Clear(); lastPoint = hit.point; dragging = true;
                    }
                    int steps = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(lastPoint, hit.point) / Mathf.Max(.5f, radius * .2f)), 1, 128);
                    foreach (var patch in patches)
                    {
                        if (!touched.Contains(patch)) { Undo.RegisterCompleteObjectUndo(patch.density, "Paint grass"); touched.Add(patch); }
                        for (int step = 1; step <= steps; step++)
                            if (patch.Paint(Vector3.Lerp(lastPoint, hit.point, step / (float)steps), radius, opacity, e.shift ? 0 : targetDensity, hardness)) EditorUtility.SetDirty(patch.density);
                    }
                    lastPoint = hit.point; e.Use(); view.Repaint();
                }
            }
            if (dragging && e.type == EventType.MouseUp)
            {
                dragging = false; Undo.CollapseUndoOperations(undoGroup); Save(); e.Use();
            }
            if (e.type == EventType.MouseMove) view.Repaint();
        }

        // Batch-mode setup, without modifying the currently open scene or prefabs.
        public static void BakeSpawnArea()
        {
            var window = CreateInstance<GrassFlowPainterWindow>();
            try { window.LoadArea(new Vector3(-24, 0, -24)); }
            finally { window.ClearPreview(); DestroyImmediate(window); }
        }
    }
}
