using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voyage.TerrainSystem
{
    /// <summary>Small world-space debug grid; no per-frame mesh rebuilding or terrain scans.</summary>
    public sealed class ChunkBorderDisplay : MonoBehaviour
    {
        public bool Visible { get; private set; }
        TerrainChunkSettings settings;
        Mesh mesh;
        Material material;
        GameObject display;
        MeshRenderer rendererComponent;
        readonly List<Vector3> vertices = new List<Vector3>(4096);
        readonly List<Color> colors = new List<Color>(4096);
        readonly List<int> indices = new List<int>(4096);
        Vector2Int lastCell;
        int lastHeight;
        bool built;
        string caption;

        public bool SetVisible(bool value)
        {
            if (value && mesh == null)
            {
                var index = Resources.Load<TerrainTileIndex>("TerrainSystem/TerrainTileIndex");
                Shader shader = Resources.Load<Shader>("ChunkBorder");
                if (index == null || index.settings == null || shader == null) return false;
                settings = index.settings;
                mesh = new Mesh { name = "Chunk boundary grid" };
                mesh.MarkDynamic();
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                display = new GameObject("VOYAGE // CHUNK BORDERS");
                display.AddComponent<MeshFilter>().sharedMesh = mesh;
                rendererComponent = display.AddComponent<MeshRenderer>();
                rendererComponent.sharedMaterial = material;
                rendererComponent.shadowCastingMode = ShadowCastingMode.Off;
                rendererComponent.receiveShadows = false;
                rendererComponent.lightProbeUsage = LightProbeUsage.Off;
                rendererComponent.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            Visible = value;
            enabled = value;
            if (rendererComponent != null) rendererComponent.enabled = value;
            if (value) { built = false; LateUpdate(); }
            return true;
        }

        void LateUpdate()
        {
            if (!Visible || settings == null) return;
            Transform target = DrivingCore.Instance != null ? DrivingCore.Instance.ControlledTarget : null;
            if (target == null) return;
            Vector3 position = target.position;
            Vector2Int cell = settings.WorldToTile(position);
            bool xz = settings.horizontalAxes == TerrainHorizontalAxes.XZ;
            int height = Mathf.FloorToInt((xz ? position.y : position.z) / 16f);
            if (built && cell == lastCell && height == lastHeight) return;
            lastCell = cell; lastHeight = height; built = true;
            vertices.Clear(); colors.Clear(); indices.Clear();
            float size = Mathf.Max(1, settings.tileSize);
            float originU = settings.worldOrigin.x;
            float originV = xz ? settings.worldOrigin.z : settings.worldOrigin.y;
            float bottom = (height - 4) * 16f, top = (height + 8) * 16f;
            // Shared boundaries overlap: draw the current cell last so its
            // yellow outline is not tinted blue by a neighbouring cell.
            for (int priority = 0; priority < 2; priority++)
            for (int z = cell.y - 1; z <= cell.y + 1; z++)
            for (int x = cell.x - 1; x <= cell.x + 1; x++)
            {
                bool current = x == cell.x && z == cell.y;
                if (current != (priority == 1)) continue;
                Color edge = current ? new Color(1, .9f, .1f, .95f) : new Color(.15f, .65f, 1, .55f);
                Color grid = current ? new Color(1, .35f, .1f, .38f) : new Color(.15f, .65f, 1, .18f);
                float u = originU + x * size, v = originV + z * size;
                for (int side = 0; side < 4; side++)
                {
                    float au = side == 1 || side == 2 ? u + size : u;
                    float av = side >= 2 ? v + size : v;
                    float bu = side == 0 || side == 1 ? u + size : u;
                    float bv = side == 1 || side == 2 ? v + size : v;
                    Line(Point(au, av, bottom, xz), Point(au, av, top, xz), edge);
                    for (float h = bottom; h <= top; h += 16f)
                        Line(Point(au, av, h, xz), Point(bu, bv, h, xz), h == bottom || h == top ? edge : grid);
                }
            }
            mesh.Clear(); mesh.SetVertices(vertices); mesh.SetColors(colors);
            mesh.SetIndices(indices, MeshTopology.Lines, 0); mesh.RecalculateBounds();
            caption = $"CHUNK {cell.x}, {cell.y}  |  {size:0.##} m  |  /chunk border";
        }

        static Vector3 Point(float u, float v, float h, bool xz) => xz ? new Vector3(u,h,v) : new Vector3(u,v,h);
        void Line(Vector3 a, Vector3 b, Color color)
        {
            indices.Add(vertices.Count); vertices.Add(a); colors.Add(color);
            indices.Add(vertices.Count); vertices.Add(b); colors.Add(color);
        }
        void OnGUI()
        {
            if (Visible && caption != null) GUI.Box(new Rect(Screen.width - 360, 20, 340, 26), caption);
        }
        void OnDisable() { Visible = false; if (rendererComponent != null) rendererComponent.enabled = false; }
        void OnDestroy()
        {
            if (display != null) Destroy(display);
            if (mesh != null) Destroy(mesh);
            if (material != null) Destroy(material);
        }
    }
}
