using UnityEngine;
using YARG.Core.Logging;

namespace YARG.Venue
{
    // WARNING: Changing this could break themes or venues!
    //
    // This script is used a lot in theme creation.
    // Changing the serialized fields in this file will result in older themes
    // not working properly. Only change if you need to.

    public class NeonLightManager : MonoBehaviour
    {
        private static readonly int _emissionMultiplier = Shader.PropertyToID("_Emission_Multiplier");
        private static readonly int _emissionSecondaryColor = Shader.PropertyToID("_Emission_Secondary_Color");
        private static readonly int _emissionColor = Shader.PropertyToID("_EmissionColor");

        [SerializeField]
        private Material[] _neonMaterials;

		[System.Serializable]
		public struct NeonFullColor {
			public Material Material;
			public VenueLightLocation Location;
			public VenueSpotLightLocation SpotLocation;
			[System.NonSerialized]
			public Color InitialColor;
		}

		[SerializeField]
		private NeonFullColor[] _neonMaterialsFullColor;

        private LightManager _lightManager;

        // Cached values to skip redundant Material.SetFloat/SetColor calls
        private float _lastGenericIntensity;
        private Color _lastGenericSecondaryColor;

        // Per-location cache for _neonMaterialsFullColor (indexed by VenueLightLocation)
        private float[] _lastLocationIntensity;
        private Color[] _lastLocationColor;
        private bool[]  _hasLocationColor;

        // Per-spot cache for spotlight lerp (indexed by VenueSpotLightLocation)
        private float[] _lastSpotIntensity;

        private void Start()
        {
            _lightManager = FindFirstObjectByType<LightManager>();

			for (int i = 0; i < _neonMaterialsFullColor.Length; i++) {
				_neonMaterialsFullColor[i].InitialColor = (_neonMaterialsFullColor[i].Material.GetColor(_emissionColor));
			}

            // Init caches so first frame always applies
            _lastGenericIntensity = float.NaN;

            _lastLocationIntensity = new float[System.Enum.GetValues(typeof(VenueLightLocation)).Length];
            _lastLocationColor = new Color[System.Enum.GetValues(typeof(VenueLightLocation)).Length];
            _hasLocationColor = new bool[System.Enum.GetValues(typeof(VenueLightLocation)).Length];

            _lastSpotIntensity = new float[System.Enum.GetValues(typeof(VenueSpotLightLocation)).Length];
            for (int i = 0; i < _lastSpotIntensity.Length; i++)
                _lastSpotIntensity[i] = float.NaN;
        }

        private void Update()
        {
           // Skip entirely if light states didn't change
            if (!_lightManager.LightStatesDirty)
                return;

            _lightManager.LightStatesDirty = false;

            // Update generic neon materials
            var genericState = _lightManager.GenericLightState;
            float genericIntensity = genericState.Intensity;
            Color genericSecondaryColor = genericState.Color ?? Color.white;
            bool hasGenericSecondary = genericState.Color.HasValue;

            bool intensityChanged = genericIntensity != _lastGenericIntensity;
            bool secondaryChanged = genericSecondaryColor != _lastGenericSecondaryColor;

            if (intensityChanged)
            {
                _lastGenericIntensity = genericIntensity;
                foreach (var material in _neonMaterials)
                    material.SetFloat(_emissionMultiplier, genericIntensity);
            }

            if (secondaryChanged)
            {
                _lastGenericSecondaryColor = genericSecondaryColor;
                foreach (var material in _neonMaterials)
                    material.SetColor(_emissionSecondaryColor, genericSecondaryColor);
            }

            // Update per-location neon materials
            for (int i = 0; i < _neonMaterialsFullColor.Length; i++)
            {
                var neon = _neonMaterialsFullColor[i];

                switch ((neon.Location, neon.SpotLocation))
                {
                    // --- Standard location cases (cached compare) ---
                    case (VenueLightLocation.Generic, VenueSpotLightLocation.None):
                        ApplyLocationState(neon.Material, _lightManager.GenericLightState, neon.InitialColor, (int)neon.Location);
                        break;
                    case (VenueLightLocation.Left, VenueSpotLightLocation.None):
                        ApplyLocationState(neon.Material, _lightManager.LeftLightState, neon.InitialColor, (int)neon.Location);
                        break;
                    case (VenueLightLocation.Right, VenueSpotLightLocation.None):
                        ApplyLocationState(neon.Material, _lightManager.RightLightState, neon.InitialColor, (int)neon.Location);
                        break;
                    case (VenueLightLocation.Front, VenueSpotLightLocation.None):
                        ApplyLocationState(neon.Material, _lightManager.FrontLightState, neon.InitialColor, (int)neon.Location);
                        break;
                    case (VenueLightLocation.Back, VenueSpotLightLocation.None):
                        ApplyLocationState(neon.Material, _lightManager.BackLightState, neon.InitialColor, (int)neon.Location);
                        break;
                    case (VenueLightLocation.Center, VenueSpotLightLocation.None):
                        ApplyLocationState(neon.Material, _lightManager.CenterLightState, neon.InitialColor, (int)neon.Location);
                        break;
                    case (VenueLightLocation.Crowd, VenueSpotLightLocation.None):
                        ApplyLocationState(neon.Material, _lightManager.CrowdLightState, neon.InitialColor, (int)neon.Location);
                        break;

                    // --- Spotlight lerp cases (always lerp, compare before SetFloat) ---
                    case (_, VenueSpotLightLocation.Bass):
                        ApplySpotLerp(neon.Material, VenueSpotLightLocation.Bass);
                        break;
                    case (_, VenueSpotLightLocation.Drums):
                        ApplySpotLerp(neon.Material, VenueSpotLightLocation.Drums);
                        break;
                    case (_, VenueSpotLightLocation.Guitar):
                        ApplySpotLerp(neon.Material, VenueSpotLightLocation.Guitar);
                        break;
                    case (_, VenueSpotLightLocation.Vocals):
                        ApplySpotLerp(neon.Material, VenueSpotLightLocation.Vocals);
                        break;

                    default:
                        YargLogger.LogDebug("Unknown location for neon light");
                        break;
                }
            }
        }

        /// <summary>Applies light state to a material, skipping calls if values unchanged.</summary>
        private void ApplyLocationState(Material material, LightManager.LightState state, Color initialColor, int locationIndex)
        {
            float intensity = state.Intensity;
            Color color = state.Color ?? initialColor;

            if (intensity != _lastLocationIntensity[locationIndex])
            {
                _lastLocationIntensity[locationIndex] = intensity;
                material.SetFloat(_emissionMultiplier, intensity);
            }

            if (color != _lastLocationColor[locationIndex] || _hasLocationColor[locationIndex] != state.Color.HasValue)
            {
                _lastLocationColor[locationIndex] = color;
                _hasLocationColor[locationIndex] = state.Color.HasValue;
                material.SetColor(_emissionColor, color);
            }
        }

        /// <summary>Lerps spotlight intensity, skipping SetFloat if converged.</summary>
        private void ApplySpotLerp(Material material, VenueSpotLightLocation spotLocation)
        {
            bool active = _lightManager.GetSpotlightStateFor(spotLocation);
            float current = material.GetFloat(_emissionMultiplier);
            float target = active ? 1f : 0f;
            float newValue = Mathf.Lerp(current, target, Time.deltaTime * 10f);

            int idx = (int)spotLocation;
            if (newValue != _lastSpotIntensity[idx])
            {
                _lastSpotIntensity[idx] = newValue;
                material.SetFloat(_emissionMultiplier, newValue);
            }
        }
    }
}
