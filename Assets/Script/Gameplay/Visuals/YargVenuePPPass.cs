using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// Single combined ScriptableRenderPass for YARG custom venue post-processing effects:
    /// mirror, scanlines, posterize, trails, vignette.
    /// Enqueued at AfterRenderingPostProcessing — reads URP post-processed frame as _MainTex.
    /// Uses pre-allocated temp texture (imported from VenueCameraRendererStatics) to avoid
    /// renderGraph.CreateTexture which crashes on Vulkan.
    /// Swaps result into cameraColor to avoid copy and enable pass merging.
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
            TextureHandle source = resourceData.activeColorTexture;

            // Import pre-allocated temp texture (avoids renderGraph.CreateTexture Vulkan crash)
            TextureHandle dest = renderGraph.ImportTexture(VenueCameraRenderer.VenueCameraRendererStatics._venuePPTextureHandle);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("YargVenuePPPass", out var passData, _profilingSampler))
            {
                builder.AllowPassCulling(false);
                passData.material = material;

                builder.SetRenderAttachment(dest, 0, AccessFlags.Write);
                builder.SetInputAttachment(source, 0);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }

            // Swap result into cameraColor — no copy needed, enables pass merging
            resourceData.cameraColor = dest;
        }

        private class PassData
        {
            public Material material;
        }
    }
}
