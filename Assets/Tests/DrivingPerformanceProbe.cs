using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Voyage.TerrainSystem;

namespace Voyage.Tests
{
    // Explicit opt-in physical driving capture for Editor and development players.
    [DefaultExecutionOrder(2000)]
    public sealed class DrivingPerformanceProbe : MonoBehaviour
    {
        static string label;
        static readonly string[] Names = { "Main Thread", "GC.Alloc", "GC.Collect", "Physics.Simulate",
            "Voyage.GrassTracks.Save", "Voyage.GrassTracks.Serialize", "Voyage.GrassTracks.Write",
            "Voyage.Terrain.LoadPrefabAsset", "Voyage.Terrain.Awake", "Voyage.Terrain.ActivateCollision",
            "Voyage.Grass.Prepare", "Voyage.Grass.ContactUpdate", "UpdatePreloading",
            "GfxDeviceD3D12.WaitForLastPresentation.WaitForGPU", "Gfx.WaitForPresentOnGfxThread" };
        readonly List<ProfilerRecorder> recorders = new List<ProfilerRecorder>();
        readonly List<double[]> samples = new List<double[]>(24000);
        Keyboard keyboard;
        CarControl car;
        Rigidbody body;
        GrassPermanentTrackStore tracks;
        Camera camera;
        RenderTexture target;
        readonly UniversalRenderPipeline.SingleCameraRequest renderRequest = new UniversalRenderPipeline.SingleCameraRequest();
        bool previousCameraEnabled;
        string directory;
        float start;
        bool capturing, throttle;
        InputSettings.BackgroundBehavior previousBackgroundBehavior;
        bool changedInputSettings;
#if UNITY_EDITOR
        InputSettings.EditorInputBehaviorInPlayMode previousEditorInputBehavior;
#endif
        static readonly System.Reflection.FieldInfo InputFocus = typeof(CarControl).GetField("hasInputFocus",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Application.isEditor)
            {
                const string request = "Library/VoyageDriveProfile.txt";
                if (!File.Exists(request)) return;
                label = File.ReadAllText(request).Trim(); File.Delete(request);
            }
            else
            {
                var args = System.Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-voyage-profile");
                if (i < 0) return;
                label = i + 1 < args.Length ? args[i + 1] : "player";
            }
            Application.runInBackground = true;
            AudioListener.volume = 0f;
            new GameObject("Physical driving capture").AddComponent<DrivingPerformanceProbe>();
        }
        IEnumerator Start()
        {
            directory = Path.GetFullPath(Application.isEditor ? "Logs/DrivingHitches2" : Path.Combine(Application.dataPath, "..", "Profile"));
            Directory.CreateDirectory(directory);
            foreach (var store in FindObjectsByType<GrassPermanentTrackStore>())
                store.fileName = Path.Combine(directory, "test-tracks-" + label + ".json");
            float deadline = Time.realtimeSinceStartup + 120;
            while ((DrivingCore.Instance == null || DrivingCore.Instance.Player == null) && Time.realtimeSinceStartup < deadline) yield return null;
            if (DrivingCore.Instance == null || DrivingCore.Instance.Player == null) { File.WriteAllText(Path.Combine(directory,"error.txt"), "Vehicle startup timed out"); Stop(); yield break; }
            car = DrivingCore.Instance.Player.GetComponent<CarControl>(); body = car.GetComponent<Rigidbody>();
            Voyage.Exploration.ExplorationSession.Instance?.EnterVehicle();
            car.AddFuel(100f);
            tracks = FindAnyObjectByType<GrassPermanentTrackStore>();
            if (tracks != null)
            {
                tracks.fileName = Path.Combine(directory, "test-tracks-" + label + ".json");
                // Exercise a long drive's full history without changing the player's save file.
                for (int i = tracks.Samples.Count; i < tracks.maxSamples; i++)
                    tracks.RecordSegment(new Vector3(100000+i,0,100000),new Vector3(100000+i,0,100000),.3f,1,null);
            }
            camera = Camera.main;
            // Hidden/minimized players may skip automatic rendering entirely.
            // Explicit URP requests keep the actual scene/shader workload active.
            target = label.Contains("highres") ? new RenderTexture(2880,1508,24) : new RenderTexture(1920,1080,24);
            target.Create();
            previousCameraEnabled = camera.enabled; camera.enabled = false;
            renderRequest.destination = target;
            var follow = camera.GetComponent<FollowCamera>();
            if (follow != null && !label.Contains("default"))
            {
                follow.defaultPitch = 15;
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                typeof(FollowCamera).GetField("currentPitch",flags).SetValue(follow,15f);
                typeof(FollowCamera).GetField("desiredPitch",flags).SetValue(follow,15f);
            }
            var startup = car.GetComponent<StartProcedure>(); if (startup != null) startup.ForceStartVehicle();
            car.SetEngineOn(true); car.SetElectricalPower(true); car.SetGear(CarControl.GearMode.Drive);
            keyboard = InputSystem.AddDevice<Keyboard>("VoyageProfileKeyboard");
            previousBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
            previousEditorInputBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
            changedInputSettings = true;
            InputSystem.onBeforeUpdate += FeedInput;
            yield return new WaitForSecondsRealtime(12);
            var handles = new List<ProfilerRecorderHandle>(); ProfilerRecorderHandle.GetAvailable(handles);
            foreach (string name in Names)
            {
                var category = ProfilerCategory.Scripts;
                foreach (var h in handles) { var d=ProfilerRecorderHandle.GetDescription(h); if(d.Name==name) {category=d.Category;break;} }
                recorders.Add(ProfilerRecorder.StartNew(category,name,1));
            }
            // Recorder discovery allocates; do not count its setup frame as a game hitch.
            yield return null; yield return null;
            start = Time.realtimeSinceStartup; capturing = true;
        }
        void FeedInput()
        {
            // The hidden opt-in player must exercise the normal controls even
            // while the user's foreground application keeps OS input focus.
            if (car != null) InputFocus?.SetValue(car, true);
            if (keyboard != null) InputSystem.QueueStateEvent(keyboard, throttle ? new KeyboardState(Key.W) : new KeyboardState());
        }
        void LateUpdate()
        {
            if (target != null && camera != null) RenderPipeline.SubmitRenderRequest(camera, renderRequest);
            if (!capturing) return;
            float elapsed = Time.realtimeSinceStartup-start;
            throttle = elapsed >= 8 && elapsed < 53;
            var row = new double[9+Names.Length];
            row[0]=elapsed; row[1]=Time.unscaledDeltaTime*1000; row[2]=body.linearVelocity.magnitude*3.6;
            row[3]=body.position.x;row[4]=body.position.y;row[5]=body.position.z;
            row[6]=tracks != null ? tracks.Samples.Count : 0; row[7]=Time.frameCount;row[8]=throttle?1:0;
            for(int i=0;i<recorders.Count;i++)row[9+i]=recorders[i].Valid?recorders[i].LastValue:-1;
            samples.Add(row);
            if(elapsed>=63)
            {
                capturing=false; throttle=false;
                var text=new StringBuilder("seconds,frameMs,speedKmh,x,y,z,tracks,frame,throttle,"+string.Join(",",Names)+"\n");
                foreach(var r in samples) { for(int i=0;i<r.Length;i++){if(i>0)text.Append(',');text.Append(r[i].ToString("R",CultureInfo.InvariantCulture));}text.AppendLine(); }
                File.WriteAllText(Path.Combine(directory,label+".csv"),text.ToString());
                File.WriteAllText(Path.Combine(directory,label+"-environment.txt"),SystemInfo.graphicsDeviceName+"\n"+target.width+"x"+target.height+"\nExplicit URP render requests, actual WheelCollider physics, virtual W, full track history. Editor="+Application.isEditor
                    +"\nInterpolation="+body.interpolation+"; fixedDelta="+Time.fixedDeltaTime+"; timeScale="+Time.timeScale
                    +"; loadingPriority="+Application.backgroundLoadingPriority
                    +"; permanentTracks="+(GrassInteractionSystem.Instance!=null && GrassInteractionSystem.Instance.recordPermanentTracks));
                var active = RenderTexture.active; RenderTexture.active = target;
                var screenshot = new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
                screenshot.ReadPixels(new Rect(0,0,target.width,target.height),0,0); screenshot.Apply();
                File.WriteAllBytes(Path.Combine(directory,label+".png"),screenshot.EncodeToPNG());
                RenderTexture.active = active; Destroy(screenshot);
                Stop();
            }
        }
        void Stop()
        {
            Cleanup();
#if UNITY_EDITOR
            if(Application.isEditor){UnityEditor.EditorApplication.isPlaying=false;return;}
#endif
            Application.Quit();
        }
        void Cleanup()
        {
            InputSystem.onBeforeUpdate-=FeedInput;
            if(keyboard!=null){InputSystem.RemoveDevice(keyboard);keyboard=null;}
            if(changedInputSettings)
            {
                InputSystem.settings.backgroundBehavior=previousBackgroundBehavior;
#if UNITY_EDITOR
                InputSystem.settings.editorInputBehaviorInPlayMode=previousEditorInputBehavior;
#endif
                changedInputSettings=false;
            }
            foreach(var r in recorders)r.Dispose();recorders.Clear();
            if(target!=null){if(camera!=null)camera.enabled=previousCameraEnabled;target.Release();Destroy(target);target=null;}
        }
        void OnDestroy()=>Cleanup();
    }
}
