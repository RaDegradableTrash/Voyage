#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Voyage.TerrainSystem;

namespace Voyage.TerrainSystem.Editor
{
    [CustomEditor(typeof(TerrainTileBakerAnchor))]
    public sealed class TerrainTileBakerAnchorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space(8f);
            if (GUILayout.Button("Open Terrain Tile Baker")) TerrainTileBakerWindow.Open();
        }
    }
}
#endif
