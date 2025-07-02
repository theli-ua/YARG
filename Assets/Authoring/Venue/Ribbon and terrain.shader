// Port of https://www.shadertoy.com/view/MslfRn

// Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License.


Shader "Ribbon and terrain"
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
            #define PI 3.14159265
            
            sampler2D _Yarg_SoundTex;
                    
            float2x2 rz2(float a) {
                float c = cos(a), s = sin(a);
                return float2x2(c, s, -s, c);
            }

            float random(float2 p) {
                return frac(sin(dot(p, float2(12., 78.))) * 4375.);
            }

            float sdBox(float3 p, float3 b) {
                float3 d = abs(p) - b;
                return min(max(max(d.x, d.y), d.z), 0.) + length(max(d, 0.));
            }

            float ribbon(float3 p) {
                p.x += sin(iTime + p.z) * .1;
                p.y += .4 + sin(iTime * 2. + p.z * 3.) * 0.1 - p.z * .1;
                p.xy = mul(rz2(sin(iTime + p.z) * .3), p.xy);
                return sdBox(p, float3(.2, .001, 2.));
            }

            float picket(float3 p) {
                p.z -= 2.;
                p.y += 1.;
                p.yz = mul(rz2(-1.), p.yz);
                return max(length(p.xy) - .1, abs(p.z) - 1.);
            }

            float terrain(float3 p) {
                p.x += 14.;
                float2 q = p.xz * float2(.05, .03);
                float2 f = floor(q);
                float r = sin(q.x * PI) * sin(q.y * PI) * random(f) * 3.;
                float2 s = p.xz * float2(.07, .03) * 10.;
                s.y += iTime * 3.;
                s.x += sin(s.y);
                
                float fft = 0.;
                for (int i = 0; i < 20; ++i) {
                    fft += pow(tex2D(_Yarg_SoundTex, float2(float(i) * .005, .25)).r, 10.);
                }
                
                float r2 = sin(s.x) * sin(s.y) * fft * .3;
                return p.y + 1.5 - r - r2;
            }

            float map(float3 p) {
                float d = ribbon(p);
                d = min(d, picket(p));
                d = min(d, terrain(p));
                return d;
            }
                    
            fixed4 mainImage(vec2 u) {
                vec2 uv = (u - .5 * iResolution.xy) / iResolution.y;
                float a = iTime * .5;
                float3 ro = float3(uv + float2(5., 3.) + float2(cos(a), sin(a)) * .5, -10.);
                float3 rd = float3(uv, 1.);
                float3 mp = ro;
                
                for (int i = 0; i < 50; ++i) {
                    float md = map(mp);
                    if (md < .001) break;
                    mp += md * rd;
                }
                
                return fixed4(length(mp - ro) * .01, length(mp - ro) * .01, length(mp - ro) * .01, 1.0);
            }

            fixed4 frag(v2f _iParam) : SV_Target {
                return mainImage(gl_FragCoord);
            }

            ENDCG
        }
    }    
}
