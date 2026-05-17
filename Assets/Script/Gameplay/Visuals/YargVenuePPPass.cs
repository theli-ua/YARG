using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// Single combined ScriptableRenderPass for YARG custom venue post-processing effects:
    /// mirror, scanlines, posterize, trails, vignette.
    /// Enqueued at AfterRenderingPostProcessing — reads URP post-processed frame via framebuffer fetch.
    /// Volume params are pushed to global shader properties by VenueCameraRenderer.OnPreCameraRender.
    /// </summary>
    public sealed class YargVenuePPPass : ScriptableRenderPass
    {
        private readonly ProfilingSampler _profilingSampler = new ProfilingSampler("YargVenuePPPass");
        internal Material material;

        public YargVenuePPPass()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            material = CoreUtils.CreateEngineMaterial("Hidden/YARG/VenuePP");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("YargVenuePPPass", out var passData, _profilingSampler))
            {
                builder.AllowPassCulling(false);
                passData.material = material;

                // Write to active color (backbuffer). Framebuffer fetch reads current framebuffer.
                TextureHandle target = resourceData.activeColorTexture;
                builder.SetRenderAttachment(target, 0, AccessFlags.Write);

                builder.SetRenderFunc<PassData>((PassData data, RasterGraphContext context) =>
                {
                    // Y-flip for platforms where graphics UV starts at top (Vulkan/DX12/Metal)
                    Vector4 scaleBias = SystemInfo.graphicsUVStartsAtTop
                        ? new Vector4(1, -1, 0, 1)
                        : new Vector4(1, 1, 0, 0);

                    Blitter.BlitTexture(context.cmd, scaleBias, data.material, 0);
                });
            }
        }

        private class PassData
        {
            public Material material;
        }
    }
}
