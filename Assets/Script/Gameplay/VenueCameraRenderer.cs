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

        public static float TargetFPS { get => VenueCameraRendererStatics.TargetFPS; }
        public static float ActualFPS { get => VenueCameraRendererStatics.ActualFPS; }

        private void Awake()
        {
            _renderCamera = GetComponent<Camera>();
            renderScale = GraphicsManager.Instance.VenueRenderScale;
            _previousRenderScale = renderScale;
            // Disable the camera so we can control when it renders
            _renderCamera.enabled = false;

            _renderCamera.allowMSAA = true;
            _renderCamera.targetTexture = null;
            _renderCamera.allowDynamicResolution = true;
            ScalableBufferManager.ResizeBuffers(renderScale, renderScale);

            var cameraData = _renderCamera.GetUniversalAdditionalCameraData();
            cameraData.antialiasing = AntialiasingMode.None;
            switch (GraphicsManager.Instance.VenueAntiAliasing)
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

        // NoVenueCamera is shared static — destroyed by VenueCameraRendererStatics.OnSceneUnloaded()

        private void Update()
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

            // Update the global volume stack with venue effects so SlowFPS
            // (and any other effects read in Update()) can access them.
            VolumeManager.instance.Update(_renderCamera.gameObject.transform, VenueCameraRendererStatics._venueLayerMask);

            VenueCameraRendererStatics._effectiveFps = VenueCameraRendererStatics.FPS;

            var stack = VolumeManager.instance.stack;
            var fpsEffect = stack.GetComponent<SlowFPSComponent>();

            if (fpsEffect.IsActive())
            {
                // Divisor is relative to 60 FPS, so target is always 60/divisor
                VenueCameraRendererStatics._effectiveFps = Mathf.RoundToInt(60f / fpsEffect.Divisor.value);
                // Clamp to FPS cap if non-zero (no cap when FPS=0)
                if (VenueCameraRendererStatics.FPS > 0)
                {
                    VenueCameraRendererStatics._effectiveFps = Mathf.Min(VenueCameraRendererStatics.FPS, VenueCameraRendererStatics._effectiveFps);
                }
            }

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

            Shader.SetGlobalInteger(VenueCameraRendererStatics._posterizeStepsId, 0);
            Shader.SetGlobalFloat(VenueCameraRendererStatics._startTimeId, 0);
            Shader.SetGlobalFloat(VenueCameraRendererStatics._IsVenueId, 0);
            Shader.SetGlobalInt(VenueCameraRendererStatics._scanlineSizeId, 0);
            Shader.SetGlobalFloat(VenueCameraRendererStatics._trailsLengthId, 0);
        }

        private void OnPreCameraRender(ScriptableRenderContext ctx, Camera cam)
        {
            // Handle venue camera
            if (cam != _renderCamera)
            {
                return;
            }

            Shader.SetGlobalFloat(VenueCameraRendererStatics._IsVenueId, 1);

            // URP replaces VolumeManager.instance.stack with either the global stack
            // or the camera's local volumeStack during rendering setup, depending on
            // the volume framework update mode. We need to update the same stack that
            // URP is using, so we update it here (after URP's setup) before reading.
            VolumeManager.instance.Update(VolumeManager.instance.stack, _renderCamera.gameObject.transform, VenueCameraRendererStatics._venueLayerMask);

            var stack = VolumeManager.instance.stack;

            var posterizeEffect = stack.GetComponent<PosterizeComponent>();
            if (posterizeEffect.IsActive())
            {
                YargLogger.LogFormatTrace("Venue PP: posterize, steps: {0}", posterizeEffect.Steps.value);
                Shader.SetGlobalInteger(VenueCameraRendererStatics._posterizeStepsId, posterizeEffect.Steps.value);
            }

            var mirrorEffect = stack.GetComponent<MirrorComponent>();
            if (mirrorEffect.IsActive())
            {
                for (int i = 0; i < VenueCameraRendererStatics._mirrorKeywords.Length; ++i)
                {
                    if (i == mirrorEffect.wipeIndex.value)
                    {
                        Shader.EnableKeyword(VenueCameraRendererStatics._mirrorKeywords[i]);
                    }
                    else
                    {
                        Shader.DisableKeyword(VenueCameraRendererStatics._mirrorKeywords[i]);
                    }
                }
                YargLogger.LogFormatTrace("Venue PP: mirror, wipeStart: {0}", mirrorEffect.startTime.value);
                Shader.SetGlobalFloat(VenueCameraRendererStatics._wipeTimeId, mirrorEffect.wipeTime.value);
                Shader.SetGlobalFloat(VenueCameraRendererStatics._startTimeId, mirrorEffect.startTime.value);
            }

            var scanlineEffect = stack.GetComponent<ScanlineComponent>();
            if (scanlineEffect.IsActive())
            {
                YargLogger.LogFormatTrace("Venue PP: scanline, line count: {0}", scanlineEffect.scanlineCount.value);
                Shader.SetGlobalFloat(VenueCameraRendererStatics._scanlineIntensityId, scanlineEffect.intensity.value);
                Shader.SetGlobalInt(VenueCameraRendererStatics._scanlineSizeId, scanlineEffect.scanlineCount.value);
            }

            var trailsEffect = stack.GetComponent<TrailsComponent>();
            if (trailsEffect.IsActive())
            {
                YargLogger.LogFormatTrace("Venue PP: trails, length: {0}", trailsEffect.length.value);
                var adjustedLength = Mathf.Pow(trailsEffect.Length, VenueCameraRendererStatics.ActualFPS / 60f);
                Shader.SetGlobalFloat(VenueCameraRendererStatics._trailsLengthId, adjustedLength);
            }

            var renderer = _renderCamera.GetUniversalAdditionalCameraData().scriptableRenderer;
            renderer.EnqueuePass(VenueCameraRendererStatics._pass);
            renderer.EnqueuePass(VenueCameraRendererStatics._highwayCompositePass);
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
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                // Copy venue + PP frame (before highways).
                // This is used by NoVenueCamera to show the last rendered frame during FPS skips.
                // And for trails PP effect
                TextureHandle source = resourceData.activeColorTexture;
                TextureHandle frameCopyTexture = renderGraph.ImportTexture(VenueCameraRendererStatics._previousFrameTextureHandle);

                renderGraph.AddCopyPass(source, frameCopyTexture, passName: "Venue Frame Copy");
            }
        }

        public static class VenueCameraRendererStatics
        {
            public static RenderTexture _previousFrameTexture;
            public static RTHandle _previousFrameTextureHandle;

            public static readonly int _IsVenueId = Shader.PropertyToID("_YargIsVenue");
            public static readonly int _trailsLengthId = Shader.PropertyToID("_YargTrailLength");
            public static readonly int _previousFrameTextureId = Shader.PropertyToID("_YargPrevFrame");
            public static readonly int _posterizeStepsId = Shader.PropertyToID("_YargPosterizeSteps");
            public static readonly int _scanlineIntensityId = Shader.PropertyToID("_YargScanlineIntensity");
            public static readonly int _scanlineSizeId = Shader.PropertyToID("_YargScanlineSize");
            public static readonly int _scanlineColor = Shader.PropertyToID("_YargScanlineColor");
            public static readonly int _scanlineEasingPower = Shader.PropertyToID("_YargScanlineEasingPower");
            public static readonly int _wipeTimeId = Shader.PropertyToID("_YargMirrorWipeLength");
            public static readonly int _startTimeId = Shader.PropertyToID("_YargMirrorStartTime");

            public static readonly string[] _mirrorKeywords = { "YARG_MIRROR_LEFT", "YARG_MIRROR_RIGHT", "YARG_MIRROR_CLOCK_CCW", "YARG_MIRROR_NONE" };

            public static VenueFrameCopyPass _pass;
            public static HighwayCompositePass _highwayCompositePass;
            public static NoVenueBackgroundPass _noVenueBackgroundPass;
            public static Material _frameCopyMaterial;

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
                _frameCopyMaterial = CoreUtils.CreateEngineMaterial("Hidden/YARG/NoVenueQuad");
                RecreateTextures();
                _pass = new VenueFrameCopyPass();
                _highwayCompositePass = new HighwayCompositePass();
                _noVenueBackgroundPass = new NoVenueBackgroundPass();

                Shader.SetGlobalColor(_scanlineColor, Color.black);
                Shader.SetGlobalFloat(_scanlineEasingPower, 2.0f);

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
                var UniversalRenderPipelineAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (_previousFrameTexture != null)
                {
                    _previousFrameTextureHandle?.Release();
                    _previousFrameTextureHandle = null;
                    _previousFrameTexture.Release();
                    _previousFrameTexture.DiscardContents();
                }

                var descriptor = new RenderTextureDescriptor(Screen.width, Screen.height, RenderTextureFormat.DefaultHDR, 0, 0);
                descriptor.msaaSamples = UniversalRenderPipelineAsset.msaaSampleCount;

                _previousFrameTexture = new RenderTexture(descriptor);
                _previousFrameTexture.filterMode = FilterMode.Bilinear;
                _previousFrameTexture.wrapMode = TextureWrapMode.Clamp;
                _previousFrameTexture.useDynamicScale = true;
                _previousFrameTexture.Create();
                _previousFrameTextureHandle = RTHandles.Alloc(_previousFrameTexture);
                Shader.SetGlobalTexture(_previousFrameTextureId, _previousFrameTexture);

                Graphics.Blit(Texture2D.blackTexture, _previousFrameTexture);
            }

            private static void OnSceneUnloaded(Scene scene)
            {
                if (_previousFrameTexture != null)
                {
                    _previousFrameTextureHandle?.Release();
                    _previousFrameTextureHandle = null;
                    _previousFrameTexture.Release();
                    Destroy(_previousFrameTexture);
                    _previousFrameTexture = null;
                }

                if (_noVenueCamera != null)
                {
                    Destroy(_noVenueCamera.gameObject);
                    _noVenueCamera = null;
                }

                // Clean up materials held by render passes to prevent leaks across scene loads.
                CoreUtils.Destroy(_frameCopyMaterial);
                _frameCopyMaterial = null;
                CoreUtils.Destroy(_highwayCompositePass?.material);
                _highwayCompositePass = null;
                _noVenueBackgroundPass = null;
                _pass = null;

                _isInitialized = false;
            }

        }
    }
}
