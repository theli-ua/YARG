// YARG Venue Post-Processing Shader
// Combined pass: scanlines, posterize, trails
// Reads URP post-processed frame via framebuffer fetch (LOAD_FRAMEBUFFER_X_INPUT)
Shader "Hidden/YARG/VenuePP"
{
    Properties
    {
        _YargPosterizeSteps("Posterize Steps", Int) = 0
        _YargScanlineSize("Scanline Size", Int) = 0
        _YargScanlineIntensity("Scanline Intensity", Float) = 0.0
        _YargTrailLength("Trail Length", Float) = 0.0
        _YargPrevFrame("Previous Frame", 2D) = "" {}
    }

    HLSLINCLUDE

    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

    // Framebuffer fetch input — reads current framebuffer (URP post-processed output)
    FRAMEBUFFER_INPUT_HALF(0);

    // Previous frame texture for trails effect
    TEXTURE2D(_YargPrevFrame);

    // ── Posterize param (local via Properties block) ──
    int _YargPosterizeSteps;

    // ── Scanline params (local via Properties block) ──
    int _YargScanlineSize;
    float _YargScanlineIntensity;

    // ── Baked scanline constants (were set once in Initialize, never changed) ──
    static const half3 _ScanlineColor = half3(0, 0, 0);
    static const float _ScanlineEasingPower = 2.0;

    // ── Trails param (local via Properties block) ──
    float _YargTrailLength;

    // ── Posterize ──
    half3 YargPosterize(half3 col)
    {
        col = floor(col * _YargPosterizeSteps) / _YargPosterizeSteps;
        return col;
    }

    // ── Scanline helpers ──
    float ExpInOut(float t)
    {
        t = 2.0 * t - 1.0;
        float sign = (t < 0.0) ? -1.0 : 1.0;
        t = sign * (1.0 - pow(1.0 - abs(t), _ScanlineEasingPower));
        return 0.5 * (t + 1.0);
    }

    half3 ColorBlend(half3 original, half3 scanline, float t)
    {
        float easedT = ExpInOut(t) * _YargScanlineIntensity;
        float brightnessBoost = 1.0 + ((1.5 - 1.0) * (1.0 - easedT));
        half3 brightened = min(original * brightnessBoost, 1.0);
        half3 result;
        result.r = brightened.r * (1.0 - easedT) + scanline.r * easedT;
        result.g = brightened.g * (1.0 - easedT) + scanline.g * easedT;
        result.b = brightened.b * (1.0 - easedT) + scanline.b * easedT;
        return result;
    }

    half3 YargScanlines(half3 col, float2 uv)
    {
        float scanline = frac(uv.y * _YargScanlineSize);
        col = ColorBlend(col, _ScanlineColor, scanline);
        return col;
    }

    // ── Fragment ──
    half4 Frag(Varyings input) : SV_Target
    {
        float2 uv = input.texcoord;

        // Read post-processed frame via framebuffer fetch
        half4 fbColor = LOAD_FRAMEBUFFER_X_INPUT(0, uv);
        half3 col = fbColor.rgb;

        // ── Posterize ──
        if (_YargPosterizeSteps > 0)
        {
            col = YargPosterize(col);
        }

        // ── Scanlines ──
        if (_YargScanlineSize > 0)
        {
            col = YargScanlines(col, uv);
        }

        // ── Trails (blend with previous frame) ──
        if (_YargTrailLength > 0)
        {
            half3 prevCol = SAMPLE_TEXTURE2D(_YargPrevFrame, sampler_LinearClamp, uv).rgb;
            half luma = dot(prevCol, half3(0.2126, 0.7152, 0.0722));
            half mask = step(0.15, luma);
            col = max(col, prevCol * (mask - _YargTrailLength * 0.5));
        }

        return half4(col, 1.0);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "YargVenuePP"
            LOD 100
            ZTest Always ZWrite Off Cull Off

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment Frag
            ENDHLSL
        }
    }
}
