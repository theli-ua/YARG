// Minimal DOTS Instancing compatible unlit shader for BRG testing
// Reads unity_ObjectToWorld, unity_WorldToObject, _BaseColor from BRG buffer
Shader "YARG/NoteBRGUnlit"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float2 uv           : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv           : TEXCOORD0;
                float4 positionCS   : SV_POSITION;
            };

            // DOTS instancing: read per-instance properties from BRG buffer
            struct DOTSInstancingData
            {
                float3 objectToWorld0;
                float3 objectToWorld1;
                float3 objectToWorld2;
                float3 objectToWorld3;
                float3 worldToObject0;
                float3 worldToObject1;
                float3 worldToObject2;
                float3 worldToObject3;
                float4 baseColor;
            };

            #if DOTS_INSTANCING_ON
                UNITY_DECLARE_DOTS_INSTANCED_ARRAY_PROP(DOTSInstancingData, unity_DOTSInstanceData);
                UNITY_DECLARE_DOTS_INSTANCED_PROP(float3, objectToWorld0);
                UNITY_DECLARE_DOTS_INSTANCED_PROP(float3, objectToWorld1);
                UNITY_DECLARE_DOTS_INSTANCED_PROP(float3, objectToWorld2);
                UNITY_DECLARE_DOTS_INSTANCED_PROP(float3, objectToWorld3);
                UNITY_DECLARE_DOTS_INSTANCED_PROP(float4, baseColor);
            #endif

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);

                // Build object-to-world matrix from DOTS instanced columns
                #if DOTS_INSTANCING_ON
                    float3 ow0 = UNITY_ACCESS_DOTS_INSTANCED_PROP(float3, objectToWorld0);
                    float3 ow1 = UNITY_ACCESS_DOTS_INSTANCED_PROP(float3, objectToWorld1);
                    float3 ow2 = UNITY_ACCESS_DOTS_INSTANCED_PROP(float3, objectToWorld2);
                    float3 ow3 = UNITY_ACCESS_DOTS_INSTANCED_PROP(float3, objectToWorld3);
                    float4x4 objectToWorld = float4x4(
                        ow0.x, ow1.x, ow2.x, ow3.x,
                        ow0.y, ow1.y, ow2.y, ow3.y,
                        ow0.z, ow1.z, ow2.z, ow3.z,
                        0,     0,     0,     1
                    );
                    float4 instanceColor = UNITY_ACCESS_DOTS_INSTANCED_PROP(float4, baseColor);
                #else
                    float4x4 objectToWorld = unity_ObjectToWorld;
                    float4 instanceColor = _BaseColor;
                #endif

                // Transform position
                float3 positionWS = mul(objectToWorld, float4(input.positionOS, 1.0)).xyz;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                #if DOTS_INSTANCING_ON
                    float3 ow0 = UNITY_ACCESS_DOTS_INSTANCED_PROP(float3, objectToWorld0);
                    float3 ow1 = UNITY_ACCESS_DOTS_INSTANCED_PROP(float3, objectToWorld1);
                    float3 ow2 = UNITY_ACCESS_DOTS_INSTANCED_PROP(float3, objectToWorld2);
                    float3 ow3 = UNITY_ACCESS_DOTS_INSTANCED_PROP(float3, objectToWorld3);
                    float4x4 objectToWorld = float4x4(
                        ow0.x, ow1.x, ow2.x, ow3.x,
                        ow0.y, ow1.y, ow2.y, ow3.y,
                        ow0.z, ow1.z, ow2.z, ow3.z,
                        0,     0,     0,     1
                    );
                    float4 instanceColor = UNITY_ACCESS_DOTS_INSTANCED_PROP(float4, baseColor);
                #else
                    float4x4 objectToWorld = unity_ObjectToWorld;
                    float4 instanceColor = _BaseColor;
                #endif

                float4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                return float4(texColor.rgb * instanceColor.rgb, instanceColor.a);
            }
            ENDHLSL
        }
    }
}
