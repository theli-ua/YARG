using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace YARG.Gameplay.Visuals
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(Camera))]
    public class HighwayCameraRendering : MonoBehaviour
    {
        [Range(-3.0F, 3.0F)]
        public float curveFactor = 0.5F;
        [Range(0.00F, 100.0F)]
        public float zeroFadePosition = 3.0F;
        [Range(0.01F, 5.0F)]
        public float fadeSize = 1.25F;

        private Camera _renderCamera;
        private CurveFadePass _curveFadePass;
        private Shader _Shader;

        private float _prevZeroFade;
        private float _prevFadeSize;
        private float _prevCurveFactor;

        protected internal Vector2 fadeParams;
        protected internal RenderTexture rt;

        private void Awake()
        {
            _renderCamera = GetComponent<Camera>();
            _curveFadePass = new CurveFadePass(this);
            _Shader = Shader.Find("HighwayBlit");
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnPreCameraRender;
            UpdateParams();

            var descriptor = new RenderTextureDescriptor(
                Screen.width, Screen.height,
                RenderTextureFormat.ARGBHalf);
            descriptor.mipCount = 0;
            descriptor.enableRandomWrite = true;
            var renderTexture = new RenderTexture(descriptor);

            rt = new RenderTexture(descriptor);
            rt.Create();
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnPreCameraRender;

            rt.Release();
            DestroyImmediate(rt);
            rt = null;
        }

        private void OnPreCameraRender(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _renderCamera)
            {
                return;
            }

            if (_prevCurveFactor != curveFactor || _prevZeroFade != zeroFadePosition || _prevFadeSize != fadeSize)
            {
                UpdateParams();
            }

            var renderer = _renderCamera.GetUniversalAdditionalCameraData().scriptableRenderer;
            renderer.EnqueuePass(_curveFadePass);
        }

        private void UpdateParams()
        {
            var offset = 3f;
            var worldZeroFadePosition = new Vector3(this.transform.position.x, this.transform.position.y, zeroFadePosition + offset);
            var worldFullFadePosition = new Vector3(this.transform.position.x, this.transform.position.y, zeroFadePosition + offset - fadeSize);
            Plane farPlane = new Plane();

            farPlane.SetNormalAndPosition(_renderCamera.transform.forward, worldZeroFadePosition);
            var fadeEnd = Mathf.Abs(farPlane.GetDistanceToPoint(_renderCamera.transform.position));

            farPlane.SetNormalAndPosition(_renderCamera.transform.forward, worldFullFadePosition);
            var fadeStart = Mathf.Abs(farPlane.GetDistanceToPoint(_renderCamera.transform.position));

            fadeParams = new Vector2(fadeStart, fadeEnd);

            _prevCurveFactor = curveFactor;
            _prevZeroFade = zeroFadePosition;
            _prevFadeSize = fadeSize;
        }

        // Curve and Fade could be separate render passes however
        // it seems natural to combine them to not go over
        // whole screen worth of data twice and we do not plan to
        // use them separately from each other
        private sealed class CurveFadePass : ScriptableRenderPass
        {
            // Kernel ID
            private int kernelHandle;

            // Property IDs
            private static readonly int SourceTextureID = Shader.PropertyToID("_SourceTexture");
            private static readonly int DepthTextureID = Shader.PropertyToID("_DepthTexture");
            private static readonly int DestinationTextureID = Shader.PropertyToID("_DestinationTexture");
            private static readonly int ZBufferParamsID = Shader.PropertyToID("_ZBufferParams");
            private static readonly int FadeParamsID = Shader.PropertyToID("_FadeParams");
            private static readonly int CurveFactorID = Shader.PropertyToID("_CurveFactor");
            private static readonly int IsFadingID = Shader.PropertyToID("_IsFading");
            private static readonly int SourceTexture_TexelSizeID = Shader.PropertyToID("_SourceTexture_TexelSize");
            private static readonly int depthTexturePropertyID = Shader.PropertyToID("_CameraDepthTexture");
            private static readonly int motionTexturePropertyID = Shader.PropertyToID("_MotionVectorTexture");

            private ComputeShader computeShader;

            private ProfilingSampler _ProfilingSampler = new ProfilingSampler("HighwayBlit");
            private CommandBuffer _cmd;
            private HighwayCameraRendering _highwayCameraRendering;

            public CurveFadePass(HighwayCameraRendering highCamRend)
            {
                _highwayCameraRendering = highCamRend;
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
                ConfigureInput(ScriptableRenderPassInput.Depth);
                computeShader = Resources.Load<ComputeShader>("HighwayPP");
                kernelHandle = computeShader.FindKernel("CSHighwayEffect");
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (computeShader == null)
                {
                    return;
                }

                ScriptableRenderer renderer = renderingData.cameraData.renderer;
                Camera camera = renderingData.cameraData.camera;
                CommandBuffer cmd = CommandBufferPool.Get("HighwayBlit");

                using (new ProfilingScope(cmd, _ProfilingSampler))
                {
                    // Get camera color and depth textures
                    var sourceTextureHandle = renderer.cameraColorTarget;

                    // Set compute shader parameters
                    cmd.SetComputeTextureParam(computeShader, kernelHandle, SourceTextureID, sourceTextureHandle);
                    cmd.SetComputeTextureParam(computeShader, kernelHandle, DepthTextureID, Shader.GetGlobalTexture(depthTexturePropertyID), 0, RenderTextureSubElement.Depth);
                    cmd.SetComputeTextureParam(computeShader, kernelHandle, DestinationTextureID, _highwayCameraRendering.rt);

                    // Set parameters
                    float near = camera.nearClipPlane;
                    float far = camera.farClipPlane;
                    float invNear = Mathf.Approximately(near, 0.0f) ? 0.0f : 1.0f / near;
                    float invFar = Mathf.Approximately(far, 0.0f) ? 0.0f : 1.0f / far;
                    float zc0 = 1.0f - far * invNear;
                    float zc1 = far * invNear;
                    Vector4 zBufferParams = new Vector4(zc0, zc1, zc0 * invFar, zc1 * invFar);

                    if (SystemInfo.usesReversedZBuffer)
                    {
                        zBufferParams.y += zBufferParams.x;
                        zBufferParams.x = -zBufferParams.x;
                        zBufferParams.w += zBufferParams.z;
                        zBufferParams.z = -zBufferParams.z;
                    }

                    cmd.SetComputeVectorParam(computeShader, ZBufferParamsID, zBufferParams);
                    cmd.SetComputeVectorParam(computeShader, FadeParamsID, _highwayCameraRendering.fadeParams);
                    cmd.SetComputeFloatParam(computeShader, CurveFactorID, _highwayCameraRendering.curveFactor);
                    cmd.SetComputeFloatParam(computeShader, IsFadingID, Shader.GetGlobalFloat(IsFadingID));

                    // Set texel size
                    Vector4 texelSize = new Vector4(
                        1.0f / camera.pixelWidth,
                        1.0f / camera.pixelHeight,
                        camera.pixelWidth,
                        camera.pixelHeight
                    );
                    cmd.SetComputeVectorParam(computeShader, SourceTexture_TexelSizeID, texelSize);

                    // Dispatch compute shader
                    int threadGroupsX = Mathf.CeilToInt(camera.pixelWidth / 8.0f);
                    int threadGroupsY = Mathf.CeilToInt(camera.pixelHeight / 8.0f);
                    cmd.DispatchCompute(computeShader, kernelHandle, threadGroupsX, threadGroupsY, 1);

                    // Blit back
                    cmd.Blit(_highwayCameraRendering.rt, sourceTextureHandle);
                }
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                CommandBufferPool.Release(cmd);
            }
        }
    }
}
