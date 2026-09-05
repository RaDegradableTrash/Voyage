#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Voyage.Editor.Physics
{
    public static class ColliderAuditBatch
    {
        // Usage:
        // Unity -batchmode -projectPath <path> -executeMethod
        // Voyage.Editor.Physics.ColliderAuditBatch.AuditPrefabs
        public static void AuditPrefabs()
        {
            List<ColliderAuditIssue> issues =
                ColliderToolsUtility.ScanProjectPrefabs(true);
            Debug.Log("Collider audit found " + issues.Count +
                      " problematic non-convex MeshCollider(s) in Asset prefabs.");
            ExitBatchMode(issues.Count == 0 ? 0 : 2);
        }

        // Audits, makes every reported prefab MeshCollider convex, then verifies the result.
        public static void AuditPrefabsAndMakeConvex()
        {
            List<ColliderAuditIssue> issues =
                ColliderToolsUtility.ScanProjectPrefabs(true);
            int changed = ColliderToolsUtility.FixIssues(
                issues, ColliderAuditFix.MakeConvex);
            List<ColliderAuditIssue> remaining =
                ColliderToolsUtility.ScanProjectPrefabs(true);

            Debug.Log("Collider audit made " + changed +
                      " prefab MeshCollider(s) convex; " + remaining.Count +
                      " issue(s) remain.");
            ExitBatchMode(remaining.Count == 0 ? 0 : 2);
        }

        private static void ExitBatchMode(int exitCode)
        {
            if (Application.isBatchMode)
                EditorApplication.Exit(exitCode);
        }
    }
}
#endif
