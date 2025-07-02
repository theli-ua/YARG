// Port of https://www.shadertoy.com/view/MslfRn

// Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License.


Shader "Synthwave"
{
    Properties
    {
        [NoScaleOffset] _Yarg_SoundTex ("SoundTexture", 2D) = "black" {}
        [NoScaleOffset] _NoiseTex ("Noise", 2D) = "black" {}
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
            #define TAU 6.2831853
            
            sampler2D _Yarg_SoundTex;
            sampler2D _NoiseTex;
            
            struct M {
                float d;
                float3 c;
            };
            
            M m;
            
            void mmin(float d, float3 c) {
                if (d < m.d) {
                    m.d = d;
                    m.c = c;
                }
            }
            
            float2x2 rz2(float a) {
                float c = cos(a), s = sin(a);
                return float2x2(c, s, -s, c);
            }
            
            float amod(float a, float m) {
                return fmod(a, m) - m * .5;
            }
            
            float random(float x) {
                return frac(sin(x * 13. + 4375.));
            }
            
            float height(float2 iuv) {
                return sin(sin(iuv.x + iTime * .1) * sin(iuv.y + iTime * .1) * 5.) * (pow(abs(iuv.x), 2.) * .02 + 0.1);
            }
            
            void map(float3 p) {
                m.d = max(max(p.y, .0), max(p.z - 6., 0.));
                float2 uv = p.xz * 2.;
                uv.y += iTime;
                float2 f = frac(uv) - .5;
                float fft = max(tex2D(_Yarg_SoundTex, float2(0.01, 0.25)).r * 2. - 1.8, 0.005);
                float l = fft / (abs(f.x) * abs(f.y));
                l += .1 * fft / (abs(p.z - 6.));
                m.c = lerp(float3(0.196, 0.003, 0.149), float3(1, 0.019, 0.384), l);
                
                uv = p.xz - .5;
                float2 iuv = floor(uv);
                float2 fuv = frac(uv);
                float h = lerp(
                    lerp(height(iuv + float2(0., 0.)), height(iuv + float2(1., 0.)), fuv.x),
                    lerp(height(iuv + float2(0., 1.)), height(iuv + float2(1., 1.)), fuv.x),
                    fuv.y) - 1.;
                float d = p.y - h;
                d = max(d, abs(p.z - 10.) - 4.);
                float2 vuv = fuv * (1. - fuv);
                float v = vuv.x * vuv.y;
                l = .01 * fft / v;
                mmin(d, float3(0., 0., 1.) * l);
            }
            
            float3 noise(float2 uv) {
                return tex2D(_NoiseTex, uv * .1).rgb;
            }
                    
            fixed4 mainImage(vec2 fragCoord) {
                float2 uv = fragCoord.xy / iResolution.xy;
                float2 v = uv * (1. - uv);
                uv -= .5;
                uv.x *= iResolution.x / iResolution.y;
                
                float2 uvn = uv * 2.5;
                float2 iuvn = floor(uvn) + float2(2., 0.);
                float2 fuvn = frac(uvn);
                float3 nb = lerp(
                    lerp(noise(iuvn + float2(0., 0.)), noise(iuvn + float2(1., 0.)), fuvn.x),
                    lerp(noise(iuvn + float2(0., 1.)), noise(iuvn + float2(1., 1.)), fuvn.x),
                    fuvn.y) * .1;
                float3 c = (float3(0.168, 0, 0.2) * .5 + nb * 3.);
                
                float2 suv = uv;
                suv = mul(rz2(iTime * .02), suv);
                // c *= float3(1. / (1. - smoothstep(0.9, 1., tex2D(_NoiseTex, suv).r)));
                float noiseFactor = 1. / (1. - smoothstep(0.9, 1., tex2D(_NoiseTex, suv).r));
                c *= noiseFactor;
                
                float2 uvc = uv - float2(.4, .2);
                float circle = 1. - smoothstep(.25, .252, length(uvc));
                float raytime = uv.y * 100. + iTime * 2.;
                float thr = -uvc.y * 5. - 1.;
                float rays = step(thr, sin(raytime));
                circle = min(circle, rays);
                float3 csun = lerp(float3(0.968, 0.137, 0.094), float3(1, 0.819, 0.019), uvc.y * 3. + .5);
                c = lerp(c, csun, circle);
                
                float3 ro = float3(0., 2., 0.);
                float3 rd = float3(uv, 1);
                float3 mp = ro;
                rd.yz = mul(rz2(-.2), rd.yz);
                
                int i;
                for (i = 0; i < 50; ++i) {
                    map(mp);
                    if (m.d < .001) break;
                    mp += rd * .5 * m.d;
                }
                if (mp.z < 14.) c = m.c;
                
                c = max(c, 0.);
                float cren = frac(uv.y * 200. + iTime * .5);
                c += (smoothstep(.2, .3, cren) - smoothstep(.7, .8, cren)) * 0.01;
                // c = pow(c, float3(1. / 2.2));
                c = pow(c, float3(1. / 2.2, 1. / 2.2, 1. / 2.2));
                c *= pow(v.x * v.y * 25.0, 0.25);
                
                return fixed4(c, 1.);
            }

            fixed4 frag(v2f _iParam) : SV_Target {
                return mainImage(gl_FragCoord);
            }

            ENDCG
        }
    }    
}
