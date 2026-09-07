using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
namespace Voyage.Rendering
{
    public sealed class DistantContourFeature : ScriptableRendererFeature
    {
        public Shader shader;
        Material material; ContourPass pass;
        sealed class ContourPass : ScriptableRenderPass
        {
            public Material material;
            sealed class Data { public Material material; public TextureHandle source; }
            sealed class DepthData { public RendererListHandle renderers; }
            public ContourPass() { renderPassEvent=RenderPassEvent.BeforeRenderingPostProcessing; requiresIntermediateTexture=true; }
            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                var resources=frame.Get<UniversalResourceData>();
                if(resources.isActiveTargetBackBuffer) return;
                var camera=frame.Get<UniversalCameraData>();
                var rendering=frame.Get<UniversalRenderingData>();
                var lights=frame.Get<UniversalLightData>();
                // A separate depth surface keeps foliage out of contour detection.
                // Merely raising a depth threshold still outlines whole grass canopies.
                var depth=graph.CreateTexture(new TextureDesc(camera.cameraTargetDescriptor.width,camera.cameraTargetDescriptor.height)
                { name="Terrain and object contour depth",depthBufferBits=DepthBits.Depth32,clearBuffer=true,
                    filterMode=FilterMode.Point,msaaSamples=MSAASamples.None });
                using(var builder=graph.AddRasterRenderPass<DepthData>("Voyage contour geometry",out var data))
                {
                    var drawing=RenderingUtils.CreateDrawingSettings(new ShaderTagId("DepthOnly"),rendering,camera,lights,camera.defaultOpaqueSortFlags);
                    drawing.perObjectData=PerObjectData.None;
                    int grassLayer=LayerMask.NameToLayer("VoyageGrass");
                    int mask=camera.camera.cullingMask & (grassLayer<0 ? ~0 : ~(1<<grassLayer));
                    data.renderers=graph.CreateRendererList(new RendererListParams(rendering.cullResults,drawing,new FilteringSettings(RenderQueueRange.opaque,mask)));
                    builder.UseRendererList(data.renderers); builder.SetRenderAttachmentDepth(depth,AccessFlags.Write);
                    builder.SetGlobalTextureAfterPass(depth,Shader.PropertyToID("_VoyageContourDepth"));
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (DepthData d,RasterGraphContext context)=>
                    {
                        context.cmd.SetGlobalFloat("_VoyageContourDepthPass",1);
                        context.cmd.DrawRendererList(d.renderers);
                        context.cmd.SetGlobalFloat("_VoyageContourDepthPass",0);
                    });
                }
                var source=resources.cameraColor; var desc=graph.GetTextureDesc(source); desc.name="Distant terrain contours"; desc.clearBuffer=false;
                var target=graph.CreateTexture(desc);
                using(var builder=graph.AddRasterRenderPass<Data>("Voyage distant contours",out var data))
                {
                    data.material=material; data.source=source;
                    builder.UseTexture(source); builder.UseTexture(depth);
                    builder.SetRenderAttachment(target,0);
                    builder.SetRenderFunc(static (Data d,RasterGraphContext context)=>Blitter.BlitTexture(context.cmd,d.source,new Vector4(1,1,0,0),d.material,0));
                }
                resources.cameraColor=target;
            }
        }
        public override void Create() { CoreUtils.Destroy(material); if(shader!=null) material=CoreUtils.CreateEngineMaterial(shader); pass=new ContourPass(); }
        public override void AddRenderPasses(ScriptableRenderer renderer,ref RenderingData data)
        {
            if(material==null || data.cameraData.renderType==CameraRenderType.Overlay || data.cameraData.cameraType==CameraType.Preview) return;
            pass.material=material; renderer.EnqueuePass(pass);
        }
        protected override void Dispose(bool disposing) { CoreUtils.Destroy(material); }
    }
}
