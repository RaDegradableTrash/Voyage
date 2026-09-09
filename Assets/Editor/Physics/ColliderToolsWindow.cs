#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Voyage.Editor.Physics
{
    public sealed class ColliderToolsWindow : EditorWindow
    {
        private bool _refitExistingBoxColliders;
        private bool _ignoreDisabled = true;
        private Vector2 _auditScroll;
        private readonly List<ColliderAuditIssue> _issues = new List<ColliderAuditIssue>();
        private readonly List<bool> _selectedIssues = new List<bool>();

        [MenuItem("Tools/Voyage/Collider Tools")]
        public static void Open()
        {
            GetWindow<ColliderToolsWindow>("Collider Tools");
        }

        private void OnGUI()
        {
            DrawAutoBoxSection();
            EditorGUILayout.Space(10f);
            DrawAuditSection();
        }

        private void DrawAutoBoxSection()
        {
            EditorGUILayout.LabelField("Automatic Box Colliders", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Fits each selected GameObject to MeshFilter vertices and SkinnedMeshRenderer " +
                "local bounds found on that object and its children.",
                MessageType.Info);

            _refitExistingBoxColliders = EditorGUILayout.ToggleLeft(
                "Refit an existing BoxCollider", _refitExistingBoxColliders);

            GameObject[] targets = Selection.gameObjects;
            using (new EditorGUI.DisabledScope(targets == null || targets.Length == 0))
            {
                if (GUILayout.Button("Fit Selected GameObjects"))
                    FitSelectedGameObjects(targets);
            }
        }

        private void DrawAuditSection()
        {
            EditorGUILayout.LabelField("Non-convex MeshCollider Audit", EditorStyles.boldLabel);
            _ignoreDisabled = EditorGUILayout.ToggleLeft(
                "Ignore disabled colliders and GameObjects", _ignoreDisabled);

            if (GUILayout.Button("Scan Open Scenes and Asset Prefabs"))
                RunAudit();

            if (_issues.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No audit results. Run a scan to inspect open scenes and prefabs under Assets.",
                    MessageType.None);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(_issues.Count + " issue(s)", EditorStyles.boldLabel);
            if (GUILayout.Button("All", GUILayout.Width(48f)))
                SetAllSelected(true);
            if (GUILayout.Button("None", GUILayout.Width(48f)))
                SetAllSelected(false);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!HasSelectedIssues()))
            {
                if (GUILayout.Button("Make Convex (Selected)"))
                    FixSelected(ColliderAuditFix.MakeConvex);
                if (GUILayout.Button("Make Kinematic (Selected)"))
                    FixSelected(ColliderAuditFix.MakeKinematic);
            }
            EditorGUILayout.EndHorizontal();

            _auditScroll = EditorGUILayout.BeginScrollView(_auditScroll);
            for (int i = 0; i < _issues.Count; i++)
                DrawIssue(i);
            EditorGUILayout.EndScrollView();
        }

        private void DrawIssue(int index)
        {
            ColliderAuditIssue issue = _issues[index];
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            _selectedIssues[index] = EditorGUILayout.Toggle(
                _selectedIssues[index], GUILayout.Width(18f));
            EditorGUILayout.LabelField(issue.DisplayPath, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(20f);
            if (GUILayout.Button("Ping / Select", GUILayout.Width(90f)))
                PingAndSelect(issue);
            if (GUILayout.Button("Make Convex", GUILayout.Width(90f)))
                FixSingle(issue, ColliderAuditFix.MakeConvex);
            if (GUILayout.Button("Make Kinematic", GUILayout.Width(110f)))
                FixSingle(issue, ColliderAuditFix.MakeKinematic);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void FitSelectedGameObjects(GameObject[] targets)
        {
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Fit Box Colliders");
            int fitted = 0;
            int skippedExisting = 0;
            int skippedNoGeometry = 0;

            for (int i = 0; i < targets.Length; i++)
            {
                GameObject target = targets[i];
                if (target == null)
                    continue;

                Collider[] existingColliders = target.GetComponents<Collider>();
                BoxCollider box = target.GetComponent<BoxCollider>();
                if (existingColliders.Length > 0 &&
                    (!_refitExistingBoxColliders || box == null))
                {
                    skippedExisting++;
                    continue;
                }

                Bounds bounds;
                if (!ColliderToolsUtility.TryCalculateLocalBounds(target, true, out bounds))
                {
                    skippedNoGeometry++;
                    continue;
                }

                if (box == null)
                    box = Undo.AddComponent<BoxCollider>(target);
                else
                    Undo.RecordObject(box, "Refit Box Collider");

                box.center = bounds.center;
                box.size = bounds.size;
                EditorUtility.SetDirty(box);
                PrefabUtility.RecordPrefabInstancePropertyModifications(box);
                fitted++;
            }

            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log("Collider Tools fitted " + fitted + " selected object(s). " +
                      skippedExisting + " skipped due to existing colliders; " +
                      skippedNoGeometry + " skipped because no mesh geometry was found.");
        }

        private void RunAudit()
        {
            _issues.Clear();
            _issues.AddRange(ColliderToolsUtility.ScanOpenScenes(_ignoreDisabled));
            _issues.AddRange(ColliderToolsUtility.ScanProjectPrefabs(_ignoreDisabled));
            _selectedIssues.Clear();
            for (int i = 0; i < _issues.Count; i++)
                _selectedIssues.Add(true);
            Repaint();
        }

        private void FixSelected(ColliderAuditFix fix)
        {
            var selected = new List<ColliderAuditIssue>();
            for (int i = 0; i < _issues.Count; i++)
            {
                if (_selectedIssues[i])
                    selected.Add(_issues[i]);
            }

            int changed = ColliderToolsUtility.FixIssues(selected, fix);
            Debug.Log("Collider Tools changed " + changed + " object(s).");
            RunAudit();
        }

        private void FixSingle(ColliderAuditIssue issue, ColliderAuditFix fix)
        {
            int changed = ColliderToolsUtility.FixIssues(
                new[] { issue }, fix);
            Debug.Log("Collider Tools changed " + changed + " object(s).");
            RunAudit();
        }

        private static void PingAndSelect(ColliderAuditIssue issue)
        {
            Object target = issue.IsPrefab ? issue.PingObject : issue.SceneCollider;
            if (target == null)
                return;

            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        private bool HasSelectedIssues()
        {
            for (int i = 0; i < _selectedIssues.Count; i++)
            {
                if (_selectedIssues[i])
                    return true;
            }
            return false;
        }

        private void SetAllSelected(bool selected)
        {
            for (int i = 0; i < _selectedIssues.Count; i++)
                _selectedIssues[i] = selected;
        }
    }
}
#endif
