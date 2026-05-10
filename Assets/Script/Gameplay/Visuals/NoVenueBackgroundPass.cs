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

            RTHandle prevFrameHandle = RTHandles.Alloc(_trailsTexture);
            var importInfo = new RenderTargetInfo
            {
                width = _trailsTexture.width,
                height = _trailsTexture.height,
                volumeDepth = _trailsTexture.volumeDepth,
                msaaSamples = _trailsTexture.antiAliasing,
                format = _trailsTexture.graphicsFormat
            };
            var importParams = new ImportResourceParams
            {
                clearOnFirstUse = false,
                discardOnLastUse = false
            };
            TextureHandle prevFrame = renderGraph.ImportTexture(prevFrameHandle, importInfo, importParams);
            TextureHandle target = resourceData.activeColorTexture;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("NoVenueBackgroundPass", out var passData, _profilingSampler))
            {
                builder.AllowPassCulling(false);
                passData.prevFrame = prevFrame;
                passData.material = _material;

                Vector4 scaleBias = SystemInfo.graphicsUVStartsAtTop
                    ? new Vector4(1, -1, 0, 1)
                    : new Vector4(1, 1, 0, 0);
                passData.scaleBias = scaleBias;

                builder.SetRenderAttachment(target, 0, AccessFlags.Write);

                builder.SetRenderFunc<PassData>((PassData data, RasterGraphContext context) =>
                {
                    var handle = data.prevFrame;
                    if (!handle.IsValid())
                        return;
                    Blitter.BlitTexture(context.cmd, (Texture)handle, data.scaleBias, data.material, 0);
                });
            }
        }

        private class PassData
        {
            public TextureHandle prevFrame;
            public Material material;
            public Vector4 scaleBias;
        }
    }
}
