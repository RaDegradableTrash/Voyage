using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Voyage.Tests.Editor
{
    public sealed class VehicleAirbornePitchTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static Type GameType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(name)).First(t => t != null);
        static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);

        [TestCase(0f, 1f)]
        [TestCase(137f, 1f)]
        [TestCase(-80f, -1f)]
        public void RealRvTakeoffPitchSettlesWithoutChangingFlightPath(float heading, float pitchSign)
        {
            Scene scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            float previousStep = Time.fixedDeltaTime;
            Time.fixedDeltaTime = .02f;
            try
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Prefabs/RV1.0.prefab");
                Assert.That(prefab, Is.Not.Null);
                Quaternion launch = Quaternion.Euler(0f, heading, 0f);
                Rigidbody[] bodies = new Rigidbody[2];
                Component[] cars = new Component[2];
                Vector3[] starts = new Vector3[2];
                for (int i = 0; i < 2; i++)
                {
                    var rv = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    PrefabUtility.UnpackPrefabInstance(rv, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    starts[i] = new Vector3(i * 100f, 100f, 0f);
                    rv.transform.SetPositionAndRotation(starts[i], launch);
                    foreach (var behaviour in rv.GetComponentsInChildren<MonoBehaviour>(true))
                        if (behaviour != null) behaviour.enabled = false;
                    Component binder = rv.GetComponent(GameType("ReferenceVehicleRuntimeBinder"))
                        ?? rv.AddComponent(GameType("ReferenceVehicleRuntimeBinder"));
                    Call(binder, "ConfigureChassisPhysics");
                    Call(binder, "NormalizeWheelPhysics");
                    bodies[i] = rv.GetComponent<Rigidbody>();
                    cars[i] = rv.GetComponent(GameType("CarControl"));
                    Type wheelType = GameType("WheelControl");
                    Component[] wheels = rv.GetComponentsInChildren(wheelType);
                    Array wheelArray = Array.CreateInstance(wheelType, wheels.Length);
                    Array.Copy(wheels, wheelArray, wheels.Length);
                    Assert.That(wheels.Length, Is.EqualTo(6));
                    Set(cars[i], "rigidBody", bodies[i]);
                    Set(cars[i], "wheels", wheelArray);
                    Set(cars[i], "authoredCenterOfMass", bodies[i].centerOfMass);
                    if (i == 0) Set(cars[i], "airbornePitchDamping", 0f); // Reproduce the old air path.
                    // Last wheel contact on a crest can leave upward pitch momentum.
                    Call(cars[i], "SetGroundedCenterOfMass", true);
                    bodies[i].linearVelocity = launch * new Vector3(0f, 8f, -30f);
                    bodies[i].angularVelocity = launch * Vector3.right * (.8f * pitchSign);
                }
                Physics.SyncTransforms();
                PhysicsScene physics = scene.GetPhysicsScene();
                Assert.That(physics, Is.Not.EqualTo(Physics.defaultPhysicsScene), "Keep physics isolated from open scenes.");
                for (int step = 0; step < 100; step++)
                {
                    foreach (Component car in cars) Call(car, "FixedUpdate");
                    physics.Simulate(Time.fixedDeltaTime);
                }
                float oldRotation = Quaternion.Angle(launch, bodies[0].rotation);
                float fixedRotation = Quaternion.Angle(launch, bodies[1].rotation);
                TestContext.WriteLine($"Heading {heading}, pitch sign {pitchSign}: old {oldRotation:F2} deg; fixed {fixedRotation:F2} deg");
                Assert.That(oldRotation, Is.GreaterThan(70f), "The undamped takeoff momentum must reproduce the tilt.");
                Assert.That(fixedRotation, Is.InRange(5f, 15f), "Retain a natural launch attitude without continuing to tip.");
                Assert.That(bodies[1].angularVelocity.magnitude, Is.LessThan(.001f));
                Assert.That(Vector3.Distance(bodies[0].linearVelocity, bodies[1].linearVelocity), Is.LessThan(.001f));
                Assert.That(Vector3.Distance(bodies[0].worldCenterOfMass - starts[0], bodies[1].worldCenterOfMass - starts[1]),
                    Is.LessThan(.01f), "Pitch stabilization must not change the center-of-mass flight path.");
            }
            finally
            {
                Time.fixedDeltaTime = previousStep;
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void AirborneDampingPreservesYawRollAndGroundedRotation()
        {
            var go = new GameObject("Airborne axes regression");
            try
            {
                Component car = go.AddComponent(GameType("CarControl"));
                var body = go.GetComponent<Rigidbody>();
                Set(car, "rigidBody", body);
                body.rotation = Quaternion.Euler(18f, 123f, -12f);
                Vector3 original = body.rotation * new Vector3(.8f, .4f, -.3f);
                body.angularVelocity = original;
                Call(car, "ApplyAirbornePitchDamping", true);
                Assert.That(Vector3.Distance(body.angularVelocity, original), Is.LessThan(.0001f), "Grounded suspension must remain in control.");
                Call(car, "ApplyAirbornePitchDamping", false);
                Vector3 local = Quaternion.Inverse(body.rotation) * body.angularVelocity;
                Assert.That(local.x, Is.InRange(0f, .799f));
                Assert.That(local.y, Is.EqualTo(.4f).Within(.0001f));
                Assert.That(local.z, Is.EqualTo(-.3f).Within(.0001f));
                body.angularVelocity = Vector3.zero;
                Quaternion attitude = body.rotation;
                Call(car, "ApplyAirbornePitchDamping", false);
                Assert.That(body.angularVelocity, Is.EqualTo(Vector3.zero), "An already stable vehicle must not receive leveling torque.");
                Assert.That(Quaternion.Angle(body.rotation, attitude), Is.LessThan(.001f));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
