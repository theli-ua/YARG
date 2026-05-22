using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// Ping-pong frame data for multi-pass venue post-processing chain.
    /// Manages source/destination texture handles between passes, avoiding new RT allocations.
    /// Stored in URP's per-frame ContextContainer via Create/Get pattern.
    /// </summary>
    public class VenuePostProcessingFrameData : ContextItem
    {
        public TextureHandle currentSource;
        public TextureHandle currentDest;

        /// <summary>
        /// Gets source and destination texture handles for the next pass in the chain.
        /// If frame data does not exist yet, creates it with:
        ///   source = activeColorTexture (URP post-processed output)
        ///   dest = imported _venuePPTexture (pre-allocated temp)
        /// </summary>
        public static (TextureHandle source, TextureHandle dest) GetSourceAndDest(
            RenderGraph renderGraph,
            ContextContainer frameData)
        {
            // GetOrCreate: first pass creates, subsequent passes reuse
            var data = frameData.GetOrCreate<VenuePostProcessingFrameData>();

            // If this is a fresh creation (currentSource is invalid), initialize it
            if (!data.currentSource.IsValid())
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                data.currentSource = resourceData.activeColorTexture;
                data.currentDest = renderGraph.ImportTexture(VenueCameraRenderer.VenueCameraRendererStatics._venuePPTexture);
            }

            return (data.currentSource, data.currentDest);
        }

        /// <summary>
        /// Swaps currentSource ↔ currentDest for the next pass in the chain.
        /// </summary>
        public static void Swap(ContextContainer frameData)
        {
            if (!frameData.Contains<VenuePostProcessingFrameData>())
                return;

            var data = frameData.Get<VenuePostProcessingFrameData>();
            var temp = data.currentSource;
            data.currentSource = data.currentDest;
            data.currentDest = temp;
        }

        /// <summary>
        /// Checks if frame data has been initialized (first pass has run).
        /// </summary>
        public static bool Exists(ContextContainer frameData)
        {
            return frameData.Contains<VenuePostProcessingFrameData>();
        }

        /// <inheritdoc />
        public override void Reset()
        {
            currentSource = default;
            currentDest = default;
        }
    }
}
