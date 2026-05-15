using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static YARG.Gameplay.VenueCameraRenderer;

namespace YARG.Gameplay
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(Camera))]
    public class NoVenueCameraRenderer : MonoBehaviour
    {

        internal Camera _noVenueCamera;

        private void Awake()
        {
            _noVenueCamera = GetComponent<Camera>();
            _noVenueCamera.allowDynamicResolution = false;
            _noVenueCamera.enabled = true;
            _noVenueCamera.orthographic = true;
            _noVenueCamera.orthographicSize = 5f;
            _noVenueCamera.nearClipPlane = 0.1f;
            _noVenueCamera.farClipPlane = 10f;
            _noVenueCamera.clearFlags = CameraClearFlags.SolidColor;
            _noVenueCamera.backgroundColor = Color.black;
            _noVenueCamera.cullingMask = 0; // Don't render any scene objects
            _noVenueCamera.allowMSAA = false;

            var noVenueData = _noVenueCamera.GetUniversalAdditionalCameraData();
            noVenueData.renderType = CameraRenderType.Base;
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnPreCameraRender;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnPreCameraRender;
        }

        private void OnPreCameraRender(ScriptableRenderContext ctx, Camera cam)
        {
            // Handle No Venue camera
            if (cam != _noVenueCamera)
            {
                return;
            }
            var noVenueRenderer = cam.GetUniversalAdditionalCameraData().scriptableRenderer;
            if (noVenueRenderer != null)
            {
                noVenueRenderer.EnqueuePass(VenueCameraRendererStatics._noVenueBackgroundPass);
                noVenueRenderer.EnqueuePass(VenueCameraRendererStatics._highwayCompositePass);
            }
        }
    }
}
