using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Voyage.TerrainSystem
{
    /// <summary>Persistent world-space tire marks, kept separate from temporary bend state.</summary>
    [DisallowMultipleComponent]
    public sealed class GrassPermanentTrackStore : MonoBehaviour
    {
        [Serializable]
        public struct TrackSample
        {
            public Vector3 position;
            public Vector2 direction;
            public float radius;
            public float strength;
        }

        [Serializable]
        sealed class SaveData
        {
            public List<TrackSample> samples = new List<TrackSample>();
        }

        [Min(256)] public int maxSamples = 8192;
        [Min(0.1f)] public float sampleSpacing = 0.8f;
        [Min(0.25f)] public float saveInterval = 2f;
        public string fileName = "voyage-grass-tracks.json";

        readonly List<TrackSample> samples = new List<TrackSample>();
        readonly Dictionary<Transform, Vector3> lastRecordedBySource = new Dictionary<Transform, Vector3>();
        bool dirty;
        float nextSaveTime;

        public IReadOnlyList<TrackSample> Samples => samples;

        void OnValidate()
        {
            maxSamples = Mathf.Max(256, maxSamples);
            sampleSpacing = Mathf.Max(0.1f, sampleSpacing);
            saveInterval = Mathf.Max(0.25f, saveInterval);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "voyage-grass-tracks.json";
        }

        void Awake()
        {
            Load();
        }

        public void RecordSegment(Vector3 from, Vector3 to, float radius, float strength, Transform source)
        {
            Vector2 delta = new Vector2(to.x - from.x, to.z - from.z);
            float distance = delta.magnitude;
            float spacing = Mathf.Max(0.1f, sampleSpacing);
            int steps = Mathf.Max(1, Mathf.CeilToInt(distance / spacing));
            Vector2 direction = distance > 0.001f ? delta / distance : Vector2.up;
            bool recorded = false;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector3 position = Vector3.Lerp(from, to, t);
                if (source != null && lastRecordedBySource.TryGetValue(source, out Vector3 last))
                {
                    Vector2 gap = new Vector2(position.x - last.x, position.z - last.z);
                    if (gap.sqrMagnitude < spacing * spacing * 0.25f) continue;
                }
                samples.Add(new TrackSample
                {
                    position = position,
                    direction = direction,
                    radius = Mathf.Max(0.01f, radius),
                    strength = Mathf.Clamp01(strength)
                });
                if (source != null) lastRecordedBySource[source] = position;
                recorded = true;
            }
            while (samples.Count > maxSamples) samples.RemoveAt(0);
            if (recorded) dirty = true;
        }

        public void ForgetSource(Transform source)
        {
            if (source != null) lastRecordedBySource.Remove(source);
        }

        public void CopySamples(Bounds area, List<TrackSample> destination)
        {
            if (destination == null) return;
            destination.Clear();
            for (int i = 0; i < samples.Count; i++)
            {
                TrackSample sample = samples[i];
                float radius = Mathf.Max(0f, sample.radius);
                if (Mathf.Abs(sample.position.x - area.center.x) <= area.extents.x + radius &&
                    Mathf.Abs(sample.position.z - area.center.z) <= area.extents.z + radius)
                    destination.Add(sample);
            }
        }

        void Update()
        {
            if (dirty && Time.unscaledTime >= nextSaveTime) Save();
        }

        void OnApplicationPause(bool pause)
        {
            if (pause) Save();
        }

        void OnDisable()
        {
            // A streamed scene or its interaction manager can be disabled
            // without the application quitting. Flush the same durable
            // snapshot here so that lifecycle changes do not discard the
            // most recent wheel samples.
            Save();
        }

        void OnApplicationQuit()
        {
            Save();
        }

        void Save()
        {
            if (!dirty) return;
            try
            {
                string path = Path.Combine(Application.persistentDataPath, fileName);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(new SaveData { samples = samples }));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                dirty = false;
                nextSaveTime = Time.unscaledTime + saveInterval;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Grass tracks could not be saved: " + exception.Message);
                try
                {
                    string temporary = Path.Combine(Application.persistentDataPath, fileName + ".tmp");
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch { }
                nextSaveTime = Time.unscaledTime + saveInterval;
            }
        }

        void Load()
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, fileName);
                if (!File.Exists(path)) return;
                SaveData data = JsonUtility.FromJson<SaveData>(File.ReadAllText(path));
                if (data != null && data.samples != null) samples.AddRange(data.samples);
                while (samples.Count > maxSamples) samples.RemoveAt(0);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Grass tracks could not be loaded: " + exception.Message);
            }
        }
    }
}
