using UnityEngine;

namespace Voyage.TerrainSystem
{
    [CreateAssetMenu(menuName = "Voyage/Terrain System/Source Descriptor", fileName = "TerrainSource")]
    public sealed class TerrainSourceAsset : ScriptableObject
    {
        public GameObject sourceObject;
        public string sourceAssetPath;
        public string sourceGuid;
        public Vector3 sourcePosition;
        public Vector3 sourceEulerAngles;
        public Vector3 sourceScale = Vector3.one;
        public TerrainHorizontalAxes horizontalAxes = TerrainHorizontalAxes.XZ;
        public float metersPerUnit = 1f;
        public Bounds sourceBounds;
        public int meshCount;
        public int rendererCount;
        public int materialCount;
        [TextArea(2, 8)] public string importSettingsSnapshot;

        /// <summary>
        /// Baked terrain tiles retain the imported FBX's vertical coordinates.
        /// Align their shared vertical center with the world Y=0 datum at runtime
        /// without rewriting the generated meshes or the source model.
        /// </summary>
        public float RuntimeVerticalOffset => -sourceBounds.center.y;

        public Vector3 GetRuntimePosition(Vector3 bakedWorldPosition)
        {
            bakedWorldPosition.y += RuntimeVerticalOffset;
            return bakedWorldPosition;
        }
    }
}
