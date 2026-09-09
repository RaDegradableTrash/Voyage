#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Voyage.Editor.Physics
{
    public enum ColliderAuditFix
    {
        MakeConvex,
        MakeKinematic
    }

    public sealed class ColliderAuditIssue
    {
        public string AssetPath { get; internal set; }
        public string HierarchyPath { get; internal set; }
        public string DisplayPath { get; internal set; }
        public int ColliderIndex { get; internal set; }
        public MeshCollider SceneCollider { get; internal set; }
        public Object PingObject { get; internal set; }

        public bool IsPrefab
        {
            get { return !string.IsNullOrEmpty(AssetPath); }
        }
    }

    public static class ColliderToolsUtility
    {
        public static bool TryCalculateLocalBounds(GameObject target, out Bounds bounds)
        {
            return TryCalculateLocalBounds(target, true, out bounds);
        }

        public static bool TryCalculateLocalBounds(GameObject target, bool includeInactive, out Bounds bounds)
        {
            bounds = default(Bounds);
            if (target == null)
                return false;

            bool initialized = false;
            Matrix4x4 worldToTarget = target.transform.worldToLocalMatrix;

            MeshFilter[] filters = target.GetComponentsInChildren<MeshFilter>(includeInactive);
            for (int i = 0; i < filters.Length; i++)
            {
                Mesh mesh = filters[i].sharedMesh;
                if (mesh == null)
                    continue;

                Matrix4x4 toTarget = worldToTarget * filters[i].transform.localToWorldMatrix;
                Vector3[] vertices;
                try
                {
                    vertices = mesh.vertices;
                }
                catch (UnityException exception)
                {
                    Debug.LogWarning("Collider Tools could not read mesh vertices for '" +
                                     filters[i].name + "': " + exception.Message, filters[i]);
                    EncapsulateTransformedBounds(mesh.bounds, toTarget, ref bounds, ref initialized);
                    continue;
                }

                for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                    Encapsulate(toTarget.MultiplyPoint3x4(vertices[vertexIndex]), ref bounds, ref initialized);
            }

            SkinnedMeshRenderer[] skinnedRenderers =
                target.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive);
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                if (skinnedRenderers[i].sharedMesh == null)
                    continue;

                Matrix4x4 toTarget = worldToTarget * skinnedRenderers[i].transform.localToWorldMatrix;
                EncapsulateTransformedBounds(
                    skinnedRenderers[i].localBounds, toTarget, ref bounds, ref initialized);
            }

            return initialized;
        }

        public static bool HasNonKinematicAncestorRigidbody(MeshCollider collider)
        {
            return GetNonKinematicAncestorRigidbody(collider) != null;
        }

        public static Rigidbody GetNonKinematicAncestorRigidbody(MeshCollider collider)
        {
            if (collider == null)
                return null;

            Transform current = collider.transform;
            while (current != null)
            {
                Rigidbody rigidbody = current.GetComponent<Rigidbody>();
                if (rigidbody != null)
                    return rigidbody.isKinematic ? null : rigidbody;
                current = current.parent;
            }

            return null;
        }

        public static bool IsProblematicMeshCollider(MeshCollider collider, bool ignoreDisabled)
        {
            if (collider == null || collider.convex || collider.sharedMesh == null)
                return false;

            if (ignoreDisabled && (!collider.enabled || !collider.gameObject.activeInHierarchy))
                return false;

            return HasNonKinematicAncestorRigidbody(collider);
        }

        public static List<ColliderAuditIssue> ScanOpenScenes(bool ignoreDisabled)
        {
            var issues = new List<ColliderAuditIssue>();
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;

                GameObject[] roots = scene.GetRootGameObjects();
                for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                {
                    MeshCollider[] colliders = roots[rootIndex].GetComponentsInChildren<MeshCollider>(true);
                    for (int colliderIndex = 0; colliderIndex < colliders.Length; colliderIndex++)
                    {
                        MeshCollider collider = colliders[colliderIndex];
                        if (!IsProblematicMeshCollider(collider, ignoreDisabled))
                            continue;

                        issues.Add(new ColliderAuditIssue
                        {
                            DisplayPath = scene.name + "/" + GetNamedHierarchyPath(collider.transform),
                            SceneCollider = collider,
                            PingObject = collider.gameObject,
                            ColliderIndex = GetComponentIndex(collider)
                        });
                    }
                }
            }

            return issues;
        }

        public static List<ColliderAuditIssue> ScanProjectPrefabs(bool ignoreDisabled)
        {
            var issues = new List<ColliderAuditIssue>();
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                ScanPrefab(assetPath, ignoreDisabled, issues);
            }

            return issues;
        }

        public static int FixIssues(IList<ColliderAuditIssue> issues, ColliderAuditFix fix)
        {
            if (issues == null)
                return 0;

            int changed = 0;
            var prefabIssues = new Dictionary<string, List<ColliderAuditIssue>>();
            for (int i = 0; i < issues.Count; i++)
            {
                ColliderAuditIssue issue = issues[i];
                if (issue == null)
                    continue;

                if (issue.IsPrefab)
                {
                    List<ColliderAuditIssue> grouped;
                    if (!prefabIssues.TryGetValue(issue.AssetPath, out grouped))
                    {
                        grouped = new List<ColliderAuditIssue>();
                        prefabIssues.Add(issue.AssetPath, grouped);
                    }
                    grouped.Add(issue);
                }
                else if (FixSceneIssue(issue, fix))
                {
                    changed++;
                }
            }

            foreach (KeyValuePair<string, List<ColliderAuditIssue>> pair in prefabIssues)
                changed += FixPrefabIssues(pair.Key, pair.Value, fix);

            if (prefabIssues.Count > 0)
                AssetDatabase.SaveAssets();

            return changed;
        }

        public static int MakeAllPrefabMeshCollidersConvex(bool ignoreDisabled)
        {
            List<ColliderAuditIssue> issues = ScanProjectPrefabs(ignoreDisabled);
            return FixIssues(issues, ColliderAuditFix.MakeConvex);
        }

        private static void Encapsulate(
            Vector3 point, ref Bounds bounds, ref bool initialized)
        {
            if (!initialized)
            {
                bounds = new Bounds(point, Vector3.zero);
                initialized = true;
            }
            else
            {
                bounds.Encapsulate(point);
            }
        }

        private static void EncapsulateTransformedBounds(
            Bounds source, Matrix4x4 transform, ref Bounds result, ref bool initialized)
        {
            Vector3 center = source.center;
            Vector3 extents = source.extents;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 corner = center + Vector3.Scale(
                            extents, new Vector3(x, y, z));
                        Encapsulate(transform.MultiplyPoint3x4(corner), ref result, ref initialized);
                    }
                }
            }
        }

        private static void ScanPrefab(
            string assetPath, bool ignoreDisabled, List<ColliderAuditIssue> issues)
        {
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(assetPath);
                MeshCollider[] colliders = root.GetComponentsInChildren<MeshCollider>(true);
                Object prefabAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                for (int i = 0; i < colliders.Length; i++)
                {
                    MeshCollider collider = colliders[i];
                    if (!IsProblematicMeshCollider(collider, ignoreDisabled))
                        continue;

                    issues.Add(new ColliderAuditIssue
                    {
                        AssetPath = assetPath,
                        HierarchyPath = GetIndexedHierarchyPath(root.transform, collider.transform),
                        DisplayPath = assetPath + "/" + GetNamedHierarchyPath(collider.transform, root.transform),
                        ColliderIndex = GetComponentIndex(collider),
                        PingObject = prefabAsset
                    });
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("Collider audit failed to inspect prefab '" + assetPath +
                               "': " + exception.Message);
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static bool FixSceneIssue(ColliderAuditIssue issue, ColliderAuditFix fix)
        {
            MeshCollider collider = issue.SceneCollider;
            if (collider == null)
                return false;

            Object changedObject;
            if (fix == ColliderAuditFix.MakeConvex)
            {
                if (collider.convex)
                    return false;
                changedObject = collider;
                Undo.RecordObject(collider, "Make Mesh Collider Convex");
                collider.convex = true;
            }
            else
            {
                Rigidbody rigidbody = GetNonKinematicAncestorRigidbody(collider);
                if (rigidbody == null)
                    return false;
                changedObject = rigidbody;
                Undo.RecordObject(rigidbody, "Make Rigidbody Kinematic");
                rigidbody.isKinematic = true;
            }

            EditorUtility.SetDirty(changedObject);
            PrefabUtility.RecordPrefabInstancePropertyModifications(changedObject);
            Scene scene = collider.gameObject.scene;
            if (scene.IsValid())
                EditorSceneManager.MarkSceneDirty(scene);
            return true;
        }

        private static int FixPrefabIssues(
            string assetPath, List<ColliderAuditIssue> issues, ColliderAuditFix fix)
        {
            GameObject root = null;
            int changed = 0;
            try
            {
                root = PrefabUtility.LoadPrefabContents(assetPath);
                var changedRigidbodies = new HashSet<Rigidbody>();
                for (int i = 0; i < issues.Count; i++)
                {
                    Transform transform = ResolveIndexedHierarchyPath(
                        root.transform, issues[i].HierarchyPath);
                    if (transform == null)
                        continue;

                    MeshCollider collider = GetComponentAtIndex<MeshCollider>(
                        transform.gameObject, issues[i].ColliderIndex);
                    if (collider == null)
                        continue;

                    if (fix == ColliderAuditFix.MakeConvex)
                    {
                        if (collider.convex)
                            continue;
                        collider.convex = true;
                        EditorUtility.SetDirty(collider);
                        changed++;
                    }
                    else
                    {
                        Rigidbody rigidbody = GetNonKinematicAncestorRigidbody(collider);
                        if (rigidbody == null || !changedRigidbodies.Add(rigidbody))
                            continue;
                        rigidbody.isKinematic = true;
                        EditorUtility.SetDirty(rigidbody);
                        changed++;
                    }
                }

                if (changed > 0)
                    PrefabUtility.SaveAsPrefabAsset(root, assetPath);
            }
            catch (Exception exception)
            {
                Debug.LogError("Collider audit failed to fix prefab '" + assetPath +
                               "': " + exception.Message);
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }

            return changed;
        }

        private static int GetComponentIndex<T>(T component) where T : Component
        {
            T[] components = component.GetComponents<T>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == component)
                    return i;
            }
            return -1;
        }

        private static T GetComponentAtIndex<T>(GameObject gameObject, int index)
            where T : Component
        {
            T[] components = gameObject.GetComponents<T>();
            return index >= 0 && index < components.Length ? components[index] : null;
        }

        private static string GetIndexedHierarchyPath(Transform root, Transform target)
        {
            if (target == root)
                return string.Empty;

            var indices = new List<int>();
            Transform current = target;
            while (current != null && current != root)
            {
                indices.Add(current.GetSiblingIndex());
                current = current.parent;
            }
            indices.Reverse();
            return string.Join("/", indices.ConvertAll(index => index.ToString()).ToArray());
        }

        private static Transform ResolveIndexedHierarchyPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path))
                return root;

            Transform current = root;
            string[] segments = path.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                int index;
                if (!int.TryParse(segments[i], out index) ||
                    index < 0 || index >= current.childCount)
                    return null;
                current = current.GetChild(index);
            }
            return current;
        }

        private static string GetNamedHierarchyPath(Transform target, Transform stopAt = null)
        {
            var names = new List<string>();
            Transform current = target;
            while (current != null)
            {
                names.Add(current.name);
                if (current == stopAt)
                    break;
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }
    }
}
#endif
