// Port of Shadertoy audio-reactive shader to Unity

Shader "Sound Room 2_1"
{
    Properties
    {
        [NoScaleOffset] _Yarg_SoundTex ("SoundTexture", 2D) = "black" {}
        _PreviousFrame ("Previous Frame", 2D) = "white" {}
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
            #include "UnityCG.cginc"
            #include "UnityCustomRenderTexture.cginc"

            #include "ShaderToy.cginc"

            // Properties
            sampler2D _Yarg_SoundTex;
            sampler2D _PreviousFrame;
            
            float avgFrq(float from, float to)
            {
                float st = (to - from) / 3.0;
                float s = tex2D(_Yarg_SoundTex, float2(from, 0.25)).x +
                         tex2D(_Yarg_SoundTex, float2(from + st, 0.25)).x +
                         tex2D(_Yarg_SoundTex, float2(from + st * 2.0, 0.25)).x +
                         tex2D(_Yarg_SoundTex, float2(from + st * 3.0, 0.25)).x;
                return s * 0.25;
            }

            fixed4 mainImage(vec2 uv)
            {
                int2 p = int2(uv * 4);
                float4 col = float4(0.0, 0.0, 0.0, 0.0);
                
                if(p.x == 0)
                {
                    col = tex2D(_PreviousFrame, uv);
                    float f = avgFrq(0.0, 0.1);
                    float delta = f - col.a;
                    
                    if(f > 0.6 && delta > 0.03)
                    {
                        col.x = 1.0 - col.x;
                        col.y = delta - 0.03;
                        col.a = f;
                    }
                    else
                    {
                        col.a -= 0.2 *  unity_DeltaTime.x;
                    }
                    col.z = f;
                }
                else if(p.x == 1)
                {
                    col = tex2D(_PreviousFrame, uv);
                    float f = avgFrq(0.4, 0.6);
                    float delta = f - col.a;
                    
                    if(f > 0.4 && delta > 0.02)
                    {
                        col.x = 1.0 - col.x;
                        col.y = delta - 0.02;
                        col.a = f;
                    }
                    else
                    {
                        col.a -= 0.3 *  unity_DeltaTime.x;
                    }
                }
                else if(p.x == 2)
                {
                    col = tex2D(_PreviousFrame, uv);
                    float f = avgFrq(0.6, 0.8);
                    float delta = f - col.a;
                    
                    if(f > 0.4 && delta > 0.02)
                    {
                        col.x = 1.0 - col.x;
                        col.y = delta - 0.02;
                        col.a = f;
                    }
                    else
                    {
                        col.a -= 0.3 *  unity_DeltaTime.x;
                    }
                }
                else if(p.x == 3)
                {
                    col = tex2D(_PreviousFrame, uv);
                    float f = avgFrq(0.9, 1.0);
                    float delta = f - col.a;
                    
                    if(f > 0.2 && delta > 0.08)
                    {
                        col.x = 1.0 - col.x;
                        col.y = delta - 0.08;
                        col.a = f;
                    }
                    else
                    {
                        col.a -= 1.0 *  unity_DeltaTime.x;
                    }
                }
                
                return col;
            }

            float4 frag(v2f_init_customrendertexture IN) : COLOR
            {
                return mainImage(IN.texcoord);
            }

            ENDCG
        }
    }
}
