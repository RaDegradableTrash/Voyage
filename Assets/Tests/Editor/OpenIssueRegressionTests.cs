using System;
using System.Collections.Generic;
using System.Reflection;
using System.Collections;
using UnityEngine.TestTools;
using UnityEditor.TestTools;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Voyage.Tests.Editor
{
    public class OpenIssueRegressionTests
    {
        const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        readonly List<Object> cleanup=new List<Object>();
        static Type TypeOf(string name)=>Type.GetType(name+", Assembly-CSharp",true);
        static object Call(object value,string name,params object[] args)=>value.GetType().GetMethod(name,Flags).Invoke(value,args);
        static void Set(object value,string name,object data)=>value.GetType().GetField(name,Flags).SetValue(value,data);
        GameObject Go(string name) {var go=new GameObject(name); cleanup.Add(go); return go;}
        [TearDown] public void Cleanup() {for(int i=cleanup.Count-1;i>=0;i--) if(cleanup[i]!=null) Object.DestroyImmediate(cleanup[i]); cleanup.Clear();}

        [TestCase(1f)] [TestCase(0f)] [TestCase(.4f)]
        public void FuelConsumptionIsTenPercentOfPreviousAuthoredRate(float throttle)
        {
            float expected=(100f*.5f/60f)*(throttle>.05f?throttle:.02f)*5f*.1f;
            var method=TypeOf("CarControl").GetMethod("FuelRatePerSecond");
            Assert.That((float)method.Invoke(null,new object[]{2800f,throttle,.02f,5f}),Is.EqualTo(expected).Within(.00001f));
        }

        [Test] public void AuthoredSixtySecondDayNowLastsFourMinutes()
        {
            var go=Go("Clock"); go.SetActive(false);
            var clock=go.AddComponent(TypeOf("Voyage.Lighting.DayNightSystem"));
            Set(clock,"dayDuration",60f); Set(clock,"currentTime",12f);
            Call(clock,"AdvanceClock",60f);
            Assert.That(clock.GetType().GetField("currentTime").GetValue(clock),Is.EqualTo(18f));
            Call(clock,"AdvanceClock",180f);
            Assert.That(clock.GetType().GetField("currentTime").GetValue(clock),Is.EqualTo(12f));
        }

        [Test] public void ReferenceVehicleCanZoomToTwoHundredMetres()
        {
            var target=Go("RV"); target.SetActive(false);
            target.AddComponent(TypeOf("ReferenceVehicleRuntimeBinder"));
            var go=Go("Camera"); go.SetActive(false);
            var follow=go.AddComponent(TypeOf("FollowCamera"));
            Call(follow,"SetTarget",target.transform);
            Assert.That(follow.GetType().GetField("maxDistance").GetValue(follow),Is.EqualTo(200f));
        }

        [TestCase(false)] [TestCase(true)]
        public void CameraCannotOrbitThroughGroundOrCloseWall(bool wall)
        {
            var obstacle=GameObject.CreatePrimitive(PrimitiveType.Cube); cleanup.Add(obstacle);
            obstacle.transform.position=wall?new Vector3(0,2,-1):new Vector3(0,-.5f,0);
            obstacle.transform.localScale=wall?new Vector3(20,20,.2f):new Vector3(100,1,100);
            var target=Go("Target"); target.transform.position=new Vector3(0,2,0);
            var go=Go("Camera"); go.SetActive(false); var camera=go.AddComponent<Camera>();
            var follow=go.AddComponent(TypeOf("FollowCamera"));
            Set(follow,"target",target.transform); Set(follow,"cameraComponent",camera);
            Physics.SyncTransforms();
            Vector3 desired=wall?new Vector3(0,2,-10):new Vector3(0,-10,-10);
            var result=(Vector3)Call(follow,"ResolveCollision",target.transform.position,desired);
            if(wall) Assert.That(result.z,Is.GreaterThan(-.7f),"Do not enforce a minimum distance that pushes the camera through a close wall.");
            else Assert.That(result.y,Is.GreaterThan(.22f),"Ground below the vehicle is still a camera obstruction.");
        }

        [Test] public void CameraEnablesFullResolutionEdgeAntialiasing()
        {
            var go=Go("Camera"); go.SetActive(false); go.AddComponent<Camera>();
            var follow=go.AddComponent(TypeOf("FollowCamera")); Call(follow,"Awake");
            var data=go.GetComponent("UniversalAdditionalCameraData");
            Assert.That(data,Is.Not.Null);
            Assert.That(data.GetType().GetProperty("antialiasing").GetValue(data).ToString(),Is.EqualTo("SubpixelMorphologicalAntiAliasing"));
        }

        [UnityTest] public IEnumerator MultipleGrassTilesPrepareOnDifferentFramesBeforeRendering()
        {
            yield return new EnterPlayMode();
            var rendererType=Type.GetType("GrassFlow.GrassFlowRenderer, Assembly-CSharp-firstpass",true);
            var patch=Resources.Load("GrassFlow/Tiles/Grass_-1_-1");
            var first=Go("First grass tile"); first.SetActive(false);
            var second=Go("Second grass tile"); second.SetActive(false);
            var cameraGo=Go("Preload camera"); var camera=cameraGo.AddComponent<Camera>(); camera.enabled=false;
            var a=first.AddComponent(rendererType); var b=second.AddComponent(rendererType);
            Set(a,"patch",patch); Set(b,"patch",patch);
            bool preparedA=(bool)Call(a,"PrepareForCamera",camera);
            bool preparedBImmediately=(bool)Call(b,"PrepareForCamera",camera);
            yield return null;
            bool preparedBNextFrame=(bool)Call(b,"PrepareForCamera",camera);
            Call(a,"OnDisable"); Call(b,"OnDisable");
            Cleanup();
            yield return new ExitPlayMode();
            Assert.That(preparedA,Is.True);
            Assert.That(preparedBImmediately,Is.False,"Crossing into several tiles must not allocate all GPU buffers on one frame.");
            Assert.That(preparedBNextFrame,Is.True);
        }
    }
}
