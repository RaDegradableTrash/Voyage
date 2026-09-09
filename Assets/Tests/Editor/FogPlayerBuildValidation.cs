using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Voyage.Tests.Editor
{
    public static class FogPlayerBuildValidation
    {
        public static void Build()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Run this validation in an isolated batch-mode project.");
            // Deliberately save a scene with fog disabled: Voyage enables it at runtime.
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            RenderSettings.fog=false;
            var go=new GameObject("Player fog verification");
            var type=Type.GetType("Voyage.Tests.FogPlayerProbe, Assembly-CSharp",true);
            var probe=go.AddComponent(type);
            type.GetField("terrainShader").SetValue(probe,Shader.Find("Voyage/Terrain/Stylized"));
            type.GetField("litShader").SetValue(probe,Shader.Find("Universal Render Pipeline/Lit"));
            const string path="Assets/Tests/FogPlayerProbeScene.unity";
            EditorSceneManager.SaveScene(scene,path);
            var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes=new[]{path},locationPathName="Builds/FogValidation/FogValidation.exe",
                target=BuildTarget.StandaloneWindows64,options=BuildOptions.None });
            if(result.summary.result!=BuildResult.Succeeded) throw new Exception("Fog player build failed: "+result.summary.result);
            Debug.Log("FOG PLAYER BUILD // "+result.summary.result);
        }
    }
}
