using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// Blits the HighwaysRT onto the backbuffer with SrcAlpha OneMinusSrcAlpha blending.
    /// Executes at AfterRendering event — after background is in backbuffer, before UberPP.
    /// </summary>
    public sealed class HighwayCompositePass : ScriptableRenderPass
    {
        private readonly ProfilingSampler _profilingSampler = new ProfilingSampler("HighwayCompositePass");
        private readonly Material _material;

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

            // Use unsafe pass (same as AddBlitPass) so we can set render target + use Blitter directly.
            // Raster pass's RasterCommandBuffer doesn't expose SetRenderTarget.
            using (var builder = renderGraph.AddUnsafePass<PassData>("HighwayCompositePass", out var passData, _profilingSampler))
            {
                passData.highwaysColor = highwaysColor;
                passData.target = target;
                passData.material = _material;

                builder.UseTexture(highwaysColor, AccessFlags.Read);
                builder.UseTexture(target, AccessFlags.Write);

                builder.SetRenderFunc<PassData>((PassData data, UnsafeGraphContext context) =>
                {
                    var handle = data.highwaysColor;
                    if (!handle.IsValid())
                        return;

                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

                    // Y-flip: highway RT rendered with Y-up NDC but backbuffer uses Y-down on Vulkan/DX12/Metal.
                    // Without flip, highway appears upside-down when composited.
                    Vector4 scaleBias = SystemInfo.graphicsUVStartsAtTop
                        ? new Vector4(1, -1, 0, 1)
                        : new Vector4(1, 1, 0, 0);

                    cmd.SetRenderTarget(data.target);
                    Blitter.BlitTexture(cmd, handle, scaleBias, data.material, 0);
                });
            }
        }

        private class PassData
        {
            public TextureHandle highwaysColor;
            public TextureHandle target;
            public Material material;
        }
    }
}
