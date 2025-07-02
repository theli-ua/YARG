// Port of Shadertoy path tracing audio-reactive shader to Unity
// Note: Requires external setup for audio data

Shader "Sound Room 2 2"
{
    Properties
    {
        _AudioTex ("Audio Texture", 2D) = "black" {}
        _Samples ("Samples", Int) = 20
        _Bounces ("Bounces", Int) = 4
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
            sampler2D _AudioTex;
            float4 _AudioTex_TexelSize;
            int _Samples;
            int _Bounces;
            
            #define PI 3.14159265359
            #define LOCATION 0
            #define FORWARD 1
            #define SAMPLES 20
            #define BOUNCES 4

            // Global variables
            float seed;
            float2 uv;
            float bass;

            struct Ray
            {
                float3 o;
                float3 d;
            };

            struct Hit
            {
                float3 p;
                float d;
                float3 n;
                bool inside;
                bool enabled;
                int m;
            };

            struct Sphere
            {
                float3 o;
                float r;
                int m;
            };

            struct Wall
            {
                float3 o;
                float3 n;
                int m;
            };

            struct Material
            {
                float3 c;
                float r;
                float b;
                float rfl;
            };

            // Material array - we'll initialize this in init()
            Material mats[7];

            bool isLit(int p, float v, float mul, float add, float div, float shift)
            {
                float4 val = tex2D(_AudioTex, float2((float(p) + 0.5) * _AudioTex_TexelSize.x, 0));
                
                return frac(mul * v * (val.x - 0.5) / (1.0 + val.y * div) + shift * val.a) + add > 0.5;
            }

            float3 getColor(Hit h)
            {
                int i = h.m;
                
                if(i == 1 && isLit(0, h.p.y + floor(h.p.x * fmod(iTime * 0.3, 2.0) * 0.5), 50.0, -0.2, 5300.0, 1.0))
                {
                   i = 3; 
                }
                
                if(i == 2 && isLit(0, h.p.x + 0.5, 3.0, -0.3, 1000.0, 0.3))
                {
                   i = 3; 
                }
                
                if(i == 0)
                {
                    if(isLit(2, h.p.y * 8.0 / bass, 3.0, -0.3, 1000.0, 0.0))
                        i = 6;
                    else if(isLit(3, h.p.y * 8.0 / bass, 3.0, -0.3, 1000.0, 0.0))
                        i = 5;
                }
                
                return mats[i].c * mats[i].b;
            }

            void init()
            {
                bass = tex2D(_AudioTex, float2(0, 0)).z;
                bass = 1.5 * smoothstep(0.6, 0.9, bass) + 0.5;
                seed = (uv.y + iTime * 0.523413187) * sqrt(uv.x * 0.77777777 * iTime);
                
                // Initialize materials
                float refl = 0.4;
                
                mats[0].c = float3(0.5, 0.5, 0.5);
                mats[0].r = 0.01;
                mats[0].b = 0.0;
                
                mats[1].c = float3(refl, refl, refl);
                mats[1].r = 0.05;
                mats[1].b = 0.0;
                
                mats[2].c = float3(refl, refl, refl);
                mats[2].r = 0.05;
                mats[2].b = 0.0;
                
                mats[3].c = float3(1.0, 1.0, 1.0);
                mats[3].r = 0.3;
                mats[3].b = 1.5;
                
                mats[4].c = float3(refl, refl, refl);
                mats[4].r = 0.05;
                mats[4].b = 0.0;
                
                mats[5].c = float3(refl, refl * 0.4, refl * 0.2);
                mats[5].r = 0.15;
                mats[5].b = 3.0;
                
                mats[6].c = float3(refl * 0.2, refl * 0.3, refl);
                mats[6].r = 0.15;
                mats[6].b = 2.0;
            }

            // Custom random function
            float rand() 
            { 
                float2 s = uv;
                float n = frac(sin(seed += 1.0) * 43758.5453123);
                return frac(n + frac(sin(dot(float2(n * s.y, s.x) * 0.123, float2(12.9898, 78.233))) * 43758.5453));
            }

            // Returns a random unit vector inside the given hemisphere
            float3 rndDirHemisphere(float3 n)
            {
                float r2 = rand();
                float phi = 2.0 * PI * rand();
                float sina = sqrt(r2);
                float cosa = sqrt(1.0 - r2);
                float3 w = normalize(n);
                float3 u = normalize(cross(w.yzx, w));
                float3 v = cross(w, u);
                return normalize((u * cos(phi) + v * sin(phi)) * sina + w * cosa);
            }

            Ray makeView(float3 location, float3 forward, float2 fragCoord, float camSize, float fov)
            {
                // Create relative direction vectors
                forward = normalize(forward);
                float3 right = -normalize(cross(forward, float3(0, 1, 0)));
                float3 up = -normalize(cross(right, forward));
                
                // Create camera plane vector (absolute)
                float2 vp = (fragCoord  - float2(0.5, 0.5));
                // vp.y *= iResolution.y / iResolution.x;
                vp.y *= 1280 / 720;
                
                Ray r;
                // Relative camera plane
                r.o = location + (right * vp.x + up * vp.y) * camSize;
                
                // Create ray through camera plane by calculating distance of the focal point through given fov in angles
                float phi = PI * (90.0 - fov * 0.5) / 180.0;
                float h = tan(phi) * camSize;
                r.d = normalize(r.o - (location - forward * h));
                
                return r;
            }

            // Solver for quadratic functions
            bool solveQuadratic(float a, float b, float c, out float x0, out float x1)
            {
                float discr = b * b - 4.0 * a * c;
                if (discr < 0.0) return false;
                else if (discr == 0.0) 
                {
                    x0 = x1 = -0.5 * b / a;
                }
                else 
                {
                    float q = (b > 0.0) ?
                        -0.5 * (b + sqrt(discr)) :
                        -0.5 * (b - sqrt(discr));
                    x0 = q / a;
                    x1 = c / q;
                }
                
                if (x0 > x1)
                {
                    float tmp = x0;
                    x0 = x1;
                    x1 = tmp;
                }

                return true;
            }

            bool hitWall(Ray r, Wall w, out Hit p)
            {
                // Initialize all members of the output struct
                p.p = float3(0, 0, 0);
                p.d = 0.0;
                p.n = float3(0, 0, 0);
                p.inside = false;
                p.enabled = false;
                p.m = 0;
                
                float d = dot(r.d, w.n);
                if(d == 0.0)
                    return false;
                
                p.d = dot(w.o - r.o, w.n) / d;
                if(p.d < 0.0)
                    return false;
                    
                p.p = r.o + r.d * p.d;
                p.n = w.n;
                
                if(dot(r.d, w.n) >= 0.0)
                {
                    p.inside = true;
                    p.n = -p.n;
                }
                else
                {
                    p.inside = false;
                }
                
                p.m = w.m;
                p.enabled = true;
                return true;
            }

            // Hit test for spheres
            bool hitSphere(Ray r, Sphere s, out Hit p)
            {
                // Initialize all members of the output struct
                p.p = float3(0, 0, 0);
                p.d = 0.0;
                p.n = float3(0, 0, 0);
                p.inside = false;
                p.enabled = false;
                p.m = 0;
                
                float3 L = r.o - s.o;
                float a = dot(r.d, r.d);
                float b = 2.0 * dot(r.d, L);
                float c = dot(L, L) - s.r * s.r;
                float t0, t1;
                
                if (!solveQuadratic(a, b, c, t0, t1)) return false;
                
                if(t0 < 0.0)
                {
                    t0 = t1;
                }
                if(t0 < 0.0)
                {
                    return false;
                }
                
                p.d = t0;
                p.p = r.o + r.d * t0;
                p.n = normalize(p.p - s.o);
                
                if(length(L) < s.r)
                {
                    p.inside = true;
                    p.n = -p.n;
                }
                else
                {
                    p.inside = false;
                }
                
                p.m = s.m;
                p.enabled = true;
                return true;
            }

            float3 demoTrace(Ray r)
            {
                // Initialize geometry
                Sphere s;
                s.o = float3(0.0, 0.5, -0.5);
                s.r = bass;
                s.m = 0;
                
                Wall w1;
                w1.o = float3(0.0, -1.0001, 0.0);
                w1.n = float3(0.0, 1.0, 0.0);
                w1.m = 2;
                
                Wall w2;
                w2.o = float3(-5.0, 0.0, 0.0);
                w2.n = float3(1.0, 0.0, 0.0);
                w2.m = 4;
                
                Wall w3;
                w3.o = float3(5.0, 0.0, 0.0);
                w3.n = float3(-1.0, 0.0, 0.0);
                w3.m = 4;
                
                Wall w4;
                w4.o = float3(0.0, 0.0, 1.05);
                w4.n = float3(0.0, 0.0, -1.0);
                w4.m = 1;
                
                float3 finalColor = float3(0.0, 0.0, 0.0);
                float jitter = 0.001;
                
                for(int k = 0; k < SAMPLES; k++)
                {
                    Hit h;
                    Ray ray;
                    ray.o = r.o;
                    ray.d = rndDirHemisphere(r.d) * jitter + r.d * (1.0 - jitter);
                    float3 fltr = float3(1.0, 1.0, 1.0);
                    
                    for(int i = 0; i < BOUNCES; i++)
                    {
                        Hit hit;
                        h.d = 999999.9;
                        h.enabled = false;
                        
                        if(hitSphere(ray, s, hit))
                        {
                            h = hit;
                            h.enabled = true;
                        }
                        
                        if(hitWall(ray, w1, hit) && hit.d < h.d)
                        {
                            h = hit;
                            h.enabled = true;
                        }
                        
                        if(hitWall(ray, w2, hit) && hit.d < h.d)
                        {
                            h = hit;
                            h.enabled = true;
                        }
                        
                        if(hitWall(ray, w3, hit) && hit.d < h.d)
                        {
                            h = hit;
                            h.enabled = true;
                        }
                        
                        if(hitWall(ray, w4, hit) && hit.d < h.d)
                        {
                            h = hit;
                            h.enabled = true;
                        }
                        
                        if(h.enabled)
                        {
                            float nrm = 1.0 - dot(ray.d, -h.n);
                            nrm = 0.4 * nrm + 0.6;
                            ray.o = h.p + h.n * 0.0001;
                            ray.d = reflect(ray.d, h.n);
                            ray.d = rndDirHemisphere(ray.d) * mats[h.m].r + ray.d * (1.0 - mats[h.m].r);
                            finalColor += fltr * getColor(h);
                            fltr *= mats[h.m].c * nrm;
                        }
                        else
                            break;
                    }
                }
                
                return finalColor / float(SAMPLES);
            }

            fixed4 mainImage(vec2 fragCoord)
            {
                uv = fragCoord;
                init();
                
                Ray view = makeView(float3(2.0, 4.0, -12.0), float3(-0.2, -0.3, 1), fragCoord, 0.1, 90.0);
                
                return fixed4(demoTrace(view), 1.0);
            }

            float4 frag(v2f_init_customrendertexture IN) : COLOR
            {
                return mainImage(IN.texcoord);
            }

            ENDCG
        }
    }
}
