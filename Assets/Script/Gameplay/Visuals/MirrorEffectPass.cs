using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using YARG.Core.Logging;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// Separate ScriptableRenderPass for the mirror UV distortion effect.
    /// Runs at AfterRenderingPostProcessing + 1, after VenuePP.
    /// Uses texture sampling (not framebuffer fetch) to support arbitrary UV transformation.
    /// </summary>
    public sealed class MirrorEffectPass : ScriptableRenderPass
    {
        private readonly ProfilingSampler _profilingSampler = new ProfilingSampler("MirrorEffectPass");
        internal Material material;

        // ── Local material property IDs ──
        static readonly int _mirrorStartTimeId = Shader.PropertyToID("_YargMirrorStartTime");
        static readonly int _mirrorWipeLengthId = Shader.PropertyToID("_YargMirrorWipeLength");

        // ── Local keywords for mirror modes ──
        static readonly string[] s_mirrorKeywordNames = { "YARG_MIRROR_LEFT", "YARG_MIRROR_RIGHT", "YARG_MIRROR_CLOCK_CCW", "YARG_MIRROR_NONE" };

        // ── Public param fields (set by VenueCameraRenderer.OnPreCameraRender) ──
        public float MirrorStartTime;
        public float MirrorWipeLength;

        public int MirrorModeIndex
        {
            set
            {
                if (material != null)
                {
                    for (int i = 0; i < s_mirrorKeywordNames.Length; i++)
                    {
                        if (i == value)
                            material.EnableKeyword(s_mirrorKeywordNames[i]);
                        else
                            material.DisableKeyword(s_mirrorKeywordNames[i]);
                    }
                }
            }
        }

        public MirrorEffectPass()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing + 1;
            material = CoreUtils.CreateEngineMaterial("Hidden/YARG/MirrorEffect");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // Resolve source/dest via shared frame data abstraction
            var (source, dest) = VenuePostProcessingFrameData.GetSourceAndDest(renderGraph, frameData);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("MirrorEffectPass", out var passData, _profilingSampler))
            {
                builder.AllowPassCulling(false);
                passData.material = material;

                // Push local material properties from fields
                YargLogger.LogFormatError("Venue PP: mirror, wipeStart: {0}", MirrorStartTime);
                material.SetFloat(_mirrorStartTimeId, MirrorStartTime);
                material.SetFloat(_mirrorWipeLengthId, MirrorWipeLength);

                builder.SetRenderAttachment(dest, 0, AccessFlags.Write);
                builder.UseTexture(source, AccessFlags.Read);

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
