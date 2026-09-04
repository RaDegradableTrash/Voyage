using NUnit.Framework;
using UnityEngine;
using Voyage.Editor.Physics;

namespace Voyage.Tests.Editor
{
    public sealed class ColliderToolsTests
    {
        [Test]
        public void TryCalculateLocalBounds_TransformsChildVerticesIntoTargetSpace()
        {
            GameObject target = new GameObject("Target");
            GameObject child = new GameObject("Mesh");
            Mesh mesh = new Mesh();
            try
            {
                target.transform.position = new Vector3(7f, -2f, 4f);
                target.transform.rotation = Quaternion.Euler(13f, 29f, -8f);
                target.transform.localScale = new Vector3(2f, 3f, 0.5f);
                child.transform.SetParent(target.transform, false);
                child.transform.localPosition = new Vector3(2f, -1f, 3f);
                child.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                child.transform.localScale = new Vector3(2f, 1f, 0.5f);

                Vector3[] vertices =
                {
                    new Vector3(-1f, -2f, -3f),
                    new Vector3(2f, 1f, 4f),
                    new Vector3(0.5f, 3f, -1f)
                };
                mesh.vertices = vertices;
                mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;

                Bounds actual;
                Assert.That(
                    ColliderToolsUtility.TryCalculateLocalBounds(target, out actual),
                    Is.True);

                Bounds expected = new Bounds(
                    target.transform.InverseTransformPoint(
                        child.transform.TransformPoint(vertices[0])),
                    Vector3.zero);
                for (int i = 1; i < vertices.Length; i++)
                {
                    expected.Encapsulate(target.transform.InverseTransformPoint(
                        child.transform.TransformPoint(vertices[i])));
                }

                AssertVectorApproximately(expected.center, actual.center);
                AssertVectorApproximately(expected.size, actual.size);
            }
            finally
            {
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void IsProblematicMeshCollider_DetectsDynamicAncestorRigidbody()
        {
            GameObject root = new GameObject("DynamicRoot");
            GameObject child = new GameObject("Collider");
            Mesh mesh = CreateTriangleMesh();
            try
            {
                root.SetActive(false);
                child.transform.SetParent(root.transform, false);
                Rigidbody rigidbody = root.AddComponent<Rigidbody>();
                rigidbody.isKinematic = false;
                MeshCollider collider = child.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                collider.convex = false;

                Assert.That(
                    ColliderToolsUtility.HasNonKinematicAncestorRigidbody(collider),
                    Is.True);
                Assert.That(
                    ColliderToolsUtility.IsProblematicMeshCollider(collider, false),
                    Is.True);

                rigidbody.isKinematic = true;
                Assert.That(
                    ColliderToolsUtility.HasNonKinematicAncestorRigidbody(collider),
                    Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void IsProblematicMeshCollider_OptionallySkipsDisabledObjects()
        {
            GameObject root = new GameObject("DynamicRoot");
            GameObject child = new GameObject("Collider");
            Mesh mesh = CreateTriangleMesh();
            try
            {
                root.SetActive(false);
                child.transform.SetParent(root.transform, false);
                root.AddComponent<Rigidbody>().isKinematic = false;
                MeshCollider collider = child.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                collider.convex = false;

                collider.enabled = false;
                root.SetActive(true);
                Assert.That(
                    ColliderToolsUtility.IsProblematicMeshCollider(collider, true),
                    Is.False);
                Assert.That(
                    ColliderToolsUtility.IsProblematicMeshCollider(collider, false),
                    Is.True);

                collider.enabled = true;
                child.SetActive(false);
                Assert.That(
                    ColliderToolsUtility.IsProblematicMeshCollider(collider, true),
                    Is.False);
                Assert.That(
                    ColliderToolsUtility.IsProblematicMeshCollider(collider, false),
                    Is.True);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
            }
        }

        private static Mesh CreateTriangleMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.right,
                Vector3.up
            };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            return mesh;
        }

        private static void AssertVectorApproximately(Vector3 expected, Vector3 actual)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
        }
    }
}
