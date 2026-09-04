using System;
using UnityEngine;

namespace Voyage.TerrainSystem
{
    public enum TerrainHorizontalAxes
    {
        XZ,
        XY
    }

    public enum TerrainTriangleBoundaryPolicy
    {
        AssignToSingleTile,
        DuplicateToAdjacentTiles,
        ClipDerivedMesh
    }

    [CreateAssetMenu(menuName = "Voyage/Terrain System/Chunk Settings", fileName = "TerrainChunkSettings")]
    public sealed class TerrainChunkSettings : ScriptableObject
    {
        [Min(1f)] public float tileSize = 256f;
        [Min(0f)] public float boundaryOverlap = 0f;
        public Vector3 worldOrigin = Vector3.zero;
        public TerrainHorizontalAxes horizontalAxes = TerrainHorizontalAxes.XZ;
        public TerrainTriangleBoundaryPolicy triangleBoundaryPolicy = TerrainTriangleBoundaryPolicy.ClipDerivedMesh;
        [Min(0.000001f)] public float boundaryPositionTolerance = 0.0001f;
        [Min(0.000001f)] public float boundaryHeightPrecision = 0.0001f;
        [Min(0.001f)] public float sourceMetersPerUnit = 1f;
        [Tooltip("整体缩放源 FBX 后再分块。若 FBX 被 Unity 按 100 倍导入，设为 0.01。源 FBX 本身不会被修改。")]
        public string tileNameFormat = "Terrain_{0}_{1}";
        [Tooltip("Rebuild normals on generated terrain meshes so slopes and shadow receiving use the actual derived geometry.")]
        public bool recalculateNormals = true;

        [Header("Baked grass")]
        [Tooltip("Bake deterministic per-tile grass placement so streaming never raycasts thousands of candidates at runtime.")]
        public bool bakeGrass = true;
        [Min(0.25f)] public float grassClusterSpacing = 0.34f;
        [Min(1)] public int grassBladesPerCluster = 18;
        [Min(0.05f)] public float grassClusterRadius = 0.70f;
        [Min(0.1f)] public float grassBladeHeight = 1.75f;
        [Range(0f, 1f)] public float grassDensity = 1f;
        [Min(1)] public int grassClusterBudget = 80000;
        [Header("Stylized grass appearance")]
        public Color grassBaseColor = new Color(0.64f, 0.42f, 0.14f, 1f);
        public Color grassRootColor = new Color(0.48f, 0.33f, 0.12f, 1f);
        public Color grassShadowColor = new Color(0.40f, 0.28f, 0.10f, 1f);
        public Color grassTipColor = new Color(0.78f, 0.56f, 0.22f, 1f);
        public Color grassBacksideColor = new Color(0.57f, 0.37f, 0.12f, 1f);
        public Color grassFadeColor = new Color(0.36f, 0.24f, 0.09f, 1f);
        [Min(0.001f)] public float grassMacroScale = 0.018f;
        [Range(0f, 1f)] public float grassMacroStrength = 0.20f;
        [Range(0f, 1f)] public float grassAlphaClip = 0.35f;
        [Min(0f)] public float grassFadeStart = 105f;
        [Min(0.01f)] public float grassFadeEnd = 495f;
        public Vector2 grassWindDirection = new Vector2(0.86f, 0.28f);
        [Min(0f)] public float grassWindSpeed = 1f;
        [Range(0f, 1f)] public float grassWindGust = 0.28f;
        [Tooltip("Use GPU-driven indirect grass drawing when the culling compute shader is available.")]
        public bool useIndirectGrass = true;
        [Range(0f, 89f)] public float grassFullDensityBelowSlope = 28f;
        [Range(1f, 90f)] public float grassNoGrassAboveSlope = 58f;

        [Header("LOD distances in metres")]
        public float lod0Distance = 90f;
        public float lod1Distance = 240f;
        public float lod2Distance = 650f;
        public float lod3Distance = 3000f;
        [Range(0.01f, 1f)] public float lod1Quality = 0.5f;
        [Range(0.01f, 1f)] public float lod2Quality = 0.25f;
        [Range(0.01f, 1f)] public float lod3Quality = 0.1f;
        public bool useCrossFade = true;
        public bool generateHlod = true;
        public bool generateSkirts = true;
        [Min(0f)] public float skirtDepth = 2f;
        [Range(0, 3)] public int maxNeighborLodDifference = 1;

        [Header("Streaming")]
        [Min(1f)] public float streamingCellSize = 512f;
        [Min(0)] public int loadedRadius = 1;
        [Min(0)] public int preloadRadius = 2;
        [Min(1)] public int unloadRadius = 3;
        [Min(1)] public int maxConcurrentLoads = 1;
        [Min(0.1f)] public float retryDelay = 2f;
        [Min(0)] public int maxLoadRetries = 3;
        public bool prioritizeForward = true;
        public bool enableCollisionWhenLoaded = true;
        [Min(0)] public int collisionRadius = 2;

        [Header("View-driven visual streaming")]
        [Min(0f)] public float visualDistanceOverride = 0f;
        [Min(0f)] public float visualTileMargin = 256f;

        public Vector2Int WorldToTile(Vector3 worldPosition)
        {
            Vector3 local = worldPosition - worldOrigin;
            float horizontal = horizontalAxes == TerrainHorizontalAxes.XZ ? local.x : local.x;
            float depth = horizontalAxes == TerrainHorizontalAxes.XZ ? local.z : local.y;
            return new Vector2Int(Mathf.FloorToInt(horizontal / tileSize), Mathf.FloorToInt(depth / tileSize));
        }

        public Bounds GetTileBounds(Vector2Int coordinate, float padding = 0f)
        {
            float minHorizontal = worldOrigin.x + coordinate.x * tileSize - padding;
            float minDepth = (horizontalAxes == TerrainHorizontalAxes.XZ ? worldOrigin.z : worldOrigin.y) + coordinate.y * tileSize - padding;
            Vector3 center;
            Vector3 size;
            if (horizontalAxes == TerrainHorizontalAxes.XZ)
            {
                center = new Vector3(minHorizontal + (tileSize + padding * 2f) * 0.5f, worldOrigin.y, minDepth + (tileSize + padding * 2f) * 0.5f);
                size = new Vector3(tileSize + padding * 2f, 100000f, tileSize + padding * 2f);
            }
            else
            {
                center = new Vector3(minHorizontal + (tileSize + padding * 2f) * 0.5f, minDepth + (tileSize + padding * 2f) * 0.5f, worldOrigin.z);
                size = new Vector3(tileSize + padding * 2f, tileSize + padding * 2f, 100000f);
            }
            return new Bounds(center, size);
        }

        public float GetLodDistance(int lod)
        {
            switch (lod)
            {
                case 0: return lod0Distance;
                case 1: return lod1Distance;
                case 2: return lod2Distance;
                default: return lod3Distance;
            }
        }

        public float GetLodQuality(int lod)
        {
            switch (lod)
            {
                case 0: return 1f;
                case 1: return lod1Quality;
                case 2: return lod2Quality;
                default: return lod3Quality;
            }
        }

        private void OnValidate()
        {
            lod1Distance = Mathf.Max(lod0Distance, lod1Distance);
            lod2Distance = Mathf.Max(lod1Distance, lod2Distance);
            lod3Distance = Mathf.Max(lod2Distance, lod3Distance);
            preloadRadius = Mathf.Max(loadedRadius, preloadRadius);
            unloadRadius = Mathf.Max(preloadRadius + 1, unloadRadius);
            collisionRadius = Mathf.Clamp(collisionRadius, 0, preloadRadius);
            grassClusterSpacing = Mathf.Max(0.25f, grassClusterSpacing);
            grassBladesPerCluster = Mathf.Clamp(grassBladesPerCluster, 1, 32);
            grassClusterRadius = Mathf.Max(0.05f, grassClusterRadius);
            grassBladeHeight = Mathf.Max(0.1f, grassBladeHeight);
            grassDensity = Mathf.Clamp01(grassDensity);
            grassClusterBudget = Mathf.Max(1, grassClusterBudget);
            grassMacroScale = Mathf.Max(0.001f, grassMacroScale);
            grassMacroStrength = Mathf.Clamp01(grassMacroStrength);
            grassAlphaClip = Mathf.Clamp01(grassAlphaClip);
            grassFadeStart = Mathf.Max(0f, grassFadeStart);
            grassFadeEnd = Mathf.Max(grassFadeStart + 0.01f, grassFadeEnd);
            if (grassWindDirection.sqrMagnitude < 0.0001f) grassWindDirection = Vector2.right;
            grassWindDirection.Normalize();
            grassWindSpeed = Mathf.Max(0f, grassWindSpeed);
            grassWindGust = Mathf.Clamp01(grassWindGust);
            grassFullDensityBelowSlope = Mathf.Clamp(grassFullDensityBelowSlope, 0f, 89f);
            grassNoGrassAboveSlope = Mathf.Clamp(grassNoGrassAboveSlope, grassFullDensityBelowSlope + 1f, 90f);
        }
    }
}
