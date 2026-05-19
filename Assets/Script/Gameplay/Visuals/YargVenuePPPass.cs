using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using YARG.Gameplay;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// ScriptableRenderPass for YARG custom venue post-processing effects:
    /// scanlines, posterize, trails, vignette (mirror extracted to MirrorEffectPass).
    /// Enqueued at AfterRenderingPostProcessing — uses framebuffer fetch for input.
    /// Uses VenuePostProcessingFrameData for ping-pong source/dest resolution.
    /// Swaps result into cameraColor to avoid copy and enable pass merging.
    /// Params set via public fields by VenueCameraRenderer.OnPreCameraRender, applied to material in RecordRenderGraph.
    /// </summary>
    public sealed class YargVenuePPPass : ScriptableRenderPass
    {
        private readonly ProfilingSampler _profilingSampler = new ProfilingSampler("YargVenuePPPass");
        internal Material material;

        // ── Local material property IDs ──
        static readonly int _posterizeStepsId = Shader.PropertyToID("_YargPosterizeSteps");
        static readonly int _scanlineIntensityId = Shader.PropertyToID("_YargScanlineIntensity");
        static readonly int _scanlineSizeId = Shader.PropertyToID("_YargScanlineSize");
        static readonly int _trailsLengthId = Shader.PropertyToID("_YargTrailLength");
        internal static readonly int _previousFrameTextureId = Shader.PropertyToID("_YargPrevFrame");

        // ── Public param fields (set by VenueCameraRenderer.OnPreCameraRender) ──
        public int PosterizeSteps;
        public float ScanlineIntensity;
        public int ScanlineSize;
        public float TrailsLength;

        public YargVenuePPPass()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            material = CoreUtils.CreateEngineMaterial("Hidden/YARG/VenuePP");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // Resolve source/dest via shared frame data abstraction
            var (source, dest) = VenuePostProcessingFrameData.GetSourceAndDest(renderGraph, frameData);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("YargVenuePPPass", out var passData, _profilingSampler))
            {
                builder.AllowPassCulling(false);
                passData.material = material;

                // Push local material properties from fields
                material.SetInt(_posterizeStepsId, PosterizeSteps);
                material.SetFloat(_scanlineIntensityId, ScanlineIntensity);
                material.SetInt(_scanlineSizeId, ScanlineSize);
                material.SetFloat(_trailsLengthId, TrailsLength);
                material.SetTexture(_previousFrameTextureId, VenueCameraRenderer.VenueCameraRendererStatics._previousFrameTexture);

                builder.SetRenderAttachment(dest, 0, AccessFlags.Write);
                builder.SetInputAttachment(source, 0);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }

            // Set cameraColor to output texture (dest) before swap
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                resourceData.cameraColor = dest;
            }

            // Swap source/dest for next pass in chain
            VenuePostProcessingFrameData.Swap(frameData);
        }

        private class PassData
        {
            public Material material;
        }
    }
}
