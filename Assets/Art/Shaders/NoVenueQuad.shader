Shader "Hidden/YARG/NoVenueQuad"
{
    HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        half4 FragNoVenueQuad(Varyings input) : SV_Target
        {
            return FragBlit(input, sampler_LinearClamp);
        }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "NoVenueQuad"
            ZWrite Off ZTest Always Cull Off Blend Off

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragNoVenueQuad
            ENDHLSL
        }
    }

    Fallback Off
}
