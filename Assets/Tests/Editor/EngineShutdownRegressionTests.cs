using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.TestTools;
using Object=UnityEngine.Object;
namespace Voyage.Tests.Editor
{
    public class EngineShutdownRegressionTests
    {
        [UnityTest] public IEnumerator RestartCancelsAnOlderDelayedShutdown()
        {
            yield return new EnterPlayMode();
            var type=Type.GetType("StartProcedure, Assembly-CSharp",true);
            var go=new GameObject("Shutdown cancellation test"); var procedure=go.AddComponent(type);
            type.GetField("shutdownDelaySeconds",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(procedure,.05f);
            type.GetMethod("ForceStartVehicle").Invoke(procedure,null);
            type.GetMethod("ToggleEngine").Invoke(procedure,null);
            type.GetMethod("ForceShutdownEngine").Invoke(procedure,null);
            type.GetMethod("ForceStartVehicle").Invoke(procedure,null);
            yield return new WaitForSeconds(.12f);
            bool stillRunning=(bool)type.GetProperty("EngineOn").GetValue(procedure);
            Object.DestroyImmediate(go);
            yield return new ExitPlayMode();
            Assert.That(stillRunning,Is.True,"A previous delayed shutdown must not stop a newly restarted engine.");
        }
    }
}
