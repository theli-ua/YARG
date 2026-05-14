using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// Renders a fullscreen quad sampling a background texture.
    /// Used by the No Venue camera to show the last rendered frame during FPS skips,
    /// or an image/video background. The texture is set via <see cref="backgroundTexture"/>
    /// by BackgroundManager (points to _trailsTexture for venues, or _backgroundRT for image/video).
    /// </summary>
    public sealed class NoVenueBackgroundPass : ScriptableRenderPass
    {
        private readonly ProfilingSampler _profilingSampler = new ProfilingSampler("NoVenueBackgroundPass");
        private Material _material;

        internal Material material => _material;

        /// <summary>
        /// The texture to display as the background. Set by BackgroundManager.
        /// </summary>
        public RTHandle backgroundTexture;

        public NoVenueBackgroundPass()
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
            _material = CoreUtils.CreateEngineMaterial("Hidden/YARG/NoVenueQuad");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (backgroundTexture == null)
                return;

            var resourceData = frameData.Get<UniversalResourceData>();

            TextureHandle source = renderGraph.ImportTexture(backgroundTexture);
            TextureHandle target = resourceData.activeColorTexture;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("NoVenueBackgroundPass", out var passData, _profilingSampler))
            {
                builder.AllowPassCulling(false);
                passData.source = source;
                passData.material = _material;

                Vector4 scaleBias = SystemInfo.graphicsUVStartsAtTop
                    ? new Vector4(1, -1, 0, 1)
                    : new Vector4(1, 1, 0, 0);
                passData.scaleBias = scaleBias;

                builder.SetRenderAttachment(target, 0, AccessFlags.Write);

                builder.SetRenderFunc<PassData>((PassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.source, data.scaleBias, data.material, 0);
                });
            }
        }

        private class PassData
        {
            public TextureHandle source;
            public Material material;
            public Vector4 scaleBias;
        }
    }
}
