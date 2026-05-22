using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// Blits the HighwaysRT onto the backbuffer with SrcAlpha OneMinusSrcAlpha blending.
    /// Executes at AfterRendering event — after background and post-processing are in backbuffer.
    /// </summary>
    public sealed class HighwayCompositePass : ScriptableRenderPass
    {
        private readonly ProfilingSampler _profilingSampler = new ProfilingSampler("HighwayCompositePass");
        private Material _material;

        internal Material material => _material;

        public HighwayCompositePass()
        {
            renderPassEvent = RenderPassEvent.AfterRendering;
            _material = CoreUtils.CreateEngineMaterial("Hidden/YARG/HighwayComposite");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();

            TextureHandle highwaysColor = renderGraph.ImportTexture(HighwayCameraRendering.HighwaysColorTextureHandle);
            TextureHandle target = resourceData.activeColorTexture;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("HighwayCompositePass", out var passData, _profilingSampler))
            {
                builder.AllowPassCulling(false);
                builder.UseTexture(highwaysColor);
                passData.highwaysColor = highwaysColor;
                passData.material = _material;

                builder.SetRenderAttachment(target, 0, AccessFlags.Write);

                builder.SetRenderFunc<PassData>((PassData data, RasterGraphContext context) =>
                {
                    var handle = data.highwaysColor;
                    if (!handle.IsValid())
                        return;

                    // Y-flip: highway RT rendered with Y-up NDC but backbuffer uses Y-down on Vulkan/DX12/Metal.
                    Vector4 scaleBias = SystemInfo.graphicsUVStartsAtTop
                        ? new Vector4(1, -1, 0, 1)
                        : new Vector4(1, 1, 0, 0);

                    Blitter.BlitTexture(context.cmd, handle, scaleBias, data.material, 0);
                });
            }
        }

        private class PassData
        {
            public TextureHandle highwaysColor;
            public Material material;
        }
    }
}
