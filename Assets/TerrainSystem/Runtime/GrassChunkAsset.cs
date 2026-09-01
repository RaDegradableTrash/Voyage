using UnityEngine;

namespace Voyage.TerrainSystem
{
    [CreateAssetMenu(menuName = "Voyage/Terrain System/Grass Chunk", fileName = "GrassChunk")]
    public sealed class GrassChunkAsset : ScriptableObject
    {
        public Mesh clusterMesh;
        public Vector3[] positions;
        // Instance rotation as a quaternion, aligned to the sampled ground normal.
        public Vector4[] parameters;
        public float[] scales;

        public int Count
        {
            get { return positions == null ? 0 : positions.Length; }
        }
    }
}
