using UnityEngine;

namespace Voyage.TerrainSystem
{
    public sealed class TerrainTileBakerAnchor : MonoBehaviour
    {
        public GameObject sourceObject;
        public TerrainChunkSettings settings;
        public bool drawGrid = true;
        public int previewRadius = 8;

        private void OnDrawGizmosSelected()
        {
            if (!drawGrid || settings == null) return;
            Bounds sourceBounds = sourceObject != null ? CalculateBounds(sourceObject) : new Bounds(transform.position, Vector3.one * settings.tileSize);
            Vector2Int min = settings.WorldToTile(sourceBounds.min);
            Vector2Int max = settings.WorldToTile(sourceBounds.max);
            min -= Vector2Int.one * previewRadius;
            max += Vector2Int.one * previewRadius;
            for (int z = min.y; z <= max.y; z++)
            for (int x = min.x; x <= max.x; x++)
            {
                Vector2Int coordinate = new Vector2Int(x, z);
                Bounds tile = settings.GetTileBounds(coordinate);
                Gizmos.color = coordinate == settings.WorldToTile(transform.position) ? Color.yellow : new Color(0f, 1f, 1f, 0.35f);
                Gizmos.DrawWireCube(tile.center, new Vector3(tile.size.x, 1f, tile.size.z));
            }
        }

        private static Bounds CalculateBounds(GameObject source)
        {
            MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>(true);
            Bounds result = new Bounds(source.transform.position, Vector3.zero);
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i].sharedMesh == null) continue;
                Bounds local = filters[i].sharedMesh.bounds;
                result.Encapsulate(filters[i].transform.TransformPoint(local.min));
                result.Encapsulate(filters[i].transform.TransformPoint(local.max));
            }
            return result;
        }
    }
}
