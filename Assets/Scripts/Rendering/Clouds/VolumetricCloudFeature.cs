using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Voyage.Wind;
using Voyage.Environment;

namespace Voyage.Rendering.Clouds
{
    /// <summary>World-space, depth-aware volumetric clouds for the PC renderer.</summary>
    public sealed class VolumetricCloudFeature : ScriptableRendererFeature
    {
        [Serializable]
        public sealed class CloudSettings
        {
            public bool renderClouds = true;
            public Shader shader;
            [Header("Quality")]
            [Range(.25f, 1f)] public float resolutionScale = .6f;
            [Range(8, 96)] public int primarySteps = 64;
            [Range(1, 8)] public int lightSteps = 4;
            [Tooltip("Steps for the full-direction cloud reflection panorama used by water.")]
            [Range(8, 64)] public int reflectionSteps = 32;
            [Header("Cloud layer")]
            [Tooltip("World-space Y altitude where the cloud volume begins.")]
            [Min(0f)] public float bottomHeight = 900f;
            [Tooltip("World-space Y altitude where the cloud volume ends.")]
            [Min(1f)] public float topHeight = 1800f;
            [Min(.0001f)] public float noiseScale = .00125f;
            [Range(0f, 1f)] public float coverage = .52f;
            [Range(0f, 4f)] public float density = 1.15f;
            [Tooltip("Erodes wispy edges while keeping broad cloud banks connected.")]
            [Range(0f, 1f)] public float erosion = .38f;
            [Min(100f)] public float maxDistance = 120000f;
            [Tooltip("Optical extinction per world metre; independent of ray step count.")]
            [Min(.00001f)] public float extinction = .003f;
            [Header("Fallback wind (used without WindSystem)")]
            public Vector2 windDirection = new Vector2(.65f, .76f);
            [Min(0f)] public float windSpeed = 14f;
        }

        sealed class CloudPass : ScriptableRenderPass
        {
            static readonly int CloudTextureId = Shader.PropertyToID("_VoyageCloudTexture");
            static readonly int ReflectionTextureId = Shader.PropertyToID("_VoyageCloudReflectionTexture");
            readonly ProfilingSampler generationSampler = new ProfilingSampler("Voyage Volumetric Clouds");
            readonly ProfilingSampler compositeSampler = new ProfilingSampler("Voyage Cloud Composite");
            readonly ProfilingSampler reflectionSampler = new ProfilingSampler("Voyage Cloud Reflection Panorama");
            Material material;
            float resolutionScale;
            bool reportedMissingColorTarget;
            bool reportedRenderGraphRecording;
            Vector2 windOffset;
            double lastWindTime = double.NaN;
            bool renderReflection;

            sealed class PassData
            {
                public Material material;
                public TextureHandle source;
                public TextureHandle cloud;
            }

            public CloudPass()
            {
                // Composite after the skybox and opaque geometry. The cloud
                // volume depth test lets it sit in front of distant terrain
                // without leaking over closer objects.
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
                requiresIntermediateTexture = true;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public void Setup(Material cloudMaterial, CloudSettings settings, Camera camera)
            {
                material = cloudMaterial;
                resolutionScale = Mathf.Clamp(settings.resolutionScale, .25f, 1f);
                material.SetFloat("_PrimarySteps", settings.primarySteps);
                material.SetFloat("_LightSteps", settings.lightSteps);
                material.SetFloat("_ReflectionSteps", settings.reflectionSteps);
                SeaLevelWaterSystem water = SeaLevelWaterSystem.Instance;
                renderReflection = water != null && water.isActiveAndEnabled;
                Shader.SetGlobalFloat("_VoyageCloudReflectionEnabled", renderReflection ? 1f : 0f);
                if (renderReflection)
                {
                    Vector3 origin = camera.transform.position;
                    origin.y = 2f * water.seaLevel - origin.y;
                    material.SetVector("_CloudReflectionOrigin", new Vector4(origin.x, origin.y, origin.z, 1f));
                }
                material.SetVector("_CloudHeights",
                    new Vector4(settings.bottomHeight, Mathf.Max(settings.bottomHeight + 1f, settings.topHeight), 0f, 0f));
                material.SetFloat("_NoiseScale", Mathf.Max(.0001f, settings.noiseScale));
                material.SetFloat("_Coverage", settings.coverage);
                material.SetFloat("_Density", settings.density);
                material.SetFloat("_Erosion", settings.erosion);
                material.SetFloat("_MaxCloudDistance", Mathf.Max(100f, settings.maxDistance));
                material.SetFloat("_Extinction", Mathf.Max(.00001f, settings.extinction));
                Vector4 wind = WindSystem.Instance != null
                    ? WindSystem.Instance.GetCloudWind(settings.windDirection, settings.windSpeed)
                    : new Vector4(settings.windDirection.sqrMagnitude > .0001f ? settings.windDirection.normalized.x : 1f,
                        settings.windDirection.sqrMagnitude > .0001f ? settings.windDirection.normalized.y : 0f,
                        settings.windSpeed, 0f);
                // Integrate velocity once per time tick, rather than multiplying
                // elapsed time by gust speed (which makes the entire sky jump).
                double now = Time.timeAsDouble;
                if (!double.IsNaN(lastWindTime) && now >= lastWindTime)
                    windOffset += new Vector2(wind.x, wind.y) * wind.z * (float)(now - lastWindTime);
                lastWindTime = now;
                material.SetVector("_CloudOffset", new Vector4(windOffset.x, windOffset.y, 0f, 0f));
                Shader.SetGlobalVector("_VoyageCloudLayer", new Vector4(settings.bottomHeight,
                    Mathf.Max(settings.bottomHeight + 1f, settings.topHeight), settings.noiseScale, settings.coverage));
                Shader.SetGlobalVector("_VoyageCloudShape", new Vector4(settings.density, settings.erosion,
                    settings.maxDistance, 0f));
                Shader.SetGlobalVector("_VoyageCloudMotion", new Vector4(windOffset.x, windOffset.y, 0f, 0f));
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (material == null) return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer || !resources.cameraColor.IsValid())
                {
                    Shader.SetGlobalFloat("_VoyageCloudEnabled", 0f);
                    Shader.SetGlobalFloat("_VoyageCloudReflectionEnabled", 0f);
                    if (!reportedMissingColorTarget)
                    {
                        Debug.LogWarning("CLOUDS // pass was enqueued but RenderGraph has no sampleable camera color target; " +
                                         $"backbuffer={resources.isActiveTargetBackBuffer}, cameraColorValid={resources.cameraColor.IsValid()}.", material);
                        reportedMissingColorTarget = true;
                    }
                    return;
                }

                TextureHandle source = resources.cameraColor;
                if (renderReflection)
                {
                    // An upper-hemisphere panorama from the mirror camera position.
                    // Its basis never uses camera rotation, FOV, or screen bounds.
                    var reflectionDesc = new TextureDesc(512, 256)
                    {
                        name = "_VoyageCloudReflectionPanorama",
                        colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
                        depthBufferBits = DepthBits.None,
                        msaaSamples = MSAASamples.None,
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Repeat,
                        clearBuffer = true,
                        clearColor = Color.clear
                    };
                    TextureHandle reflection = renderGraph.CreateTexture(reflectionDesc);
                    using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<PassData>(
                               "Voyage Cloud Reflection Panorama", out PassData reflectionData, reflectionSampler))
                    {
                        reflectionData.material = material;
                        builder.SetRenderAttachment(reflection, 0, AccessFlags.Write);
                        builder.SetGlobalTextureAfterPass(reflection, ReflectionTextureId);
                        builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                        {
                            context.cmd.DrawProcedural(Matrix4x4.identity, data.material, 2, MeshTopology.Triangles, 3, 1);
                        });
                    }
                }
                TextureDesc cloudDesc = renderGraph.GetTextureDesc(source);
                cloudDesc.name = "_VoyageCloudLowResolution";
                // Camera HDR buffers may use B10G11R11, which has no alpha.
                // The cloud buffer stores premultiplied radiance AND opacity.
                cloudDesc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                cloudDesc.width = Mathf.Max(1, Mathf.RoundToInt(cloudDesc.width * resolutionScale));
                cloudDesc.height = Mathf.Max(1, Mathf.RoundToInt(cloudDesc.height * resolutionScale));
                cloudDesc.depthBufferBits = DepthBits.None;
                cloudDesc.msaaSamples = MSAASamples.None;
                cloudDesc.clearBuffer = true;
                cloudDesc.clearColor = Color.clear;
                cloudDesc.filterMode = FilterMode.Bilinear;
                material.SetVector("_CloudTexelSize", new Vector4(1f / cloudDesc.width, 1f / cloudDesc.height,
                    cloudDesc.width, cloudDesc.height));
                TextureHandle cloud = renderGraph.CreateTexture(cloudDesc);
                using (IRasterRenderGraphBuilder builder =
                       renderGraph.AddRasterRenderPass<PassData>("Voyage Cloud Raymarch", out PassData passData, generationSampler))
                {
                    passData.material = material;
                    passData.cloud = cloud;
                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(cloud, 0, AccessFlags.Write);
                    // Publish before the composite samples it, and keep it alive for water.
                    builder.SetGlobalTextureAfterPass(cloud, CloudTextureId);
                    builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                    {
                        context.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1);
                    });
                }

                TextureDesc destinationDesc = renderGraph.GetTextureDesc(source);
                destinationDesc.name = "_VoyageCloudComposite";
                destinationDesc.clearBuffer = false;
                TextureHandle destination = renderGraph.CreateTexture(destinationDesc);

                using (IRasterRenderGraphBuilder builder =
                       renderGraph.AddRasterRenderPass<PassData>("Voyage Cloud Composite", out PassData passData, compositeSampler))
                {
                    passData.material = material;
                    passData.source = source;
                    passData.cloud = cloud;
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.UseGlobalTexture(CloudTextureId, AccessFlags.Read);
                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                    builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                    {
                        Blitter.BlitTexture(context.cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, 1);
                    });
                }

                resources.cameraColor = destination;
                if (!reportedRenderGraphRecording)
                {
                    Debug.Log("CLOUDS // RenderGraph recorded the cloud raymarch and composite passes.", material);
                    reportedRenderGraphRecording = true;
                }
            }
        }

        public CloudSettings settings = new CloudSettings();
        CloudPass cloudPass;
        Material cloudMaterial;
        bool reportedActiveConfiguration;

        public override void Create()
        {
            RenderPipelineManager.beginCameraRendering -= ResetCloudAvailability;
            RenderPipelineManager.beginCameraRendering += ResetCloudAvailability;
            DisposeMaterial();
            Shader cloudShader = settings.shader != null ? settings.shader : Shader.Find("Hidden/Voyage/VolumetricClouds");
            reportedActiveConfiguration = false;
            if (cloudShader == null)
            {
                Debug.LogError("CLOUDS // renderer feature could not find Hidden/Voyage/VolumetricClouds.", this);
            }
            else if (!cloudShader.isSupported)
            {
                Debug.LogError($"CLOUDS // shader '{cloudShader.name}' is unsupported on {SystemInfo.graphicsDeviceType}.", this);
            }
            else
            {
                cloudMaterial = CoreUtils.CreateEngineMaterial(cloudShader);
                if (cloudMaterial == null)
                    Debug.LogError($"CLOUDS // could not create a material from '{cloudShader.name}'.", this);
            }
            cloudPass = new CloudPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraData cameraData = renderingData.cameraData;
            if (cameraData.renderType == CameraRenderType.Overlay) return;
            bool active = settings.renderClouds && cloudMaterial != null &&
                (cameraData.cameraType == CameraType.Game || cameraData.cameraType == CameraType.SceneView);
            Shader.SetGlobalFloat("_VoyageCloudEnabled", active ? 1f : 0f);
            if (!active)
                return;

            cloudPass.Setup(cloudMaterial, settings, cameraData.camera);
            if (!reportedActiveConfiguration)
            {
                Debug.Log($"CLOUDS // pass active; shader={cloudMaterial.shader.name}, " +
                          $"height={settings.bottomHeight:0}-{settings.topHeight:0}, " +
                          $"coverage={settings.coverage:0.00}, density={settings.density:0.00}, " +
                          $"noise={settings.noiseScale:0.00000}, steps={settings.primarySteps}/{settings.lightSteps}.", this);
                reportedActiveConfiguration = true;
            }
            renderer.EnqueuePass(cloudPass);
        }

        protected override void Dispose(bool disposing)
        {
            RenderPipelineManager.beginCameraRendering -= ResetCloudAvailability;
            DisposeMaterial();
            base.Dispose(disposing);
        }

        static void ResetCloudAvailability(ScriptableRenderContext context, Camera camera)
        {
            // An inactive renderer feature does not receive AddRenderPasses.
            // Water must not reuse a cloud texture left over from another camera/frame.
            Shader.SetGlobalFloat("_VoyageCloudEnabled", 0f);
            Shader.SetGlobalFloat("_VoyageCloudReflectionEnabled", 0f);
        }

        void DisposeMaterial()
        {
            Shader.SetGlobalFloat("_VoyageCloudEnabled", 0f);
            Shader.SetGlobalFloat("_VoyageCloudReflectionEnabled", 0f);
            if (cloudMaterial != null) CoreUtils.Destroy(cloudMaterial);
            cloudMaterial = null;
        }
    }
}
