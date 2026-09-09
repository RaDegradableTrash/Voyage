using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Voyage.Tests.Editor
{
    public sealed class VehicleControlTests
    {
        const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static Type GameType(string name) => Type.GetType(name + ", Assembly-CSharp", true);

        [TestCase(0f)]
        [TestCase(-152.683f)]
        public void ImportedWheelKeepsDimensionsButSuspendsAlongVehicleUp(float heading)
        {
            var vehicle = new GameObject("Vehicle");
            try
            {
                vehicle.SetActive(false);
                vehicle.transform.SetPositionAndRotation(new Vector3(23, 5, -42), Quaternion.Euler(0, heading, 0));
                vehicle.AddComponent<Rigidbody>();
                var chassis = new GameObject("Imported chassis").transform;
                chassis.SetParent(vehicle.transform, false);
                chassis.localScale = Vector3.one * 100;
                chassis.localRotation = Quaternion.Euler(-90, 0, 0);
                var wheelObject = new GameObject("Wheel");
                wheelObject.transform.SetParent(chassis, false);
                wheelObject.transform.localPosition = new Vector3(.017f, -.0083f, -.0211f);
                var collider = wheelObject.AddComponent<WheelCollider>();
                collider.radius = .01f;
                collider.suspensionDistance = .01f;
                collider.center = new Vector3(.001f, .002f, .003f);
                Vector3 center = collider.transform.TransformPoint(collider.center);
                Component wheel = wheelObject.AddComponent(GameType("WheelControl"));
                Component binder = vehicle.AddComponent(GameType("ReferenceVehicleRuntimeBinder"));
                binder.GetType().GetMethod("NormalizeWheelPhysics", Members).Invoke(binder, null);

                Assert.That(Vector3.Distance(collider.transform.TransformPoint(collider.center), center), Is.LessThan(.0001f));
                Assert.That(Vector3.Dot(collider.transform.up, vehicle.transform.up), Is.GreaterThan(.9999f));
                Assert.That(collider.radius, Is.EqualTo(1).Within(.0001f));
                Assert.That(collider.suspensionDistance, Is.EqualTo(1).Within(.0001f));
                Assert.That(wheel.GetType().GetField("WheelCollider").GetValue(wheel), Is.SameAs(collider));
                Assert.That(vehicle.GetComponentsInChildren<WheelCollider>(true).Length, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(vehicle); }
        }

        [TestCase(1f)]
        [TestCase(100f)]
        public void TransmissionRpmUsesWorldRadius(float scale)
        {
            var vehicle = new GameObject("Vehicle");
            try
            {
                vehicle.SetActive(false);
                var wheelObject = new GameObject("Wheel");
                wheelObject.transform.SetParent(vehicle.transform, false);
                wheelObject.transform.localScale = Vector3.one * scale;
                var collider = wheelObject.AddComponent<WheelCollider>();
                collider.radius = 1f / scale;
                Type wheelType = GameType("WheelControl");
                Component wheel = wheelObject.AddComponent(wheelType);
                wheelType.GetMethod("BindCollider").Invoke(wheel, new object[] { collider });
                Component car = vehicle.AddComponent(GameType("CarControl"));
                Array wheels = Array.CreateInstance(wheelType, 1);
                wheels.SetValue(wheel, 0);
                car.GetType().GetField("wheels", Members).SetValue(car, wheels);
                var rpm = (float)car.GetType().GetMethod("GetStableWheelRpm", Members).Invoke(car, new object[] { 2 * Mathf.PI });
                Assert.That(rpm, Is.EqualTo(60).Within(.001f));
                var forward = (Vector3)car.GetType().GetProperty("DriveForward").GetValue(car);
                Assert.That(Vector3.Dot(forward, -vehicle.transform.forward), Is.GreaterThan(.9999f));
            }
            finally { Object.DestroyImmediate(vehicle); }
        }
    }
}
