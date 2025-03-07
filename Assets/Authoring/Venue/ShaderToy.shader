// Port of https://www.shadertoy.com/view/Wt33Wf
// Licensed CC-BY-NC
Shader "ShaderToy"
{
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

			float sun(float2 uv, float battery)
			{
			 	float val = smoothstep(0.3, 0.29, length(uv));
			 	float bloom = smoothstep(0.7, 0.0, length(uv));
			    float cut = 3.0 * sin((uv.y + _Time.y * 0.2 * (battery + 0.02)) * 100.0) 
							+ clamp(uv.y * 14.0 + 1.0, -6.0, 6.0);
			    cut = clamp(cut, 0.0, 1.0);
			    return clamp(val * cut, 0.0, 1.0) + bloom * 0.6;
			}

            float grid(float2 uv, float battery)
            {
                float2 size = float2(uv.y, uv.y * uv.y * 0.2) * 0.01;
                uv += float2(0.0, _Time.y * 4.0 * (battery + 0.05));
                uv = abs(frac(uv) - 0.5);
             	float2 lines = smoothstep(size, float2(0.0, 0.0), uv);
             	lines += smoothstep(size * 5.0, float2(0.0, 0.0), uv) * 0.4 * battery;
                return clamp(lines.x + lines.y, 0.0, 3.0);
            }

			float sdTrapezoid( in float2 p, in float r1, float r2, float he )
            {
                float2 k1 = {r2, he};
                float2 k2 = {r2-r1, 2.0*he};
                p.x = abs(p.x);
                float2 ca = { p.x-min(p.x,(p.y<0.0)?r1:r2), abs(p.y)-he };
                float2 cb = p - k1 + k2*clamp( dot(k1-p,k2)/dot(k2, k2), 0.0, 1.0 );
                float s = (cb.x<0.0 && ca.y<0.0) ? -1.0 : 1.0;
                return s*sqrt( min(dot(ca, ca),dot(cb, cb)) );
            }

            float sdLine( in float2 p, in float2 a, in float2 b )
            {
                float2 pa = p-a, ba = b-a;
                float h = clamp( dot(pa,ba)/dot(ba,ba), 0.0, 1.0 );
                return length( pa - ba*h );
            }

            float sdBox( in float2 p, in float2 b )
            {
                float2 d = abs(p)-b;
                return length(max(d,float2(0, 0))) + min(max(d.x,d.y),0.0);
            }

            float opSmoothUnion(float d1, float d2, float k){
                float h = clamp(0.5 + 0.5 * (d2 - d1) /k,0.0,1.0);
                return lerp(d2, d1 , h) - k * h * ( 1.0 - h);
            }

            float sdCloud(in float2 p, in float2 a1, in float2 b1, in float2 a2, in float2 b2, float w)
            {
                //float lineVal1 = smoothstep(w - 0.0001, w, sdLine(p, a1, b1));
                float lineVal1 = sdLine(p, a1, b1);
                float lineVal2 = sdLine(p, a2, b2);
                float2 ww = float2(w*1.5, 0.0);
                float2 left = max(a1 + ww, a2 + ww);
                float2 right = min(b1 - ww, b2 - ww);
                float2 boxCenter = (left + right) * 0.5;
                //float boxW = right.x - left.x;
                float boxH = abs(a2.y - a1.y) * 0.5;
                //float boxVal = sdBox(p - boxCenter, float2(boxW, boxH)) + w;
                float boxVal = sdBox(p - boxCenter, float2(0.04, boxH)) + w;
                
                float uniVal1 = opSmoothUnion(lineVal1, boxVal, 0.05);
                float uniVal2 = opSmoothUnion(lineVal2, boxVal, 0.05);
                
                return min(uniVal1, uniVal2);
            }

			float fmod(float x, float y)
			{
			  return x - y * floor(x/y);
			}

			float4 mainImage( float2 fragCoord )
            {
                float2 uv = (2.0 * fragCoord.xy - _ScreenParams.xy)/_ScreenParams.y;
                float battery = 1.0;
                //if (iMouse.x > 1.0 && iMouse.y > 1.0) battery = iMouse.y / _ScreenParams.y;
                //else battery = 0.8;
                
                //if (abs(uv.x) < (9.0 / 16.0))
                {
                    // Grid
                    float fog = smoothstep(0.1, -0.02, abs(uv.y + 0.2));
                    float3 col = float3(0.0, 0.1, 0.2);
                    if (uv.y < -0.2)
                    {
                        uv.y = 3.0 / (abs(uv.y + 0.2) + 0.05);
                        uv.x *= uv.y * 1.0;
                        float gridVal = grid(uv, battery);
                        col = lerp(col, float3(1.0, 0.5, 1.0), gridVal);
                    }
                    else
                    {
                        float fujiD = min(uv.y * 4.5 - 0.5, 1.0);
                        uv.y -= battery * 1.1 - 0.51;
                        
                        float2 sunUV = uv;
                        float2 fujiUV = uv;
                        
                        // Sun
                        sunUV += float2(0.75, 0.2);
                        //uv.y -= 1.1 - 0.51;
                        col = float3(1.0, 0.2, 1.0);
                        float sunVal = sun(sunUV, battery);
                        
                        col = lerp(col, float3(1.0, 0.4, 0.1), sunUV.y * 2.0 + 0.2);
                        col = lerp(float3(0.0, 0.0, 0.0), col, sunVal);
                        
                        // fuji
                        float fujiVal = sdTrapezoid( uv  + float2(-0.75+sunUV.y * 0.0, 0.5), 1.75 + pow(uv.y * uv.y, 2.1), 0.2, 0.5);
                        float waveVal = uv.y + sin(uv.x * 20.0 + _Time.y * 2.0) * 0.05 + 0.2;
                        float wave_width = smoothstep(0.0,0.01,(waveVal));
                        
                        // fuji color
                        col = lerp( col, lerp(float3(0.0, 0.0, 0.25), float3(1.0, 0.0, 0.5), fujiD), step(fujiVal, 0.0));
                        // fuji top snow
                        col = lerp( col, float3(1.0, 0.5, 1.0), wave_width * step(fujiVal, 0.0));
                        // fuji outline
                        col = lerp( col, float3(1.0, 0.5, 1.0), 1.0-smoothstep(0.0,0.01,abs(fujiVal)) );
                        //col = lerp( col, float3(1.0, 1.0, 1.0), 1.0-smoothstep(0.03,0.04,abs(fujiVal)) );
                        //col = float3(1.0, 1.0, 1.0) *(1.0-smoothstep(0.03,0.04,abs(fujiVal)));
                        
                        // horizon color
                        col += lerp( col, lerp(float3(1.0, 0.12, 0.8), float3(0.0, 0.0, 0.2), clamp(uv.y * 3.5 + 3.0, 0.0, 1.0)), step(0.0, fujiVal) );
                        
                        // cloud
                        float2 cloudUV = uv;

                        cloudUV.x = fmod(cloudUV.x + _Time.y * 0.1, 4.0) - 2.0;
                        float cloudTime = _Time.y * 0.5;
                        float cloudY = -0.5;
                        float cloudVal1 = sdCloud(cloudUV, 
                                                float2(0.1 + sin(cloudTime + 140.5)*0.1,cloudY), 
                                                float2(1.05 + cos(cloudTime * 0.9 - 36.56) * 0.1, cloudY), 
                                                float2(0.2 + cos(cloudTime * 0.867 + 387.165) * 0.1,0.25+cloudY), 
                                                float2(0.5 + cos(cloudTime * 0.9675 - 15.162) * 0.09, 0.25+cloudY), 0.075);
                        cloudY = -0.6;
                        float cloudVal2 = sdCloud(cloudUV, 
                                                float2(-0.9 + cos(cloudTime * 1.02 + 541.75) * 0.1,cloudY), 
                                                float2(-0.5 + sin(cloudTime * 0.9 - 316.56) * 0.1, cloudY), 
                                                float2(-1.5 + cos(cloudTime * 0.867 + 37.165) * 0.1,0.25+cloudY), 
                                                float2(-0.6 + sin(cloudTime * 0.9675 + 665.162) * 0.09, 0.25+cloudY), 0.075);
                        
                        float cloudVal = min(cloudVal1, cloudVal2);
                        
                        //col = lerp(col, float3(1.0,1.0,0.0), smoothstep(0.0751, 0.075, cloudVal));
                        col = lerp(col, float3(0.0, 0.0, 0.2), 1.0 - smoothstep(0.075 - 0.0001, 0.075, cloudVal));
                        col += float3(1.0, 1.0, 1.0)*(1.0 - smoothstep(0.0,0.01,abs(cloudVal - 0.075)));
                    }

                    col += fog * fog * fog;
                    col = lerp(float3(col.r, col.r, col.r) * 0.5, col, battery * 0.7);

                    // return float4(0.0, 1.0, 0.0 ,1.0);
                    return float4(col.r, col.g, col.b ,1.0);
                }
                //else fragColor = float4(0.0);

                
            }
            
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

            #define FLT_MIN 1.175494351e-38
                    
            fixed4 frag(v2f _iParam) : SV_Target {
                return mainImage(gl_FragCoord);
            }

            ENDCG
        }
    }
}
