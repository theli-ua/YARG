// YARG Mirror Effect Shader
// Separate pass for mirror UV distortion — uses texture sampling (not framebuffer fetch)
// Reads input via SAMPLE_TEXTURE2D on _MainTex
Shader "Hidden/YARG/MirrorEffect"
{
    Properties
    {
        _YargMirrorStartTime ("Mirror Start Time", Float) = 0.0
        _YargMirrorWipeLength ("Mirror Wipe Length", Float) = 1.0
    }

    HLSLINCLUDE

    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

    // ── Mirror params ──
    float _YargMirrorStartTime;
    float _YargMirrorWipeLength;

    // Mirror mode keywords
    #pragma multi_compile_local _ YARG_MIRROR_LEFT YARG_MIRROR_RIGHT YARG_MIRROR_CLOCK_CCW YARG_MIRROR_NONE

    // ── Mirror UV transformation ──
    float2 YargVenueMirror(float2 uv)
    {
        float elapsedTime = _Time.y - _YargMirrorStartTime;
        float t = saturate(elapsedTime / _YargMirrorWipeLength);

        #if YARG_MIRROR_LEFT
            float mirrorPoint = lerp(1.0, 0.0, t);
            if (uv.x > mirrorPoint)
            {
                uv.x = 1.0 - uv.x;
            }
        #elif YARG_MIRROR_RIGHT
            float mirrorPoint = lerp(0.0, 0.5, t);
            if (uv.x < mirrorPoint)
            {
                uv.x = 2.0 * mirrorPoint - uv.x;
            }
        #elif YARG_MIRROR_CLOCK_CCW
            float startAngle = 0.0;
            float endAngle = 2.0 * 3.14159;
            float currentAngle = lerp(startAngle, endAngle, t);

            float2 centered = uv - float2(0.5, 0.5);
            float pixelAngle = atan2(centered.y, centered.x);
            if (pixelAngle < 0.0)
            {
                pixelAngle += 2.0 * 3.14159;
            }
            if (pixelAngle <= currentAngle)
            {
                uv.x = 1.0 - uv.x;
            }
        #else // YARG_MIRROR_NONE
            if (uv.x < 0.5)
            {
                uv.x = 1.0 - uv.x;
            }
        #endif

        return uv;
    }

    // ── Fragment ──
    half4 Frag(Varyings input) : SV_Target
    {
        float2 uv = input.texcoord.xy;

        // Mirror UV transformation
        float2 uvMirrored = uv;
        if (_YargMirrorStartTime > 0.0)
        {
            uvMirrored = YargVenueMirror(uv);
        }

        // Sample input texture with (possibly mirrored) UV
        half4 col = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearRepeat, uvMirrored, _BlitMipLevel);
        return col;
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "MirrorEffect"
            LOD 100
            ZTest Always ZWrite Off Cull Off

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment Frag
            ENDHLSL
        }
    }
}
