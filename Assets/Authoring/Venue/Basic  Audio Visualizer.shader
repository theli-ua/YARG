// Port of https://www.shadertoy.com/view/MslfRn

// Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License.


Shader "Basic Audio Visualizer"
{
    Properties
    {
        [NoScaleOffset] _Yarg_SoundTex ("SoundTexture", 2D) = "black" {}
    }
    

    SubShader
     {
        Pass
        {
            ColorMask RGB

            // We don't want this to be culled
            Cull Off

            ZWrite On
            ZTest Off
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
                
                
            #include "UnityCG.cginc"
            #include "ShaderToy.cginc"
            

            #define FLT_MIN 1.175494351e-38
            #define time iTime
            
            sampler2D _Yarg_SoundTex;
            
            float noise3D(float3 p)
            {
                return frac(sin(dot(p, float3(12.9898, 78.233, 12.7378))) * 43758.5453) * 2.0 - 1.0;
            }
            
            float3 mixc(float3 col1, float3 col2, float v)
            {
                v = clamp(v, 0.0, 1.0);
                return col1 + v * (col2 - col1);
            }
                    
            fixed4 mainImage(vec2 fragCoord) {
                float2 uv = fragCoord.xy / iResolution.xy;
                float2 p = uv * 2.0 - 1.0;
                p.x *= iResolution.x / iResolution.y;
                p.y += 0.5;
                
                float3 col = float3(0.0, 0.0, 0.0);
                float3 ref = float3(0.0, 0.0, 0.0);
                
                float nBands = 64.0;
                float i = floor(uv.x * nBands);
                float f = frac(uv.x * nBands);
                float band = i / nBands;
                band *= band * band;
                band = band * 0.995;
                band += 0.005;
                float s = tex2D(_Yarg_SoundTex, float2(band, 0.25)).x;
                
                /* Gradient colors and amount here */
                const int nColors = 4;
                float3 colors[4];
                colors[0] = float3(0.0, 0.0, 1.0);
                colors[1] = float3(0.0, 1.0, 1.0);
                colors[2] = float3(1.0, 1.0, 0.0);
                colors[3] = float3(1.0, 0.0, 0.0);
                
                float3 gradCol = colors[0];
                float n = float(nColors) - 1.0;
                for (int j = 1; j < nColors; j++)
                {
                    gradCol = mixc(gradCol, colors[j], (s - float(j - 1) / n) * n);
                }
                
                col += float3(1.0 - smoothstep(0.0, 0.01, p.y - s * 1.5), 
                             1.0 - smoothstep(0.0, 0.01, p.y - s * 1.5), 
                             1.0 - smoothstep(0.0, 0.01, p.y - s * 1.5));
                col *= gradCol;
                
                ref += float3(1.0 - smoothstep(0.0, -0.01, p.y + s * 1.5),
                             1.0 - smoothstep(0.0, -0.01, p.y + s * 1.5),
                             1.0 - smoothstep(0.0, -0.01, p.y + s * 1.5));
                ref *= gradCol * smoothstep(-0.5, 0.5, p.y);
                
                col = lerp(ref, col, smoothstep(-0.01, 0.01, p.y));
                
                col *= smoothstep(0.125, 0.375, f);
                col *= smoothstep(0.875, 0.625, f);
                
                col = clamp(col, 0.0, 1.0);
                
                float dither = noise3D(float3(p, time)) * 2.0 / 256.0;
                col += dither;
                
                return fixed4(col, 1.0);
            }

            fixed4 frag(v2f _iParam) : SV_Target {
                return mainImage(gl_FragCoord);
            }

            ENDCG
        }
    }   
}
