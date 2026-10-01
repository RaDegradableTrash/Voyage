using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Collections;
using UnityEngine.TestTools;
using UnityEditor.TestTools;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using NUnit.Framework;
using UnityEngine;
using Unity.Profiling;
using Object=UnityEngine.Object;

namespace Voyage.Tests.Editor
{
    public class OpenIssueRegressionTests
    {
        const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        readonly List<Object> cleanup=new List<Object>();
        static Type TypeOf(string name)
        {
            var type = Type.GetType(name, false);
            if (type != null) return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(name, false);
                if (type != null) return type;
            }

            throw new TypeLoadException($"Could not find '{name}' in the loaded Unity assemblies.");
        }
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

        [Test] public void ReleasedSteeringRecentersAtLowSpeedAndDampsYawWithoutChangingRollOrPitch()
        {
            var go=Go("Steering release regression"); go.SetActive(false);
            var body=go.AddComponent<Rigidbody>();
            var car=go.AddComponent(TypeOf("CarControl"));
            Set(car,"rigidBody",body);
            Set(car,"currentSteerAngle",20f);
            Set(car,"currentDriftAmount",0f);

            Assert.That((float)Call(car,"GetSteeringReturnSpeed",0f),Is.GreaterThan(0f),
                "Releasing A/D at a crawl must still return the front wheels toward center.");

            body.angularVelocity=new Vector3(.5f,2f,.25f);
            Set(car,"steeringInputHeld",true);
            Call(car,"ApplySteeringReleaseYawDamping",true);
            Assert.That(body.angularVelocity.y,Is.EqualTo(2f).Within(.0001f),
                "Yaw correction must not fight active steering input.");
            Set(car,"steeringInputHeld",false);
            Call(car,"ApplySteeringReleaseYawDamping",true);
            Assert.That(body.angularVelocity.y,Is.LessThan(2f),
                "Residual chassis yaw should decay after steering is released.");
            Assert.That(body.angularVelocity.x,Is.EqualTo(.5f).Within(.0001f));
            Assert.That(body.angularVelocity.z,Is.EqualTo(.25f).Within(.0001f));
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

        [Test] public void VehicleCameraLagsOnTurnsAndThenSettlesToTheNewHeading()
        {
            var go=Go("Turn-follow camera"); go.SetActive(false);
            var follow=go.AddComponent(TypeOf("FollowCamera"));
            const float frameTime=1f/60f;
            Call(follow,"SmoothVehicleHeading",Vector3.forward,frameTime);

            Vector3 duringTurn=(Vector3)Call(follow,"SmoothVehicleHeading",Vector3.right,frameTime);
            float firstFrameTurn=Vector3.Angle(Vector3.forward,duringTurn);
            Assert.That(firstFrameTurn,Is.GreaterThan(1f),"The camera should begin rotating toward the turn immediately.");
            Assert.That(firstFrameTurn,Is.LessThan(15f),"The camera should visibly lag behind the vehicle during a turn.");

            for(int i=0;i<180;i++)
                duringTurn=(Vector3)Call(follow,"SmoothVehicleHeading",Vector3.right,frameTime);
            Assert.That(Vector3.Angle(Vector3.right,duringTurn),Is.LessThan(.1f),
                "The camera should settle to the new vehicle heading after the turn.");
        }

        [Test] public void VehicleUsesLowCenterOfMassOnGroundAndRestoresAuthoredCenterInAir()
        {
            var go=Go("Center of mass regression");
            var car=go.AddComponent(TypeOf("CarControl"));
            go.AddComponent<BoxCollider>();
            var body=go.GetComponent<Rigidbody>();
            Assert.That(body,Is.Not.Null);
            Vector3 authored=new Vector3(.12f,-1.1f,-.08f);
            Set(car,"rigidBody",body); Set(car,"authoredCenterOfMass",authored);
            Set(car,"centreOfGravityOffset",-2.5f); Set(car,"groundedCenterOfMassApplied",false);

            Call(car,"SetGroundedCenterOfMass",true);
            Assert.That(body.centerOfMass,Is.EqualTo(authored+Vector3.up*-2.5f));
            Vector3 groundedCenter = body.centerOfMass;
            Call(car,"SetGroundedCenterOfMass",false);
            Assert.That(body.centerOfMass,Is.EqualTo(authored),"Airborne rotation must use the authored vehicle center of mass.");
            Assert.That(body.centerOfMass,Is.Not.EqualTo(groundedCenter),"Leaving the ground must restore the actual rotation pivot.");
            Assert.That(body.inertiaTensor.sqrMagnitude,Is.GreaterThan(0f),"Each center-of-mass transition must leave a valid inertia tensor.");
        }

        [Test] public void GrassDrawRangeFollowsTheDrivingCameraZoom()
        {
            var patch = ScriptableObject.CreateInstance(TypeOf("GrassFlow.GrassFlowPatch"));
            var grass = Go("Grass range").AddComponent(TypeOf("GrassFlow.GrassFlowRenderer"));
            var cameraObject = Go("Zoom camera");
            cameraObject.SetActive(false);
            var camera = cameraObject.AddComponent<Camera>();
            var follow = cameraObject.AddComponent(TypeOf("FollowCamera"));
            var target = Go("Zoom target");
            cleanup.Add(patch);
            Set(grass,"patch",patch);
            Set(patch,"drawDistance",120f);
            Set(follow,"target",target.transform);
            Set(follow,"maxDistance",200f);
            camera.transform.position = target.transform.position + Vector3.back * 200f;

            float withFollow = (float)Call(grass,"DrawDistance",0f,camera);
            Assert.That(withFollow,Is.EqualTo(240f).Within(.01f),
                "Grass should cover the ground around a driver viewed at maximum zoom.");

            Object.DestroyImmediate(follow);
            float withoutFollow = (float)Call(grass,"DrawDistance",0f,camera);
            Assert.That(withoutFollow,Is.EqualTo(120f).Within(.01f),
                "The longer range should only apply to the vehicle follow camera.");
        }

        [Test] public void MountainFogKeepsTheFarBackdropVisibleWithoutStartingBesideTheVehicle()
        {
            var go=Go("Fog distance regression");
            go.SetActive(false);
            var fog=go.AddComponent(TypeOf("Voyage.Lighting.FogSystem"));
            Call(fog,"OnEnable");
            Call(fog,"Apply");
            try
            {
                float start = (float)fog.GetType().GetProperty("CurrentStartDistance").GetValue(fog);
                float end = (float)fog.GetType().GetProperty("CurrentEndDistance").GetValue(fog);
                Assert.That(start, Is.InRange(900f, 3000f), "Fog should stay out of the immediate driving view.");
                Assert.That(end, Is.InRange(6500f, 10000f), "Fog should still blend distant terrain into the horizon.");
            }
            finally { Call(fog,"OnDisable"); }
        }

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void CameraCannotOrbitThroughGroundOrCloseWall(bool wall, bool afterFastOrbitChange)
        {
            var obstacle=GameObject.CreatePrimitive(PrimitiveType.Cube); cleanup.Add(obstacle);
            obstacle.transform.position=wall?new Vector3(0,2,-1):new Vector3(0,-.5f,0);
            obstacle.transform.localScale=wall?new Vector3(20,20,.2f):new Vector3(100,1,100);
            var target=Go("Target"); target.transform.position=new Vector3(0,2,0);
            var go=Go("Camera"); go.SetActive(false); var camera=go.AddComponent<Camera>();
            var follow=go.AddComponent(TypeOf("FollowCamera"));
            Set(follow,"target",target.transform); Set(follow,"cameraComponent",camera);
            if(afterFastOrbitChange) Set(follow,"currentCollisionDistance",10f);
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

        [UnityTest] public IEnumerator SampleSceneCreatesTheVehicleAfterStreamingStartup()
        {
            var previousScene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            yield return new EnterPlayMode();
            bool vehicleStarted = false;
            Type coreType = TypeOf("DrivingCore");
            MonoBehaviour core = null;
            var startupFrameTimesMs = new List<float>(240);
            long startupPreparePeak = 0, startupMeshPeak = 0, startupBinningPeak = 0, startupGeneratePeak = 0;
            long startupTerrainInstantiatePeak = 0, startupTerrainUnloadPeak = 0, startupCollisionPeak = 0;
            long startupStreamPeak = 0, startupLodPeak = 0, startupAssetPeak = 0, startupAwakePeak = 0, startupGrassInitPeak = 0;
            using (var startupPrepare = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.Prepare", 240))
            using (var startupMesh = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.MeshSurface.Build", 240))
            using (var startupBinning = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.MeshSurface.Bin", 240))
            using (var startupGenerate = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.Generate", 240))
            using (var startupInstantiate = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.Instantiate", 240))
            using (var startupUnload = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.Unload", 240))
            using (var startupCollision = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.ActivateCollision", 240))
            using (var startupStream = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.StreamUpdate", 240))
            using (var startupLod = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.LodCollisionScan", 240))
            using (var startupAsset = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.LoadPrefabAsset", 240))
            using (var startupAwake = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.Awake", 240))
            using (var startupGrassInit = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.InitializeGrass", 240))
            {
                float deadline = Time.realtimeSinceStartup + 30f;
                while (Time.realtimeSinceStartup < deadline && !vehicleStarted)
                {
                    core = Object.FindAnyObjectByType(coreType) as MonoBehaviour;
                    vehicleStarted = core != null && coreType.GetProperty("Player")?.GetValue(core) != null;
                    startupFrameTimesMs.Add(Time.unscaledDeltaTime * 1000f);
                    Accumulate(startupPrepare, ref startupPreparePeak);
                    Accumulate(startupMesh, ref startupMeshPeak);
                    Accumulate(startupBinning, ref startupBinningPeak);
                    Accumulate(startupGenerate, ref startupGeneratePeak);
                    Accumulate(startupInstantiate, ref startupTerrainInstantiatePeak);
                    Accumulate(startupUnload, ref startupTerrainUnloadPeak);
                    Accumulate(startupCollision, ref startupCollisionPeak);
                    Accumulate(startupStream, ref startupStreamPeak);
                    Accumulate(startupLod, ref startupLodPeak);
                    Accumulate(startupAsset, ref startupAssetPeak);
                    Accumulate(startupAwake, ref startupAwakePeak);
                    Accumulate(startupGrassInit, ref startupGrassInitPeak);
                    if (!vehicleStarted) yield return null;
                }
                if (vehicleStarted)
                {
                    // Measure the period when streamed tiles and grass patches
                    // first become visible; this is where startup hitches occur.
                    float warmupUntil = Time.realtimeSinceStartup + 3f;
                    while (Time.realtimeSinceStartup < warmupUntil)
                    {
                        startupFrameTimesMs.Add(Time.unscaledDeltaTime * 1000f);
                        Accumulate(startupPrepare, ref startupPreparePeak);
                        Accumulate(startupMesh, ref startupMeshPeak);
                        Accumulate(startupBinning, ref startupBinningPeak);
                        Accumulate(startupGenerate, ref startupGeneratePeak);
                        Accumulate(startupInstantiate, ref startupTerrainInstantiatePeak);
                        Accumulate(startupUnload, ref startupTerrainUnloadPeak);
                        Accumulate(startupCollision, ref startupCollisionPeak);
                        Accumulate(startupStream, ref startupStreamPeak);
                        Accumulate(startupLod, ref startupLodPeak);
                        Accumulate(startupAsset, ref startupAssetPeak);
                        Accumulate(startupAwake, ref startupAwakePeak);
                        Accumulate(startupGrassInit, ref startupGrassInitPeak);
                        yield return null;
                    }
                }
            }
            if (startupFrameTimesMs.Count > 0)
            {
                startupFrameTimesMs.Sort();
                int startupP95 = Mathf.Clamp(Mathf.CeilToInt(startupFrameTimesMs.Count * .95f) - 1, 0, startupFrameTimesMs.Count - 1);
                Debug.Log($"VOYAGE_STARTUP_PROFILE frames={startupFrameTimesMs.Count} medianMs={startupFrameTimesMs[startupFrameTimesMs.Count / 2]:F2} p95Ms={startupFrameTimesMs[startupP95]:F2} maxMs={startupFrameTimesMs[startupFrameTimesMs.Count - 1]:F2} grassPreparePeakMs={ToMs(startupPreparePeak):F3} grassMeshSurfacePeakMs={ToMs(startupMeshPeak):F3} grassSurfaceBinningPeakMs={ToMs(startupBinningPeak):F3} grassGeneratePeakMs={ToMs(startupGeneratePeak):F3} terrainInstantiatePeakMs={ToMs(startupTerrainInstantiatePeak):F3} terrainUnloadPeakMs={ToMs(startupTerrainUnloadPeak):F3} collisionPeakMs={ToMs(startupCollisionPeak):F3} streamUpdatePeakMs={ToMs(startupStreamPeak):F3} lodScanPeakMs={ToMs(startupLodPeak):F3} prefabLoadPeakMs={ToMs(startupAssetPeak):F3} tileAwakePeakMs={ToMs(startupAwakePeak):F3} grassInitPeakMs={ToMs(startupGrassInitPeak):F3}");
            }
            if (vehicleStarted)
            {
                var frameTimesMs = new List<float>(180);
                using (var prepare = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.Prepare", 180))
                using (var meshSurface = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.MeshSurface.Build", 180))
                using (var generate = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.Generate", 180))
                using (var surfaceBinning = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.MeshSurface.Bin", 180))
                using (var cull = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.Cull", 180))
                using (var tileBuild = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.Instantiate", 180))
                using (var grassContact = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.ContactUpdate", 180))
                using (var initializeGrass = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.InitializeGrass", 180))
                {
                    long prepareTotal = 0, cullTotal = 0, tileBuildTotal = 0, contactTotal = 0, initializeGrassTotal = 0;
                    long meshSurfacePeak = 0, generatePeak = 0, surfaceBinningPeak = 0;
                    long preparePeak = 0, cullPeak = 0, tileBuildPeak = 0, contactPeak = 0, initializeGrassPeak = 0;
                    int prepareSamples = 0, cullSamples = 0, tileBuildSamples = 0, contactSamples = 0, initializeGrassSamples = 0;
                    for (int i = 0; i < 180; i++)
                    {
                        frameTimesMs.Add(Time.unscaledDeltaTime * 1000f);
                        Accumulate(prepare, ref prepareTotal, ref preparePeak, ref prepareSamples);
                        Accumulate(meshSurface, ref meshSurfacePeak);
                        Accumulate(generate, ref generatePeak);
                        Accumulate(surfaceBinning, ref surfaceBinningPeak);
                        Accumulate(cull, ref cullTotal, ref cullPeak, ref cullSamples);
                        Accumulate(tileBuild, ref tileBuildTotal, ref tileBuildPeak, ref tileBuildSamples);
                        Accumulate(grassContact, ref contactTotal, ref contactPeak, ref contactSamples);
                        Accumulate(initializeGrass, ref initializeGrassTotal, ref initializeGrassPeak, ref initializeGrassSamples);
                        yield return null;
                    }
                    frameTimesMs.Sort();
                    int p95 = Mathf.Clamp(Mathf.CeilToInt(frameTimesMs.Count * .95f) - 1, 0, frameTimesMs.Count - 1);
                    var loaded = (IDictionary)coreType.GetField("loadedTerrainTiles", Flags).GetValue(core);
                    Type grassRendererType = TypeOf("GrassFlow.GrassFlowRenderer");
                    int grassFlowTiles = Object.FindObjectsByType(grassRendererType,
                        FindObjectsInactive.Exclude).Length;
                    Type interactiveGrassType = TypeOf("Voyage.TerrainSystem.InteractiveGrassTile");
                    var interactiveGrassTiles = Object.FindObjectsByType(interactiveGrassType, FindObjectsInactive.Include);
                    int interactiveBuilt = 0;
                    var buildFinished = interactiveGrassType.GetProperty("BuildFinished");
                    foreach (Object grass in interactiveGrassTiles)
                        if ((bool)buildFinished.GetValue(grass)) interactiveBuilt++;
                    Debug.Log($"VOYAGE_RUNTIME_PROFILE frames={frameTimesMs.Count} medianMs={frameTimesMs[frameTimesMs.Count / 2]:F2} p95Ms={frameTimesMs[p95]:F2} maxMs={frameTimesMs[frameTimesMs.Count - 1]:F2} terrainTiles={loaded.Count} grassFlowTiles={grassFlowTiles} interactiveGrassTiles={interactiveGrassTiles.Length} interactiveGrassBuilt={interactiveBuilt} grassPrepareAvgMs={AverageMs(prepareTotal, prepareSamples):F3} grassPreparePeakMs={ToMs(preparePeak):F3} grassMeshSurfacePeakMs={ToMs(meshSurfacePeak):F3} grassSurfaceBinningPeakMs={ToMs(surfaceBinningPeak):F3} grassGeneratePeakMs={ToMs(generatePeak):F3} grassCullAvgMs={AverageMs(cullTotal, cullSamples):F3} grassCullPeakMs={ToMs(cullPeak):F3} grassContactAvgMs={AverageMs(contactTotal, contactSamples):F3} grassContactPeakMs={ToMs(contactPeak):F3} grassInitAvgMs={AverageMs(initializeGrassTotal, initializeGrassSamples):F3} grassInitPeakMs={ToMs(initializeGrassPeak):F3} terrainInstantiateAvgMs={AverageMs(tileBuildTotal, tileBuildSamples):F3} terrainInstantiatePeakMs={ToMs(tileBuildPeak):F3}");

                }
            }
            yield return new ExitPlayMode();
            if (previousScene.IsValid() && !string.IsNullOrEmpty(previousScene.path))
                EditorSceneManager.OpenScene(previousScene.path, OpenSceneMode.Single);
            else if (Application.isBatchMode)
                EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            else
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(vehicleStarted, Is.True, "The Play Mode terrain/vehicle startup should not wait indefinitely.");
        }

        [UnityTest] public IEnumerator SampleSceneKeepsGrassInRangeAtAHighDrivingCameraZoom()
        {
            var previousScene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            yield return new EnterPlayMode();

            Type coreType = TypeOf("DrivingCore");
            Type grassType = TypeOf("GrassFlow.GrassFlowRenderer");
            Component core = null;
            Component vehicle = null;
            float startupDeadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < startupDeadline && vehicle == null)
            {
                core = Object.FindAnyObjectByType(coreType) as Component;
                vehicle = core == null ? null : coreType.GetProperty("Player")?.GetValue(core) as Component;
                if (vehicle == null) yield return null;
            }
            bool vehicleStarted = vehicle != null;

            float runtimeGrassRange = 0f;
            int visibleGrassInstances = 0;
            int rendererCount = 0;
            int readyCount = 0;
            if (vehicle != null)
            {
                var carControl = vehicle.GetComponent(TypeOf("CarControl")) as Behaviour;
                if (carControl != null) carControl.enabled = false;
                var body = vehicle.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.isKinematic = true;
                }

                Camera camera = Camera.main;
                var followType = TypeOf("FollowCamera");
                var follow = camera != null ? camera.GetComponent(followType) as Behaviour : null;
                if (camera != null)
                {
                    if (follow != null)
                    {
                        Call(follow, "SetTarget", vehicle.transform);
                        float authoredDistance = (float)followType.GetField("distance", Flags).GetValue(follow);
                        float maximumDistance = (float)followType.GetField("maxDistance", Flags).GetValue(follow);
                        float zoom = maximumDistance / Mathf.Max(.01f, authoredDistance);
                        Set(follow, "zoom", zoom);
                        Set(follow, "zoomTarget", zoom);
                        float pitch = (float)followType.GetField("defaultPitch", Flags).GetValue(follow);
                        Set(follow, "currentPitch", pitch);
                        Set(follow, "desiredPitch", pitch);
                        Set(follow, "currentYaw", 0f);
                        Set(follow, "desiredYaw", 0f);
                    }
                    camera.farClipPlane = 3000f;
                    camera.aspect = 16f / 9f;

                    float prepareDeadline = Time.realtimeSinceStartup + 12f;
                    float stableSince = -1f;
                    object[] renderers = Object.FindObjectsByType(grassType, FindObjectsInactive.Exclude);
                    while (Time.realtimeSinceStartup < prepareDeadline)
                    {
                        renderers = Object.FindObjectsByType(grassType, FindObjectsInactive.Exclude);
                        readyCount = 0;
                        foreach (object renderer in renderers)
                            if ((bool)grassType.GetProperty("Ready").GetValue(renderer)) readyCount++;
                        // Wait for nearby streamed grass to finish preparation.
                        if (readyCount > 0)
                        {
                            if (stableSince < 0f) stableSince = Time.realtimeSinceStartup;
                            if (Time.realtimeSinceStartup - stableSince >= 1f) break;
                        }
                        else stableSince = -1f;
                        yield return null;
                    }
                    rendererCount = renderers.Length;
                    foreach (object renderer in renderers)
                        runtimeGrassRange = Mathf.Max(runtimeGrassRange, (float)Call(renderer, "DrawDistance", 0f, camera));
                    float cameraSettleUntil = Time.realtimeSinceStartup + 1f;
                    while (Time.realtimeSinceStartup < cameraSettleUntil) yield return null;

                    Vector3 focusPoint = vehicle.transform.position + Vector3.up * 1.5f;
                    Vector3 focusViewport = camera.WorldToViewportPoint(focusPoint);
                    RaycastHit groundHit;
                    bool hasGround = Physics.Raycast(vehicle.transform.position + Vector3.up * 1000f,
                        Vector3.down, out groundHit, 2000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    int terrainTileCount = Object.FindObjectsByType(TypeOf("Voyage.TerrainSystem.TerrainTileRuntime"),
                        FindObjectsInactive.Exclude).Length;
                    string patchInfo = "none";
                    if (renderers.Length > 0)
                    {
                        var patchField = grassType.GetField("patch", Flags);
                        var patch = patchField == null ? null : patchField.GetValue(renderers[0]);
                        if (patch != null)
                        {
                            Bounds patchBounds = (Bounds)patch.GetType().GetField("bounds").GetValue(patch);
                            object drawDistance = patch.GetType().GetField("drawDistance").GetValue(patch);
                            patchInfo = $"bounds={patchBounds} drawDistance={drawDistance}";
                        }
                    }
                    Debug.Log($"VOYAGE_OVERHEAD_CAMERA camera={camera.name} position={camera.transform.position} forward={camera.transform.forward} focus={focusViewport} cullingMask={camera.cullingMask} pitch={(follow != null ? followType.GetField("defaultPitch", Flags).GetValue(follow) : "none")} zoom={(follow != null ? followType.GetField("maxDistance", Flags).GetValue(follow) : "none")} runtimeGrassRange={runtimeGrassRange} terrainTiles={terrainTileCount} groundHit={hasGround}:{(hasGround ? groundHit.point.ToString() : "none")} grassLayer={LayerMask.NameToLayer("VoyageGrass")} grassObjectLayer={(renderers.Length > 0 ? ((Component)renderers[0]).gameObject.layer : -1)} {patchInfo}");

                    var oldTargetTexture = camera.targetTexture;
                    var renderTarget = new RenderTexture(64, 64, 16);
                    renderTarget.Create();
                    camera.targetTexture = renderTarget;
                    try
                    {
                        yield return new WaitForEndOfFrame();
                        int renderedGrassFrame = -1;
                        using (var countBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw))
                        {
                            foreach (object renderer in renderers)
                            {
                                var cameraBuffersByCamera = grassType.GetField("cameras", Flags).GetValue(renderer) as IDictionary;
                                if (cameraBuffersByCamera == null || !cameraBuffersByCamera.Contains(camera)) continue;
                                object cameraBuffers = cameraBuffersByCamera[camera];
                                renderedGrassFrame = Mathf.Max(renderedGrassFrame,
                                    (int)cameraBuffers.GetType().GetField("renderedFrame", Flags).GetValue(cameraBuffers));
                                var visibleBuffers = (ComputeBuffer[])cameraBuffers.GetType()
                                    .GetField("visible", Flags).GetValue(cameraBuffers);
                                foreach (ComputeBuffer visibleBuffer in visibleBuffers)
                                {
                                    ComputeBuffer.CopyCount(visibleBuffer, countBuffer, 0);
                                    var count = new uint[1];
                                    countBuffer.GetData(count);
                                    visibleGrassInstances += (int)count[0];
                                }
                            }
                        }
                        Debug.Log($"VOYAGE_HIGH_ZOOM_GRASS_GPU visibleInstances={visibleGrassInstances} renderedFrame={renderedGrassFrame} frame={Time.frameCount}");
                    }
                    finally
                    {
                        camera.targetTexture = oldTargetTexture;
                        renderTarget.Release();
                        Object.Destroy(renderTarget);
                    }
                }
            }

            yield return new ExitPlayMode();
            if (previousScene.IsValid() && !string.IsNullOrEmpty(previousScene.path))
                EditorSceneManager.OpenScene(previousScene.path, OpenSceneMode.Single);
            else if (Application.isBatchMode)
                EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            else
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Debug.Log($"VOYAGE_ACTUAL_OVERHEAD_GRASS renderers={rendererCount} ready={readyCount} range={runtimeGrassRange:0} visibleInstances={visibleGrassInstances} camera=FollowCameraMaxZoom");
            Assert.That(vehicleStarted, Is.True, "The runtime scene must spawn its vehicle before measuring actual-view grass.");
            Assert.That(rendererCount, Is.GreaterThan(0), "Streamed terrain must create actual GrassFlow renderers around the vehicle.");
            Assert.That(readyCount, Is.GreaterThan(0), "Grass buffers around the vehicle must finish building before high-zoom culling is measured.");
            Assert.That(runtimeGrassRange, Is.GreaterThanOrEqualTo(200f), "The meadow range must cover the maximum driving-camera zoom.");
            Assert.That(visibleGrassInstances, Is.GreaterThan(0), "The high-zoom game camera must keep visible grass instances after GPU culling.");
        }

        static float GrassContrastCoverage(Color32[] bare, Color32[] grass, int threshold)
        {
            int changed = 0;
            for (int i = 0; i < bare.Length; i += 2)
            {
                Color32 a = bare[i], b = grass[i];
                int difference = Mathf.Max(Mathf.Abs(a.r - b.r),
                    Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
                if (difference >= threshold) changed++;
            }
            return changed / (float)((bare.Length + 1) / 2);
        }

        [UnityTest] public IEnumerator VehicleTerrainStreamingProfilesContinuousTileCrossings()
        {
            var previousScene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            yield return new EnterPlayMode();

            bool vehicleStarted = false;
            int boundaryCrossings = 0;
            int routeTiles = 0;
            int loadedTiles = 0;
            int framesOver16Ms = 0;
            float movementSeconds = 0f;
            float medianFrameMs = 0f, p95FrameMs = 0f, maxFrameMs = 0f;
            float terrainAwakePeakMs = 0f, instantiatePeakMs = 0f, unloadPeakMs = 0f;
            float collisionPeakMs = 0f, grassInitPeakMs = 0f, grassPreparePeakMs = 0f;
            float assetLoadPeakMs = 0f, streamUpdatePeakMs = 0f, lodScanPeakMs = 0f, grassCullPeakMs = 0f;
            Type coreType = TypeOf("DrivingCore");
            MonoBehaviour core = null;
            float startupDeadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < startupDeadline && !vehicleStarted)
            {
                core = Object.FindAnyObjectByType(coreType) as MonoBehaviour;
                vehicleStarted = core != null && coreType.GetProperty("Player")?.GetValue(core) != null;
                if (!vehicleStarted) yield return null;
            }

            if (vehicleStarted)
            {
                // Warm the initial preload ring before measuring movement, then
                // move the kinematic vehicle across enough real tile boundaries
                // to force the streamer to load and retire terrain while active.
                float warmupUntil = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < warmupUntil) yield return null;

                Component player = coreType.GetProperty("Player").GetValue(core) as Component;
                Transform target = player.transform;
                var carControl = player.GetComponent(TypeOf("CarControl")) as Behaviour;
                if (carControl != null) carControl.enabled = false;
                Rigidbody body = player.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.isKinematic = true;
                    body.useGravity = false;
                }

                Type indexType = TypeOf("Voyage.TerrainSystem.TerrainTileIndex");
                Type settingsType = TypeOf("Voyage.TerrainSystem.TerrainChunkSettings");
                Object terrainIndex = Resources.Load("TerrainSystem/TerrainTileIndex", indexType);
                object settings = terrainIndex == null ? null : indexType.GetField("settings").GetValue(terrainIndex);
                IList records = terrainIndex == null ? null : indexType.GetField("tiles").GetValue(terrainIndex) as IList;
                if (settings != null && records != null)
                {
                    var coordinates = new HashSet<Vector2Int>();
                    foreach (object record in records)
                        if (record != null) coordinates.Add((Vector2Int)record.GetType().GetField("coordinate").GetValue(record));

                    Vector2Int startCoordinate = (Vector2Int)settingsType.GetMethod("WorldToTile").Invoke(settings, new object[] { target.position });
                    int preloadRadius = (int)settingsType.GetMethod("GetPreloadRadius").Invoke(settings, null);
                    float tileSize = (float)settingsType.GetField("tileSize").GetValue(settings);
                    Vector2Int[] directions = { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down };
                    Vector2Int travel = Vector2Int.zero;
                    int bestLength = 0;
                    int routeLimit = preloadRadius + 2;
                    for (int d = 0; d < directions.Length; d++)
                    {
                        int length = 0;
                        while (length < routeLimit && coordinates.Contains(startCoordinate + directions[d] * (length + 1))) length++;
                        if (length > bestLength) { bestLength = length; travel = directions[d]; }
                    }
                    routeTiles = Mathf.Min(bestLength, routeLimit);

                    if (routeTiles > 0)
                    {
                        var frameTimes = new List<float>(4096);
                        Vector3 direction = new Vector3(travel.x, 0f, travel.y);
                        float distanceLeft = tileSize * routeTiles;
                        const float testSpeed = 55.56f; // 200 km/h, enough to keep forward streaming under load.
                        float movementDeadline = Time.realtimeSinceStartup + Mathf.Max(35f, distanceLeft / testSpeed * 2.5f);
                        float movementStarted = Time.realtimeSinceStartup;
                        Vector2Int previousCoordinate = startCoordinate;
                        var loaded = coreType.GetField("loadedTerrainTiles", Flags).GetValue(core) as IDictionary;
                        using (var terrainAwake = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.Awake", 4096))
                        using (var instantiate = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.Instantiate", 4096))
                        using (var unload = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.Unload", 4096))
                        using (var collision = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.ActivateCollision", 4096))
                        using (var grassInit = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.InitializeGrass", 4096))
                        using (var grassPrepare = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.Prepare", 4096))
                        using (var assetLoad = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.LoadPrefabAsset", 4096))
                        using (var streamUpdate = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.StreamUpdate", 4096))
                        using (var lodScan = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Terrain.LodCollisionScan", 4096))
                        using (var grassCull = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Voyage.Grass.Cull", 4096))
                        {
                            long terrainAwakeMax = 0, instantiateMax = 0, unloadMax = 0;
                            long collisionMax = 0, grassInitMax = 0, grassPrepareMax = 0;
                            long assetLoadMax = 0, streamUpdateMax = 0, lodScanMax = 0, grassCullMax = 0;
                            while (distanceLeft > 0f && Time.realtimeSinceStartup < movementDeadline)
                            {
                                float delta = Time.unscaledDeltaTime;
                                if (delta <= 0f) { yield return null; continue; }
                                float step = Mathf.Min(distanceLeft, testSpeed * delta);
                                Vector3 position = target.position + direction * step;
                                target.position = position;
                                if (body != null) body.position = position;
                                distanceLeft -= step;

                                Vector2Int coordinate = (Vector2Int)settingsType.GetMethod("WorldToTile").Invoke(settings, new object[] { position });
                                if (coordinate != previousCoordinate)
                                {
                                    boundaryCrossings += Mathf.Abs(coordinate.x - previousCoordinate.x) + Mathf.Abs(coordinate.y - previousCoordinate.y);
                                    previousCoordinate = coordinate;
                                }
                                float frameMs = Time.unscaledDeltaTime * 1000f;
                                if (frameMs > 16.67f) framesOver16Ms++;
                                frameTimes.Add(frameMs);
                                Accumulate(terrainAwake, ref terrainAwakeMax);
                                Accumulate(instantiate, ref instantiateMax);
                                Accumulate(unload, ref unloadMax);
                                Accumulate(collision, ref collisionMax);
                                Accumulate(grassInit, ref grassInitMax);
                                Accumulate(grassPrepare, ref grassPrepareMax);
                                Accumulate(assetLoad, ref assetLoadMax);
                                Accumulate(streamUpdate, ref streamUpdateMax);
                                Accumulate(lodScan, ref lodScanMax);
                                Accumulate(grassCull, ref grassCullMax);
                                yield return null;
                            }

                            // Keep recording while the new forward tiles finish
                            // their asynchronous integration/collision work.
                            float settleUntil = Time.realtimeSinceStartup + 3f;
                            while (Time.realtimeSinceStartup < settleUntil)
                            {
                                frameTimes.Add(Time.unscaledDeltaTime * 1000f);
                                Accumulate(terrainAwake, ref terrainAwakeMax);
                                Accumulate(instantiate, ref instantiateMax);
                                Accumulate(unload, ref unloadMax);
                                Accumulate(collision, ref collisionMax);
                                Accumulate(grassInit, ref grassInitMax);
                                Accumulate(grassPrepare, ref grassPrepareMax);
                                Accumulate(assetLoad, ref assetLoadMax);
                                Accumulate(streamUpdate, ref streamUpdateMax);
                                Accumulate(lodScan, ref lodScanMax);
                                Accumulate(grassCull, ref grassCullMax);
                                yield return null;
                            }

                            terrainAwakePeakMs = ToMs(terrainAwakeMax);
                            instantiatePeakMs = ToMs(instantiateMax);
                            unloadPeakMs = ToMs(unloadMax);
                            collisionPeakMs = ToMs(collisionMax);
                            grassInitPeakMs = ToMs(grassInitMax);
                            grassPreparePeakMs = ToMs(grassPrepareMax);
                            assetLoadPeakMs = ToMs(assetLoadMax);
                            streamUpdatePeakMs = ToMs(streamUpdateMax);
                            lodScanPeakMs = ToMs(lodScanMax);
                            grassCullPeakMs = ToMs(grassCullMax);
                        }
                        movementSeconds = Time.realtimeSinceStartup - movementStarted;

                        if (frameTimes.Count > 0)
                        {
                            frameTimes.Sort();
                            int p95 = Mathf.Clamp(Mathf.CeilToInt(frameTimes.Count * .95f) - 1, 0, frameTimes.Count - 1);
                            medianFrameMs = frameTimes[frameTimes.Count / 2];
                            p95FrameMs = frameTimes[p95];
                            maxFrameMs = frameTimes[frameTimes.Count - 1];
                        }
                        loadedTiles = loaded == null ? 0 : loaded.Count;
                    }
                }
            }

            yield return new ExitPlayMode();
            if (previousScene.IsValid() && !string.IsNullOrEmpty(previousScene.path))
                EditorSceneManager.OpenScene(previousScene.path, OpenSceneMode.Single);
            else if (Application.isBatchMode)
                EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            else
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Debug.Log($"VOYAGE_STREAMING_PROFILE started={vehicleStarted} routeTiles={routeTiles} crossedBoundaries={boundaryCrossings} loadedTiles={loadedTiles} movementSeconds={movementSeconds:F1} framesOver16Ms={framesOver16Ms} medianMs={medianFrameMs:F2} p95Ms={p95FrameMs:F2} maxMs={maxFrameMs:F2} terrainAwakePeakMs={terrainAwakePeakMs:F3} prefabAssetLoadPeakMs={assetLoadPeakMs:F3} instantiatePeakMs={instantiatePeakMs:F3} unloadPeakMs={unloadPeakMs:F3} collisionPeakMs={collisionPeakMs:F3} streamUpdatePeakMs={streamUpdatePeakMs:F3} lodScanPeakMs={lodScanPeakMs:F3} grassInitPeakMs={grassInitPeakMs:F3} grassPreparePeakMs={grassPreparePeakMs:F3} grassCullPeakMs={grassCullPeakMs:F3}");
            Assert.That(vehicleStarted, Is.True, "The streaming profile must reach a live vehicle before measuring movement.");
            Assert.That(boundaryCrossings, Is.GreaterThan(0), "The test must cross streamed tile boundaries.");
        }

        static void Accumulate(ProfilerRecorder recorder, ref long total, ref long peak, ref int samples)
        {
            if (!recorder.Valid) return;
            long value = recorder.LastValue;
            if (value <= 0) return;
            total += value;
            peak = System.Math.Max(peak, value);
            samples++;
        }

        static void Accumulate(ProfilerRecorder recorder, ref long peak)
        {
            if (!recorder.Valid) return;
            peak = System.Math.Max(peak, recorder.LastValue);
        }

        static float AverageMs(long totalNanoseconds, int samples) =>
            samples > 0 ? totalNanoseconds / (samples * 1000000f) : 0f;

        static float ToMs(long nanoseconds) => nanoseconds / 1000000f;

    }
}
