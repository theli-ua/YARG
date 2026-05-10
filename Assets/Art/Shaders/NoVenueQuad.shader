Shader "Hidden/YARG/NoVenueQuad"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }

        Pass
        {
            Name "NoVenueQuad"
            ZWrite Off ZTest Always Cull Off Blend Off

            HLSLPROGRAM
                #pragma vertex vert
                #pragma fragment frag

                sampler2D _YargPrevFrame;

                struct Attributes
                {
                    float4 positionHCS : POSITION;
                    float2 uv : TEXCOORD0;
                };

                struct Varyings
                {
                    float4 positionCS : SV_POSITION;
                    float2 uv : TEXCOORD0;
                };

                Varyings vert(Attributes input)
                {
                    Varyings output;
                    output.positionCS = float4(input.positionHCS.xy * 2.0, 0.0, 1.0);
                    #if UNITY_UV_STARTS_AT_TOP
                    output.positionCS.y *= -1;
                    #endif
                    output.uv = input.uv;
                    return output;
                }

                half4 frag(Varyings input) : SV_Target
                {
                    return tex2D(_YargPrevFrame, input.uv);
                }
            ENDHLSL
        }
    }

    Fallback Off
}
