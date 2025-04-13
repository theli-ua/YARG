Shader "Discoteq"
{
    Properties
    {
        [NoScaleOffset] _Yarg_SoundTex ("SoundTexture", 2D) = "white" {}
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
                
            #define iResolution _ScreenParams
            #define gl_FragCoord ((_iParam.scrPos.xy/_iParam.scrPos.w) * _ScreenParams.xy)
                
            #include "UnityCG.cginc"

            sampler2D _Yarg_SoundTex;
          
            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f {    
                float4 pos : SV_POSITION;    
                float4 scrPos : TEXCOORD0;   
            };

            v2f vert(appdata_t v)
            {
				v2f OUT;
                // Expects you're using the default Unity quad
                // this makes it cover whole screen/camera
                float4 pos = float4(v.vertex.xy * 2.0, 0.0, 1.0);
                #if UNITY_REVERSED_Z
                pos.z = 0.000001;
                #else
                pos.z = 0.999999;
                #endif
                    
                OUT.pos = pos;
                OUT.scrPos = ComputeScreenPos(pos);
                
                return OUT;
            }
            

            #define S(a, b, t) smoothstep(a, b, t)
            static const float NUM_LINES = 20.0;
            
            float4 Line(float vu0, float t, float2 uv, float speed, float height, float3 col)
            {
                float ti = 1.0 - t;
                float vu = (tex2D(_Yarg_SoundTex, float2(ti, 0.25)).x) * ti;
                
                float b = S(1.0, 0.0, abs(uv.x)) * sin(_Time.y * speed + uv.x * height * t) * 0.2;
                uv.y += b * 2.0 * (vu0 * 1.0 + 0.3);
                uv.x += vu * 12.0 - 2.0;
                
                return float4(S(0.06 * S(0.2, 0.9, abs(uv.x)), 0.0, abs(uv.y) - 0.004) * col, 1.0) * S(1.0, 0.3, abs(uv.x));
            }
            
            fixed4 frag (v2f _iParam) : SV_Target
            {
                // float2 uv = (i.uv * 2.0 - 1.0) * float2(1.0, _ScreenParams.y / _ScreenParams.x);
                // float2 uv = gl_FragCoord;
                
                float2 uv = (gl_FragCoord - .5 * iResolution.xy) / iResolution.y;
                
                // float2 uv = (gl_FragCoord * 2.0 - 1.0) * float2(1.0, _ScreenParams.y / _ScreenParams.x);
                float4 O = float4(0, 0, 0, 0);
                
                // Get average audio volume 
                float vu0 = (
                    tex2D(_Yarg_SoundTex, float2(0.1, 0.25)).x +
                    tex2D(_Yarg_SoundTex, float2(0.2, 0.25)).x +
                    tex2D(_Yarg_SoundTex, float2(0.4, 0.25)).x +
                    tex2D(_Yarg_SoundTex, float2(0.6, 0.25)).x +
                    tex2D(_Yarg_SoundTex, float2(0.7, 0.25)).x +
                    tex2D(_Yarg_SoundTex, float2(0.9, 0.25)).x
                ) / 6.0;
                
                for (float i = 0.0; i <= NUM_LINES; i += 1.0)
                {
                    float t = i / NUM_LINES;
                    float c = (vu0 - t) + 0.3;
                    
                    O += Line(vu0, t, uv, 1.0 + t, 4.0 + t, float3(0.2 + c * 0.7, 0.2 + c * 0.4, 0.3)) * 2.0;
                }
                
                return O;
            }
            
            ENDCG
        }
    }
}
