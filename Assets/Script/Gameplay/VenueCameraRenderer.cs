using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using static UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using YARG.Core.Logging;
using YARG.Gameplay.Visuals;
using YARG.Helpers.UI;
using YARG.Settings;
using YARG.Venue.VolumeComponents;

namespace YARG.Gameplay
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(Camera))]
    public class VenueCameraRenderer : MonoBehaviour
    {
        [Range(0.01F, 1.0F)]
        public float renderScale = 1.0F;

        internal Camera _renderCamera;
        private UniversalRenderPipelineAsset UniversalRenderPipelineAsset;
        private float _previousRenderScale;

        // Pass enqueue flags — set in Update(), consumed in OnPreCameraRender
        private bool _enqueueVenuePP;
        private bool _enqueueMirror;

        public static float TargetFPS { get => VenueCameraRendererStatics.TargetFPS; }
        public static float ActualFPS { get => VenueCameraRendererStatics.ActualFPS; }

        private void Awake()
        {
            _renderCamera = GetComponent<Camera>();
            renderScale = GraphicsManager.Instance.VenueRenderScale;
            _previousRenderScale = renderScale;
            // Disable the camera so we can control when it renders
            _renderCamera.enabled = false;

            _renderCamera.targetTexture = null;
            if (renderScale != 1.0)
            {
                // Only if actually needed otherwise it trips the TAA
                _renderCamera.allowDynamicResolution = true;
            }
            ScalableBufferManager.ResizeBuffers(renderScale, renderScale);

            var cameraData = _renderCamera.GetUniversalAdditionalCameraData();
            cameraData.antialiasing = AntialiasingMode.None;
            var aaMethod = GraphicsManager.Instance.VenueAntiAliasing;
            switch (aaMethod)
            {
                case VenueAntiAliasingMethod.None:
                    break;
                case VenueAntiAliasingMethod.FXAA:
                    cameraData.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
                    break;
                case VenueAntiAliasingMethod.SMAA:
                    cameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                    break;
                case VenueAntiAliasingMethod.TAA:
                    cameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                    break;
            }

            // TAA and MSAA are mutually exclusive. If TAA selected and DRS is off, disable MSAA.
            _renderCamera.allowMSAA = aaMethod != VenueAntiAliasingMethod.TAA || renderScale != 1.0;
            UniversalRenderPipelineAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

            // Initialize static state (passes, textures, shader globals)
            // Guard in Initialize() prevents double-init if BackgroundManager also calls it
            VenueCameraRendererStatics.Initialize();
        }

        public static void CreateUnscaledBackgroundTexture()
        {
            VenueCameraRendererStatics.RecreateTextures();
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnPreCameraRender;
            RenderPipelineManager.endCameraRendering += OnEndCameraRender;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnPreCameraRender;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRender;
        }

        private void LateUpdate()
        {
            if (ScreenSizeDetector.HasScreenSizeChanged)
            {
                ScalableBufferManager.ResizeBuffers(renderScale, renderScale);
                // Force a render this frame to avoid flickering when resizing
                VenueCameraRendererStatics.ResetRenderState();
                _previousRenderScale = renderScale;
            }

            // Update DRS buffers when VenueRenderScale changes (e.g. via settings)
            if (renderScale != _previousRenderScale)
            {
                ScalableBufferManager.ResizeBuffers(renderScale, renderScale);
                _previousRenderScale = renderScale;
            }

            // Update volume stack ONCE per frame. All volume components read here.
            VolumeManager.instance.Update(_renderCamera.gameObject.transform, VenueCameraRendererStatics._venueLayerMask);

            var stack = VolumeManager.instance.stack;

            // SlowFPS controls venue render rate
            VenueCameraRendererStatics._effectiveFps = VenueCameraRendererStatics.FPS;
            var fpsEffect = stack.GetComponent<SlowFPSComponent>();
            if (fpsEffect.IsActive())
            {
                VenueCameraRendererStatics._effectiveFps = Mathf.RoundToInt(60f / fpsEffect.Divisor.value);
                if (VenueCameraRendererStatics.FPS > 0)
                {
                    VenueCameraRendererStatics._effectiveFps = Mathf.Min(VenueCameraRendererStatics.FPS, VenueCameraRendererStatics._effectiveFps);
                }
            }

            // Read venue PP effects and set enqueue flags (consumed in OnPreCameraRender)
            _enqueueVenuePP = false;
            _enqueueMirror = false;
            ApplyVolumeEffects(stack);

            // Increment wall clock time regardless of whether we render a frame
            var currentFrameTime = Time.unscaledTime;

            // Accumulator-based FPS limiting: smooths quantization over time.
            // Add dt each frame, when accumulator >= frameInterval, render and subtract.
            // This averages to the exact target FPS regardless of Update() frequency.
            float frameInterval = VenueCameraRendererStatics._effectiveFps > 0 ? 1f / VenueCameraRendererStatics._effectiveFps : 0f;
            VenueCameraRendererStatics._frameAccumulator += Time.unscaledDeltaTime;

            if (VenueCameraRendererStatics._effectiveFps == 0 || VenueCameraRendererStatics._frameAccumulator >= frameInterval)
            {
                // Sliding window: reset every ~1 second, compute FPS from frame count / elapsed time.
                if (VenueCameraRendererStatics._fpsWindowStart > 0f && currentFrameTime - VenueCameraRendererStatics._fpsWindowStart > 1.0f)
                {
                    VenueCameraRendererStatics.ActualFPS = VenueCameraRendererStatics._fpsWindowFrames / (currentFrameTime - VenueCameraRendererStatics._fpsWindowStart);
                    VenueCameraRendererStatics._fpsWindowStart = currentFrameTime;
                    VenueCameraRendererStatics._fpsWindowFrames = 0;
                }

                VenueCameraRendererStatics._fpsWindowFrames++;
                if (VenueCameraRendererStatics._fpsWindowFrames == 1)
                {
                    VenueCameraRendererStatics._fpsWindowStart = currentFrameTime;
                }

                // Venue renders — disable No Venue camera, enable venue camera
                if (VenueCameraRendererStatics._noVenueCamera != null)
                {
                    VenueCameraRendererStatics._noVenueCamera.enabled = false;
                }
                Render();
                VenueCameraRendererStatics._frameAccumulator -= frameInterval;
            }
            else
            {
                // Venue skips — enable No Venue camera to show last frame
                if (VenueCameraRendererStatics._noVenueCamera != null)
                {
                    VenueCameraRendererStatics._noVenueCamera.enabled = true;
                }

            }
        }

        private void OnEndCameraRender(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _renderCamera)
            {
                return;
            }

            // Disable the camera after rendering so it only renders when explicitly triggered
            _renderCamera.enabled = false;
        }

        private void OnPreCameraRender(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _renderCamera)
            {
                return;
            }

            // Enqueue passes based on flags set in Update()
            var renderer = _renderCamera.GetUniversalAdditionalCameraData().scriptableRenderer;
            if (_enqueueVenuePP)
            {
                renderer.EnqueuePass(VenueCameraRendererStatics._yargVenuePPPass);
            }
            if (_enqueueMirror)
            {
                renderer.EnqueuePass(VenueCameraRendererStatics._mirrorEffectPass);
            }
            renderer.EnqueuePass(VenueCameraRendererStatics._venueFrameCopyPass);
            renderer.EnqueuePass(VenueCameraRendererStatics._highwayCompositePass);
        }

        private void ApplyVolumeEffects(VolumeStack stack)
        {
            var venuePPPass = VenueCameraRendererStatics._yargVenuePPPass;
            var mirrorPass = VenueCameraRendererStatics._mirrorEffectPass;

            var posterizeEffect = stack.GetComponent<PosterizeComponent>();
            if (posterizeEffect.IsActive())
            {
                YargLogger.LogFormatTrace("Venue PP: posterize, steps: {0}", posterizeEffect.Steps.value);
                venuePPPass.PosterizeSteps = posterizeEffect.Steps.value;
            }

            var mirrorEffect = stack.GetComponent<MirrorComponent>();
            if (mirrorEffect.IsActive())
            {
                YargLogger.LogFormatTrace("Venue PP: mirror, wipeStart: {0}", mirrorEffect.startTime.value);
                mirrorPass.MirrorStartTime = mirrorEffect.startTime.value;
                mirrorPass.MirrorWipeLength = mirrorEffect.wipeTime.value;
                mirrorPass.MirrorModeIndex = mirrorEffect.wipeIndex.value;
            }

            var scanlineEffect = stack.GetComponent<ScanlineComponent>();
            if (scanlineEffect.IsActive())
            {
                YargLogger.LogFormatTrace("Venue PP: scanline, line count: {0}", scanlineEffect.scanlineCount.value);
                venuePPPass.ScanlineIntensity = scanlineEffect.intensity.value;
                venuePPPass.ScanlineSize = scanlineEffect.scanlineCount.value;
            }

            var trailsEffect = stack.GetComponent<TrailsComponent>();
            if (trailsEffect.IsActive())
            {
                YargLogger.LogFormatTrace("Venue PP: trails, length: {0}", trailsEffect.length.value);
                var adjustedLength = Mathf.Pow(trailsEffect.Length, VenueCameraRendererStatics.ActualFPS / 60f);
                venuePPPass.TrailsLength = adjustedLength;
            }

            // Set enqueue flags — consumed in OnPreCameraRender
            _enqueueVenuePP = posterizeEffect.IsActive() || scanlineEffect.IsActive() || trailsEffect.IsActive();
            _enqueueMirror = mirrorEffect.IsActive();
        }

        private void Render()
        {
            // Render directly to backbuffer (no intermediate RT)
            _renderCamera.targetTexture = null;
            _renderCamera.enabled = true;
        }


        public sealed class VenueFrameCopyPass : ScriptableRenderPass
        {
            public VenueFrameCopyPass()
            {
                // Runs after MirrorEffectPass (event 96) to capture final output including mirror
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing + 2;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                // Copy venue + PP frame (before highways).
                // This is used by NoVenueCamera to show the last rendered frame during FPS skips.
                // And for trails PP effect
                TextureHandle source = resourceData.activeColorTexture;
                TextureHandle frameCopyTexture = renderGraph.ImportTexture(VenueCameraRendererStatics._previousFrameTexture);

                renderGraph.AddCopyPass(source, frameCopyTexture, passName: "Venue Frame Copy");
            }
        }

        public static class VenueCameraRendererStatics
        {
            public static RTHandle _previousFrameTexture;
            public static RTHandle _venuePPTexture;

            public static YargVenuePPPass _yargVenuePPPass;
            public static MirrorEffectPass _mirrorEffectPass;
            public static VenueFrameCopyPass _venueFrameCopyPass;
            public static HighwayCompositePass _highwayCompositePass;
            public static NoVenueBackgroundPass _noVenueBackgroundPass;

            // Shared No Venue camera — one instance across all VenueCameraRenderers
            internal static Camera _noVenueCamera;

            public static float ActualFPS;
            public static float TargetFPS;

            public static int _fps;
            public static int FPS
            {
                get => _fps;
                set
                {
                    _fps = value;
                    TargetFPS = value;
                }
            }
            public static int _effectiveFps;

            public static int _venueLayerMask;

            public static float _frameAccumulator = 0f;
            public static float _fpsWindowStart = 0f;
            public static int _fpsWindowFrames = 0;

            public static Material CreateMaterial(string shaderName)
            {
                var shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    YargLogger.LogFormatError("Failed to find shader {0}", shaderName);
                    return null;
                }

                return CoreUtils.CreateEngineMaterial(shader);
            }

            private static bool _isInitialized;

            public static void Initialize()
            {
                if (_isInitialized)
                    return;
                _isInitialized = true;

                SceneManager.sceneUnloaded += OnSceneUnloaded;
                RecreateTextures();
                _yargVenuePPPass = new YargVenuePPPass();
                _mirrorEffectPass = new MirrorEffectPass();
                _venueFrameCopyPass = new VenueFrameCopyPass();
                _highwayCompositePass = new HighwayCompositePass();
                _noVenueBackgroundPass = new NoVenueBackgroundPass();

                FPS = SettingsManager.Settings.VenueFpsCap.Value;
                _venueLayerMask = LayerMask.GetMask("Venue");

                ResetRenderState();
                EnsureNoVenueCamera();
            }

            public static void ResetRenderState()
            {
                _frameAccumulator = 0f;
                _fpsWindowStart = 0f;
                _fpsWindowFrames = 0;
            }

            private static void EnsureNoVenueCamera()
            {
                if (_noVenueCamera != null)
                    return;

                var go = new GameObject("No Venue Camera");
                _noVenueCamera = go.AddComponent<Camera>();
                go.AddComponent<NoVenueCameraRenderer>();
            }

            public static void RecreateTextures()
            {
                var universalRenderPipelineAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                _previousFrameTexture?.Release();
                _venuePPTexture?.Release();

                var descriptor = new RenderTextureDescriptor(Screen.width, Screen.height, RenderTextureFormat.DefaultHDR, 0, 0);
                descriptor.msaaSamples = universalRenderPipelineAsset.msaaSampleCount;
                descriptor.useDynamicScale = true;

                _previousFrameTexture = RTHandles.Alloc(descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "PreviousFrameTexture");
                Graphics.Blit(Texture2D.blackTexture, _previousFrameTexture);

                // Venue PP temp texture (avoid renderGraph.CreateTexture which crashes on Vulkan)
                _venuePPTexture = RTHandles.Alloc(descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "VenuePPTexture");
            }

            private static void OnSceneUnloaded(Scene scene)
            {
                _previousFrameTexture?.Release();
                _previousFrameTexture = null;
                _venuePPTexture?.Release();
                _venuePPTexture = null;

                if (_noVenueCamera != null)
                {
                    Destroy(_noVenueCamera.gameObject);
                    _noVenueCamera = null;
                }

                // Clean up materials held by render passes to prevent leaks across scene loads.
                CoreUtils.Destroy(_yargVenuePPPass?.material);
                _yargVenuePPPass = null;
                CoreUtils.Destroy(_mirrorEffectPass?.material);
                _mirrorEffectPass = null;
                CoreUtils.Destroy(_highwayCompositePass?.material);
                _highwayCompositePass = null;
                _noVenueBackgroundPass = null;
                _venueFrameCopyPass = null;

                _isInitialized = false;
            }

        }
    }
}
