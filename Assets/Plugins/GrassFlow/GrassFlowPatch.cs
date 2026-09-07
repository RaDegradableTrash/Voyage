using UnityEngine;

namespace GrassFlow
{
    [CreateAssetMenu(menuName = "GrassFlow/Painted patch")]
    public sealed class GrassFlowPatch : ScriptableObject
    {
        public enum GrassStyle
        {
            [InspectorName("金色草甸 / Golden Meadow")] GoldenMeadow = 0,
            [InspectorName("芦荟 / Aloe")] Aloe = 1
        }
        [Tooltip("GoldenMeadow: 细密金色草甸。Aloe: 保留的宽叶版本，称为“芦荟”。")]
        public GrassStyle style = GrassStyle.GoldenMeadow;
        public Bounds bounds;
        public Texture2D surface;
        public Texture2D density;
        [Range(16, 256)] public int bladesPerRow = 256;
        [Range(.1f, 3f)] public float minHeight = .65f;
        [Range(.1f, 3f)] public float maxHeight = 1.25f;
        [Range(20f, 200f)] public float drawDistance = 120f;
        [Header("Aloe palette (芦荟)")]
        public Color rootColor = new Color(.19f, .25f, .10f);
        public Color leafColor = new Color(.46f, .57f, .22f);
        public Color tipColor = new Color(.83f, .78f, .40f);
        [Range(0f, .6f)] public float windStrength = .30f;
        [Header("Golden meadow palette")]
        public Color meadowRoot = new Color(.43f, .39f, .29f);
        public Color meadowLeaf = new Color(.48f, .43f, .32f);
        public Color meadowTip = new Color(.62f, .57f, .44f);
        [System.NonSerialized] public int revision;
        public int Capacity => Mathf.Clamp(bladesPerRow, 16, 256) * Mathf.Clamp(bladesPerRow, 16, 256);
        void OnEnable()
        {
            // Unity can preserve the old field defaults across a live script reload.
            // Upgrade only the exact old preset, retaining custom palettes and all paint.
            if (meadowLeaf == new Color(.91f,.72f,.32f) && meadowTip == new Color(1f,.91f,.62f))
            {
                meadowRoot = new Color(.43f,.39f,.29f);
                meadowLeaf = new Color(.48f,.43f,.32f);
                meadowTip = new Color(.62f,.57f,.44f);
                Changed();
            }
        }
        public void Changed() { revision++; }
        void OnValidate() => Changed();

        // World-space sampling makes strokes seamless across neighbouring tiles.
        public bool Paint(Vector3 center, float radius, float opacity, float target, float hardness)
        {
            if (density == null || surface == null || radius <= 0f) return false;
            int w = density.width, h = density.height;
            Vector3 min = bounds.min;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((center.x - radius - min.x) / bounds.size.x * w), 0, w - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((center.x + radius - min.x) / bounds.size.x * w), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((center.z - radius - min.z) / bounds.size.z * h), 0, h - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt((center.z + radius - min.z) / bounds.size.z * h), 0, h - 1);
            bool changed = false;
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float u = (x + .5f) / w, v = (y + .5f) / h;
                float distance = Vector2.Distance(new Vector2(min.x + u * bounds.size.x, min.z + v * bounds.size.z), new Vector2(center.x, center.z)) / radius;
                if (distance > 1f || surface.GetPixelBilinear(u, v).g < .99f) continue;
                float weight = 1f - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(Mathf.Min(hardness, .999f), 1f, distance));
                float old = density.GetPixel(x, y).r;
                float value = Mathf.Lerp(old, Mathf.Clamp01(target), Mathf.Clamp01(opacity * weight));
                if (Mathf.Abs(old - value) < 1f / 510f) continue;
                density.SetPixel(x, y, new Color(value, value, value, 1));
                changed = true;
            }
            if (changed) { density.Apply(false); Changed(); }
            return changed;
        }
    }
}
