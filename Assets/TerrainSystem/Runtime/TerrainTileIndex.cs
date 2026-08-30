using System;
using System.Collections.Generic;
using UnityEngine;

namespace Voyage.TerrainSystem
{
    [Serializable]
    public sealed class TerrainTileRecord
    {
        public Vector2Int coordinate;
        public Bounds bounds;
        public string resourcePath;
        public int vertexCount;
        public int triangleCount;
        public int materialCount;
        public long estimatedBytes;
        public bool hasCollision;
        public bool hasHlod;
    }

    [CreateAssetMenu(menuName = "Voyage/Terrain System/Tile Index", fileName = "TerrainTileIndex")]
    public sealed class TerrainTileIndex : ScriptableObject
    {
        public TerrainSourceAsset source;
        public TerrainChunkSettings settings;
        public List<TerrainTileRecord> tiles = new List<TerrainTileRecord>();

        [NonSerialized] private Dictionary<Vector2Int, TerrainTileRecord> lookup;

        public void RebuildLookup()
        {
            lookup = new Dictionary<Vector2Int, TerrainTileRecord>();
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] != null)
                    lookup[tiles[i].coordinate] = tiles[i];
            }
        }

        public bool TryGet(Vector2Int coordinate, out TerrainTileRecord record)
        {
            if (lookup == null) RebuildLookup();
            return lookup.TryGetValue(coordinate, out record);
        }
    }
}
