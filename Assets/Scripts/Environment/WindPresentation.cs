using UnityEngine;
using UnityEngine.Rendering;

namespace Voyage.Wind
{
    /// <summary>One world-space slope field shared by all grass LODs and pooled wind ribbons.</summary>
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(WindSystem))]
    public sealed class WindPresentation : MonoBehaviour
    {
        const int Capacity = 8, Points = 28;
        public const float FieldSize = 512f;
        public RenderTexture SlopeField { get; private set; }
        public int ActiveLineCount { get; private set; }
        readonly Ribbon[] ribbons = new Ribbon[Capacity];
        Material fieldMaterial, lineMaterial;
        WindSystem wind;
        Vector2 fieldOffset;
        float nextLine;
        sealed class Ribbon
        {
            public LineRenderer renderer;
            public readonly Vector3[] points = new Vector3[Points];
            public Vector3 origin, direction, side;
            public float age, lifetime, travel;
            public bool active;
        }

        void OnEnable()
        {
            wind = GetComponent<WindSystem>();
            // Resources references retain these shaders in standalone builds.
            var slopeShader = Resources.Load<Shader>("WindSlope");
            var lineShader = Resources.Load<Shader>("WindLine");
            if (slopeShader == null || lineShader == null)
            {
                Debug.LogError("Wind presentation shaders are missing.", this);
                enabled = false;
                return;
            }
            fieldMaterial = new Material(slopeShader) { hideFlags = HideFlags.HideAndDontSave };
            lineMaterial = new Material(lineShader) { hideFlags = HideFlags.HideAndDontSave };
            SlopeField = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            { name = "Wind slope (world space)", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            SlopeField.Create();
            UpdateField(0);
            nextLine = Time.time + 1f;
        }

        public void UpdateField(float deltaTime)
        {
            if (fieldMaterial == null) return;
            Vector4 state = wind.GrassWindVector;
            if (!wind.enabled) state.z = state.w = 0;
            fieldOffset += new Vector2(state.x,state.y) * (deltaTime * state.z * 5f / FieldSize);
            fieldMaterial.SetVector("_DirectionForce", new Vector4(state.x, state.y, state.w, 0));
            fieldMaterial.SetVector("_Phase", new Vector4(fieldOffset.x, fieldOffset.y, 0, 0));
            Graphics.Blit(null, SlopeField, fieldMaterial);
            Shader.SetGlobalTexture("_VoyageWindField", SlopeField);
            Shader.SetGlobalVector("_VoyageWindFieldWorld", new Vector4(FieldSize*.5f, FieldSize*.5f, FieldSize, 256));
        }

        public bool SpawnNearPlayer(bool forced)
        {
            var player = DrivingCore.Instance != null ? DrivingCore.Instance.ControlledTarget : null;
            Camera camera = Camera.main;
            if (player == null || camera == null || lineMaterial == null) return false;
            Vector3 forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .01f) forward = player.transform.forward;
            // A forced ribbon crosses the visible foreground, even in calm wind.
            Vector3 center = forced
                ? camera.transform.position + forward * 12f
                : player.transform.position + forward * Random.Range(10f, 35f) + camera.transform.right * Random.Range(-14f, 14f);
            center.y = player.transform.position.y + 2.5f;
            if (Physics.Raycast(center + Vector3.up * 100f, Vector3.down, out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore))
                center.y = Mathf.Max(center.y, hit.point.y + 2.5f);
            return Spawn(center);
        }

        public bool Spawn(Vector3 center)
        {
            if (lineMaterial == null) return false;
            int slot = -1;
            for (int i = 0; i < Capacity; i++) if (ribbons[i] == null || !ribbons[i].active) { slot = i; break; }
            // Bounded pool: repeated debug commands replace the oldest ribbon.
            if (slot < 0)
            {
                slot = 0;
                for (int i = 1; i < Capacity; i++) if (ribbons[i].age > ribbons[slot].age) slot = i;
            }
            Ribbon ribbon = ribbons[slot];
            if (ribbon == null)
            {
                var go = new GameObject("Wind ribbon");
                go.transform.SetParent(transform, false);
                var line = go.AddComponent<LineRenderer>();
                line.sharedMaterial = lineMaterial;
                line.useWorldSpace = true;
                line.positionCount = Points;
                line.widthCurve = new AnimationCurve(new Keyframe(0, 0), new Keyframe(.18f, 1), new Keyframe(.75f, .8f), new Keyframe(1, 0));
                line.widthMultiplier = .12f;
                line.numCornerVertices = 2;
                line.shadowCastingMode = ShadowCastingMode.Off;
                line.receiveShadows = false;
                ribbon = ribbons[slot] = new Ribbon { renderer = line };
            }
            Vector2 dir = wind.Direction;
            ribbon.direction = new Vector3(dir.x, 0, dir.y);
            ribbon.side = new Vector3(-dir.y, 0, dir.x);
            ribbon.origin = center - ribbon.direction * 6f;
            ribbon.age = ribbon.travel = 0;
            ribbon.lifetime = 5f;
            ribbon.active = true;
            ribbon.renderer.enabled = true;
            Draw(ribbon, 0);
            return true;
        }

        void LateUpdate()
        {
            UpdateField(Time.deltaTime);
            if (wind.enabled && wind.force > .01f && wind.speed > .01f && Time.time >= nextLine)
            {
                SpawnNearPlayer(false);
                nextLine = Time.time + Random.Range(2f, 5f);
            }
            ActiveLineCount = 0;
            foreach (Ribbon ribbon in ribbons)
            {
                if (ribbon == null || !ribbon.active) continue;
                ribbon.age += Time.deltaTime;
                if (ribbon.age >= ribbon.lifetime) { ribbon.active = false; ribbon.renderer.enabled = false; continue; }
                ribbon.travel += Time.deltaTime * Mathf.Max(2f, wind.GrassWindVector.z * 5f);
                Draw(ribbon, ribbon.age);
                ActiveLineCount++;
            }
        }

        void Draw(Ribbon ribbon, float age)
        {
            float opacity = Mathf.SmoothStep(0, 1, age / .45f) * Mathf.SmoothStep(0, 1, (ribbon.lifetime-age)/1f);
            Color color = new Color(.85f, .88f, .82f, opacity * .65f);
            ribbon.renderer.startColor = ribbon.renderer.endColor = color;
            for (int j = 0; j < Points; j++)
            {
                float t = j / (float)(Points - 1);
                ribbon.points[j] = ribbon.origin + ribbon.direction * (ribbon.travel + t*9f)
                    + ribbon.side * Mathf.Sin(t*5f-age*1.4f)*.8f
                    + Vector3.up * Mathf.Sin(t*4f+age)*.45f;
            }
            ribbon.renderer.SetPositions(ribbon.points);
        }

        void OnDisable()
        {
            Shader.SetGlobalTexture("_VoyageWindField", Texture2D.blackTexture);
            Shader.SetGlobalVector("_VoyageWindFieldWorld", Vector4.zero);
            if (SlopeField != null) { SlopeField.Release(); Release(SlopeField); SlopeField = null; }
            if (fieldMaterial != null) Release(fieldMaterial);
            if (lineMaterial != null) Release(lineMaterial);
            for (int i = 0; i < Capacity; i++)
            {
                if (ribbons[i] != null && ribbons[i].renderer != null) Release(ribbons[i].renderer.gameObject);
                ribbons[i] = null;
            }
            ActiveLineCount = 0;
        }

        static void Release(Object value)
        {
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
    }
}
