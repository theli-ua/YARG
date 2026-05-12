using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using static YARG.Gameplay.VenueCameraRenderer.VenueCameraRendererStatics;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// Renders a fullscreen quad sampling _YargPrevFrame global texture.
    /// Used by the No Venue camera to show the last rendered frame during FPS skips.
    /// The shader reads _YargPrevFrame as a global — routing is handled by BackgroundManager
    /// (points to _trailsTexture for venues, or BackgroundManager RT for image/video).
    /// </summary>
    public sealed class NoVenueBackgroundPass : ScriptableRenderPass
    {
        private readonly ProfilingSampler _profilingSampler = new ProfilingSampler("NoVenueBackgroundPass");
        private readonly Material _material;

        public NoVenueBackgroundPass()
        {
            renderPassEvent = RenderPassEvent.BeforeRendering;
            _material = CoreUtils.CreateEngineMaterial("Hidden/YARG/NoVenueQuad");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            if (_trailsTexture == null)
                return;

            TextureHandle target = resourceData.activeColorTexture;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("NoVenueBackgroundPass", out var passData, _profilingSampler))
            {
                builder.AllowPassCulling(false);
                passData.material = _material;

                Vector4 scaleBias = SystemInfo.graphicsUVStartsAtTop
                    ? new Vector4(1, -1, 0, 1)
                    : new Vector4(1, 1, 0, 0);
                passData.scaleBias = scaleBias;

                builder.SetRenderAttachment(target, 0, AccessFlags.Write);

                builder.SetRenderFunc<PassData>((PassData data, RasterGraphContext context) =>
                {
                    // Shader samples _YargPrevFrame global texture directly.
                    // Blitter.BlitTexture renders the fullscreen quad; the source texture
                    // is irrelevant here since NoVenueQuad.shader reads the global.
                    Blitter.BlitTexture(context.cmd, Texture2D.blackTexture, data.scaleBias, data.material, 0);
                });
            }
        }

        private class PassData
        {
            public Material material;
            public Vector4 scaleBias;
        }
    }
}
