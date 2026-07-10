Shader "HighwaysAlphaMask"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "UniversalMaterialType"="Unlit" }

        Pass
        {
            Tags { "LightMode"="UniversalForward" }

            ZWrite Off
            Cull Off
            ColorMask A
            Blend One One
            BlendOp Min

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            // Required so override-material draws through BRG/Hybrid Batch Group load
            // per-instance unity_ObjectToWorld from the batch GPU buffer.
            #pragma multi_compile _ DOTS_INSTANCING_ON

            // Core.hlsl → Input.hlsl already includes UniversalDOTSInstancing.hlsl, which
            // rebinds unity_ObjectToWorld / UNITY_MATRIX_M to BRG batch metadata when
            // DOTS_INSTANCING_ON is active. No extra DOTS include needed here.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Art/Shaders/highways.hlsl"

            StructuredBuffer<float> _YargFadeParams;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                // Critical for BRG: without this, TransformObjectToWorld reads the wrong
                // (or default) instance and alpha is written at incorrect screen positions.
                // MeshRenderers ignore instance setup — still safe.
                UNITY_SETUP_INSTANCE_ID(IN);

                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = YargTransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // positionWS from object matrix is rest-Z for BRG; scroll for live fade distance.
                float3 positionWS = YargApplyDotsScroll(IN.positionWS);
                int index = WorldPosToIndex(positionWS);
                float fadeStartPos = _YargFadeParams[index * 2];
                float fadeEndPos   = _YargFadeParams[index * 2 + 1];
                // Distance along highway Z from camera (matches prior fade convention)
                float3 camPos = YargWorldSpaceCameraPos(positionWS);
                float dist = positionWS.z - camPos.z;
                float alpha = 0.0;

                if (dist < fadeStartPos)
                {
                    alpha = 1.0;
                }
                else if (dist > fadeEndPos)
                {
                    alpha = 0.0;
                }
                else
                {
                    float rate = 1.0 / (fadeEndPos - fadeStartPos);
                    float fadeValue = (dist - fadeStartPos) * rate;
                    alpha = 1.0 - smoothstep(0.0, 1.0, fadeValue);
                }

                // Write alpha into A channel; Min blend with clear=1 keeps empty areas at 1.0
                return half4(0, 0, 0, alpha);
            }
            ENDHLSL
        }
    }
}
