using System;
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
        public Camera NoVenueCamera;

        /// <summary>Shim - returns trails texture. Will be removed after BackgroundManager migration.</summary>
        [Obsolete("Use _trailsTexture directly or BackgroundManager RT. Will be removed.")]
        public static RenderTexture VenueTexture { get => VenueCameraRendererStatics._trailsTexture; }
        public static float TargetFPS { get => VenueCameraRendererStatics.TargetFPS; }
        public static float ActualFPS { get => VenueCameraRendererStatics.ActualFPS; }

        private void Awake()
        {
            renderScale = GraphicsManager.Instance.VenueRenderScale;
            _previousRenderScale = renderScale;
            _renderCamera = GetComponent<Camera>();
            // Disable the camera so we can control when it renders
            _renderCamera.enabled = false;

            _renderCamera.allowMSAA = false;
            _renderCamera.targetTexture = null;
            _renderCamera.allowDynamicResolution = true;

            var cameraData = _renderCamera.GetUniversalAdditionalCameraData();
            cameraData.antialiasing = AntialiasingMode.None;
            switch (GraphicsManager.Instance.VenueAntiAliasing)
            {
                case VenueAntiAliasingMethod.None:
                    break;
                case VenueAntiAliasingMethod.FXAA:
                    cameraData.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
                    break;
                case VenueAntiAliasingMethod.MSAA:
                    _renderCamera.allowMSAA = true;
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

            // Create render passes (must happen on main thread after Initialize guard)
            if (VenueCameraRendererStatics._highwayCompositePass == null)
            {
                VenueCameraRendererStatics._highwayCompositePass = new HighwayCompositePass();
                VenueCameraRendererStatics._noVenueBackgroundPass = new NoVenueBackgroundPass();
            }

            // Create No Venue camera for FPS skips and image/video backgrounds
            CreateNoVenueCamera();
        }

        private void CreateNoVenueCamera()
        {
            var go = new GameObject("No Venue Camera");
            go.layer = gameObject.layer;
            NoVenueCamera = go.AddComponent<Camera>();
            NoVenueCamera.enabled = false;
            NoVenueCamera.orthographic = true;
            NoVenueCamera.orthographicSize = 5f;
            NoVenueCamera.nearClipPlane = 0.1f;
            NoVenueCamera.farClipPlane = 10f;
            NoVenueCamera.clearFlags = CameraClearFlags.SolidColor;
            NoVenueCamera.backgroundColor = Color.black;
            NoVenueCamera.cullingMask = 0; // Don't render any scene objects
            NoVenueCamera.allowMSAA = false;

            var noVenueData = NoVenueCamera.GetUniversalAdditionalCameraData();
            noVenueData.renderType = CameraRenderType.Base;
        }

        public static void CreateUnscaledBackgroundTexture()
        {
            VenueCameraRendererStatics.RecreateTextures();
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnPreCameraRender;
            RenderPipelineManager.endCameraRendering += OnEndCameraRender;

            // Safety: disable No Venue camera when venue camera is enabled
            if (NoVenueCamera != null)
            {
                NoVenueCamera.enabled = false;
            }
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnPreCameraRender;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRender;
        }

        private void OnDestroy()
        {
            if (NoVenueCamera != null)
            {
                Destroy(NoVenueCamera.gameObject);
                NoVenueCamera = null;
            }
        }

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
                if (NoVenueCamera != null)
                {
                    NoVenueCamera.enabled = false;
                }
                Render();
                VenueCameraRendererStatics._frameAccumulator -= frameInterval;
            }
            else
            {
                // Venue skips — enable No Venue camera to show last frame
                if (NoVenueCamera != null)
                {
                    NoVenueCamera.enabled = true;
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
            // Handle No Venue camera
            if (cam == NoVenueCamera)
            {
                var noVenueRenderer = cam.GetUniversalAdditionalCameraData().scriptableRenderer;
                if (noVenueRenderer != null)
                {
                    noVenueRenderer.EnqueuePass(VenueCameraRendererStatics._noVenueBackgroundPass);
                    noVenueRenderer.EnqueuePass(VenueCameraRendererStatics._highwayCompositePass);
                }
                return;
            }

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


        public sealed class VenuePostPostProcessingPass : ScriptableRenderPass
        {
            public VenuePostPostProcessingPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                // Copy composited frame (venue + highways) to trails texture
                // This is used by No Venue camera to show the last rendered frame during FPS skips
                TextureHandle source = resourceData.activeColorTexture;
                TextureHandle trailsTexture = renderGraph.ImportTexture(VenueCameraRendererStatics._trailsTextureHandle);

                renderGraph.AddCopyPass(source, trailsTexture, passName: "Trails Frame Copy");
            }
        }

        public static class VenueCameraRendererStatics
        {
            public static RenderTexture _trailsTexture;
            public static RTHandle _trailsTextureHandle;

            public static readonly int _IsVenueId = Shader.PropertyToID("_YargIsVenue");
            public static readonly int _trailsLengthId = Shader.PropertyToID("_YargTrailLength");
            public static readonly int _trailsTextureId = Shader.PropertyToID("_YargPrevFrame");
            public static readonly int _posterizeStepsId = Shader.PropertyToID("_YargPosterizeSteps");
            public static readonly int _scanlineIntensityId = Shader.PropertyToID("_YargScanlineIntensity");
            public static readonly int _scanlineSizeId = Shader.PropertyToID("_YargScanlineSize");
            public static readonly int _scanlineColor = Shader.PropertyToID("_YargScanlineColor");
            public static readonly int _scanlineEasingPower = Shader.PropertyToID("_YargScanlineEasingPower");
            public static readonly int _wipeTimeId = Shader.PropertyToID("_YargMirrorWipeLength");
            public static readonly int _startTimeId = Shader.PropertyToID("_YargMirrorStartTime");

            public static readonly string[] _mirrorKeywords = { "YARG_MIRROR_LEFT", "YARG_MIRROR_RIGHT", "YARG_MIRROR_CLOCK_CCW", "YARG_MIRROR_NONE" };

            public static VenuePostPostProcessingPass _pass;
            public static HighwayCompositePass _highwayCompositePass;
            public static NoVenueBackgroundPass _noVenueBackgroundPass;

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
                _pass = new VenuePostPostProcessingPass();

                Shader.SetGlobalColor(_scanlineColor, Color.black);
                Shader.SetGlobalFloat(_scanlineEasingPower, 2.0f);

                FPS = SettingsManager.Settings.VenueFpsCap.Value;
                _venueLayerMask = LayerMask.GetMask("Venue");

                ResetRenderState();
            }

            public static void ResetRenderState()
            {
                _frameAccumulator = 0f;
                _fpsWindowStart = 0f;
                _fpsWindowFrames = 0;
            }

            public static void RecreateTextures()
            {
                if (_trailsTexture != null)
                {
                    _trailsTextureHandle?.Release();
                    _trailsTextureHandle = null;
                    _trailsTexture.Release();
                    _trailsTexture.DiscardContents();
                }

                var descriptor = new RenderTextureDescriptor(Screen.width, Screen.height, RenderTextureFormat.DefaultHDR, 0, 0);
                _trailsTexture = new RenderTexture(descriptor);
                _trailsTexture.filterMode = FilterMode.Bilinear;
                _trailsTexture.wrapMode = TextureWrapMode.Clamp;
                _trailsTexture.Create();
                _trailsTextureHandle = RTHandles.Alloc(_trailsTexture);
                Shader.SetGlobalTexture(_trailsTextureId, _trailsTexture);

                Graphics.Blit(Texture2D.blackTexture, _trailsTexture);
            }

            private static void OnSceneUnloaded(Scene scene)
            {
                if (_trailsTexture != null)
                {
                    _trailsTextureHandle?.Release();
                    _trailsTextureHandle = null;
                    _trailsTexture.Release();
                    Destroy(_trailsTexture);
                    _trailsTexture = null;
                }
            }

        }
    }
}
