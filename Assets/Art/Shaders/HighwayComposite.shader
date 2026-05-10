Shader "Hidden/YARG/HighwayComposite"
{
    HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        half4 FragHighwayComposite(Varyings input) : SV_Target
        {
            half4 highwayColor = FragBlit(input, sampler_LinearClamp);
            return highwayColor;
        }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "HighwayComposite"
            ZWrite Off ZTest Always Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragHighwayComposite
            ENDHLSL
        }
    }

    Fallback Off
}
