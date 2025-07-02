Shader "Sound Room 2 3"
{
    Properties
    {
        _MainTex ("Source Texture", 2D) = "white" {}
        _BlurRadius ("Blur Radius", Float) = 1.0
    }
    
    SubShader
    {
        Pass
        {
            ColorMask RGBA
            Cull Off
            ZWrite On
            ZTest Off
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
                
            #include "UnityCG.cginc"
            #include "ShaderToy.cginc"

            // Properties
            sampler2D _MainTex;
            float _BlurRadius;

            fixed4 mainImage(vec2 fragCoord)
            {
                vec2 uv = fragCoord.xy / iResolution.xy;
                float4 total = float4(0.0, 0.0, 0.0, 0.0);
                float r = _BlurRadius;
                float k = 0.0;
                
                for(float x = -r; x <= r; x++)
                {
                    for(float y = -r; y <= r; y++)
                    {
                        total += tex2D(_MainTex, (fragCoord + float2(x, y)) / iResolution.xy);
                        k++;
                    }
                }
                
                return total / k;
            }

            fixed4 frag(v2f _iParam) : SV_Target
            {
                return mainImage(gl_FragCoord.xy);
            }

            ENDCG
        }
    }
}

