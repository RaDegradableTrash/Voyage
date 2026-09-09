using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Voyage.Rendering.Clouds
{
    /// <summary>Half-resolution, depth-aware volumetric clouds for the PC renderer.</summary>
    public sealed class VolumetricCloudFeature : ScriptableRendererFeature
    {
        [Serializable]
        public sealed class CloudSettings
        {
            public Shader shader;
            [Range(.25f, 1f)] public float resolutionScale = .5f;
            [Range(8, 64)] public int primarySteps = 18;
            [Range(1, 8)] public int lightSteps = 2;
            [Min(0f)] public float bottomHeight = 360f;
            [Min(1f)] public float topHeight = 900f;
            [Min(.0001f)] public float noiseScale = .0018f;
            [Range(0f, 1f)] public float coverage = .52f;
            [Range(0f, 4f)] public float density = 1.15f;
            public Vector2 windDirection = new Vector2(.8f, .3f);
            [Min(0f)] public float windSpeed = 10f;
        }

        sealed class CloudPass : ScriptableRenderPass
        {
            static readonly int CloudTextureId = Shader.PropertyToID("_VoyageCloudTexture");
            readonly ProfilingSampler generationSampler = new ProfilingSampler("Voyage Volumetric Clouds");
            readonly ProfilingSampler compositeSampler = new ProfilingSampler("Voyage Cloud Composite");
            Material material;
            float resolutionScale;

            sealed class PassData
            {
                public Material material;
                public TextureHandle source;
                public TextureHandle cloud;
            }

            public CloudPass()
            {
                // Composite against the complete camera color, including the
                // skybox. Running before the skybox can leave the opaque
                // source unresolved on some URP/DX12 paths, which appears as
                // a black vehicle and black terrain after the blit.
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
                requiresIntermediateTexture = true;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public void Setup(Material cloudMaterial, CloudSettings settings)
            {
                material = cloudMaterial;
                resolutionScale = Mathf.Clamp(settings.resolutionScale, .25f, 1f);
                material.SetFloat("_PrimarySteps", settings.primarySteps);
                material.SetFloat("_LightSteps", settings.lightSteps);
                material.SetVector("_CloudHeights",
                    new Vector4(settings.bottomHeight, Mathf.Max(settings.bottomHeight + 1f, settings.topHeight), 0f, 0f));
                material.SetFloat("_NoiseScale", Mathf.Max(.0001f, settings.noiseScale));
                material.SetFloat("_Coverage", settings.coverage);
                material.SetFloat("_Density", settings.density);
                Vector2 wind = settings.windDirection.sqrMagnitude > .0001f
                    ? settings.windDirection.normalized
                    : Vector2.right;
                material.SetVector("_CloudWind", new Vector4(wind.x, wind.y, settings.windSpeed, 0f));
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (material == null) return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer || !resources.cameraColor.IsValid()) return;

                TextureHandle source = resources.cameraColor;
                TextureDesc cloudDesc = renderGraph.GetTextureDesc(source);
                cloudDesc.name = "_VoyageCloudLowResolution";
                cloudDesc.width = Mathf.Max(1, Mathf.RoundToInt(cloudDesc.width * resolutionScale));
                cloudDesc.height = Mathf.Max(1, Mathf.RoundToInt(cloudDesc.height * resolutionScale));
                cloudDesc.depthBufferBits = DepthBits.None;
                cloudDesc.msaaSamples = MSAASamples.None;
                cloudDesc.clearBuffer = true;
                cloudDesc.clearColor = Color.clear;
                TextureHandle cloud = renderGraph.CreateTexture(cloudDesc);

                using (IRasterRenderGraphBuilder builder =
                       renderGraph.AddRasterRenderPass<PassData>("Voyage Cloud Raymarch", out PassData passData, generationSampler))
                {
                    passData.material = material;
                    passData.cloud = cloud;
                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(cloud, 0, AccessFlags.Write);
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
                    builder.UseTexture(cloud, AccessFlags.Read);
                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                    {
                        context.cmd.SetGlobalTexture(CloudTextureId, data.cloud);
                        Blitter.BlitTexture(context.cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, 1);
                    });
                }

                resources.cameraColor = destination;
            }
        }

        public CloudSettings settings = new CloudSettings();
        CloudPass cloudPass;
        Material cloudMaterial;

        public override void Create()
        {
            DisposeMaterial();
            Shader cloudShader = settings.shader != null ? settings.shader : Shader.Find("Hidden/Voyage/VolumetricClouds");
            if (cloudShader != null) cloudMaterial = CoreUtils.CreateEngineMaterial(cloudShader);
            cloudPass = new CloudPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraData cameraData = renderingData.cameraData;
            if (cloudMaterial == null || cameraData.cameraType != CameraType.Game ||
                cameraData.renderType == CameraRenderType.Overlay)
                return;

            cloudPass.Setup(cloudMaterial, settings);
            renderer.EnqueuePass(cloudPass);
        }

        protected override void Dispose(bool disposing)
        {
            DisposeMaterial();
            base.Dispose(disposing);
        }

        void DisposeMaterial()
        {
            if (cloudMaterial != null) CoreUtils.Destroy(cloudMaterial);
            cloudMaterial = null;
        }
    }
}
