using UnityEngine;

namespace Voyage.TerrainSystem
{
    /// <summary>
    /// Shared grass-cluster geometry. Tile-specific placement is generated at
    /// runtime, so the prototype is saved once instead of once per tile.
    /// </summary>
    [CreateAssetMenu(menuName = "Voyage/Terrain System/Grass Prototype", fileName = "GrassPrototype")]
    public sealed class GrassPrototypeAsset : ScriptableObject
    {
        public Mesh clusterMesh;
        public Material material;
        public int bladesPerCluster;
        public float clusterRadius;
        public float bladeHeight;
    }
}
