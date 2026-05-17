// YARG Venue Post-Processing Shader
// Single combined pass: mirror, scanlines, posterize, trails, vignette
// Reads URP post-processed frame via framebuffer fetch (LOAD_FRAMEBUFFER_X_INPUT)
Shader "Hidden/YARG/VenuePP"
{
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Assets/Art/Shaders/ShaderGraph/Includes/Core.hlsl"

        // Mirror mode keywords
        #pragma multi_compile_local _ YARG_MIRROR_LEFT YARG_MIRROR_RIGHT YARG_MIRROR_CLOCK_CCW YARG_MIRROR_NONE

        // Framebuffer fetch input — reads current framebuffer (URP post-processed output)
        FRAMEBUFFER_INPUT_HALF(0)

        // Previous frame texture for trails effect
        TEXTURE2D(_YargPrevFrame);
        SAMPLER(sampler_LinearClamp);

        // ── Mirror params ──
        float _YargMirrorStartTime;
        float _YargMirrorWipeLength;

        // ── Posterize params ──
        int _YargPosterizeSteps;

        // ── Scanline params ──
        float4 _YargScanlineColor;
        float _YargScanlineSize;
        float _YargScanlineIntensity;
        float _YargScanlineEasingPower;

        // ── Trails params ──
        float _YargTrailLength;

        // ── Vertex ──
        struct Varyings
        {
            float4 positionCS   : SV_POSITION;
            float2 texcoord     : TEXCOORD0;
        };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionCS = float4(input.position.xy, 0.0, 1.0);
            output.texcoord = input.texcoord.xy;
            return output;
        }

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
            t = sign * (1.0 - pow(1.0 - abs(t), _YargScanlineEasingPower));
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
            col = ColorBlend(col, _YargScanlineColor, scanline);
            return col;
        }

        // ── Fragment ──
        half4 Frag(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;

            // Read post-processed frame via framebuffer fetch
            half4 fbColor = LOAD_FRAMEBUFFER_X_INPUT(0, uv);
            half3 col = fbColor.rgb;

            // ── Mirror (UV transformation) ──
            float2 uvMirrored = uv;
            if (_YargMirrorStartTime > 0.0)
            {
                uvMirrored = YargVenueMirror(uv);
                // Re-sample from framebuffer with mirrored UV
                fbColor = LOAD_FRAMEBUFFER_X_INPUT(0, uvMirrored);
                col = fbColor.rgb;
            }

            // ── Posterize ──
            if (_YargPosterizeSteps > 0)
            {
                col = YargPosterize(col);
            }

            // ── Scanlines ──
            if (_YargScanlineSize > 0)
            {
                col = YargScanlines(col, uvMirrored);
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
