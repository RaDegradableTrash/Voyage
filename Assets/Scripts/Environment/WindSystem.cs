using UnityEngine;

namespace Voyage.Wind
{
    /// <summary>
    /// Shared atmospheric wind state. Renderers consume the published shader
    /// values so grass, clouds and future wind-reactive props stay in sync.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WindSystem : MonoBehaviour
    {
        public static WindSystem Instance { get; private set; }

        [Header("Base wind")]
        public Vector2 direction = new Vector2(.86f, .28f);
        [Min(0f)] public float speed = 1f;
        [Range(0f, 2f)] public float force = .42f;
        [Range(0f, 1f)] public float gustStrength = .28f;
        [Min(0f)] public float gustPeriod = 7f;
        [Header("Height response")]
        [Range(0f, 1f)] public float heightShear = .35f;
        [Min(1f)] public float heightReference = 500f;

        static readonly int GrassWindId = Shader.PropertyToID("_VoyageGrassWind");
        static readonly int WindDirectionId = Shader.PropertyToID("_VoyageWindDirection");
        static readonly int WindParametersId = Shader.PropertyToID("_VoyageWindParameters");
        static readonly int WindHeightId = Shader.PropertyToID("_VoyageWindHeightReference");

        public Vector2 Direction => direction.sqrMagnitude > .0001f ? direction.normalized : Vector2.right;
        public Vector4 GrassWindVector
        {
            get
            {
                Vector2 dir = Direction;
                float gust = EvaluateGust(Time.time);
                return new Vector4(dir.x, dir.y, Mathf.Max(0f, speed) * (1f + gust * gustStrength),
                    Mathf.Clamp01(force * (1f + gust * gustStrength)));
            }
        }

        void OnEnable()
        {
            if (Instance != null && Instance != this) { enabled = false; return; }
            Instance = this;
            Publish();
        }

        void OnDisable()
        {
            if (Instance != this) return;
            Instance = null;
            Shader.SetGlobalVector(GrassWindId, new Vector4(1f, 0f, 1f, 0f));
            Shader.SetGlobalVector(WindDirectionId, new Vector4(1f, 0f, 0f, 0f));
            Shader.SetGlobalVector(WindParametersId, Vector4.zero);
            Shader.SetGlobalFloat(WindHeightId, 500f);
        }

        void OnValidate()
        {
            if (direction.sqrMagnitude < .0001f) direction = Vector2.right;
            direction.Normalize();
            speed = Mathf.Max(0f, speed);
            gustPeriod = Mathf.Max(1f, gustPeriod);
            heightReference = Mathf.Max(1f, heightReference);
        }

        void Update() => Publish();

        float EvaluateGust(float time)
        {
            float period = Mathf.Max(1f, gustPeriod);
            return Mathf.Sin(time * Mathf.PI * 2f / period) * .5f +
                   Mathf.Sin(time * Mathf.PI * 2f / (period * .43f) + 1.7f) * .25f;
        }

        void Publish()
        {
            Vector2 dir = Direction;
            Vector4 grass = GrassWindVector;
            Shader.SetGlobalVector(GrassWindId, grass);
            Shader.SetGlobalVector(WindDirectionId, new Vector4(dir.x, dir.y, 0f, 0f));
            Shader.SetGlobalVector(WindParametersId,
                new Vector4(Mathf.Max(0f, speed), Mathf.Clamp01(force), Mathf.Clamp01(gustStrength),
                    Mathf.Clamp01(heightShear)));
            Shader.SetGlobalFloat(WindHeightId, Mathf.Max(1f, heightReference));
        }

        public Vector4 GetCloudWind(Vector2 fallbackDirection, float fallbackSpeed)
        {
            Vector2 dir = Direction;
            float windSpeed = GrassWindVector.z;
            if (Instance == null)
            {
                dir = fallbackDirection.sqrMagnitude > .0001f ? fallbackDirection.normalized : Vector2.right;
                windSpeed = Mathf.Max(0f, fallbackSpeed);
            }
            return new Vector4(dir.x, dir.y, windSpeed, 0f);
        }
    }
}
