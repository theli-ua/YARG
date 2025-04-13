Shader "MatrixAA"
{
    Properties
    {
        [NoScaleOffset] _Yarg_AlbumCover ("ALbumArt", 2D) = "white" {}

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
#define vec2 float2
#define vec3 float3
#define vec4 float4
#define mat2 float2x2
#define mat3 float3x3
#define ivec3 int3
#define ivec2 int2
#define fract frac
#define iTime _Time.y
#define atan atan2
#define mix lerp
#define texture tex2D
#define iChannel0 _Yarg_AlbumCover               
    // #define mod fmod
            #include "UnityCG.cginc"

            static const float _RainSpeed = 1.75;
            static const float _DropSize = 3.0;
            sampler2D _Yarg_AlbumCover;
          
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

float rand(vec2 co){
    return fract(sin(dot(co.xy ,vec2(12.9898,78.233))) * 43758.5453);
}

float mod(float x, float y)
{
  return x - y * floor(x/y);
}   
float rchar(vec2 outer, vec2 inner, float globalTime) {
	//return float(rand(floor(inner * 2.0) + outer) > 0.9);
	
	vec2 seed = floor(inner * 4.0) + outer.y;
	if (rand(vec2(outer.y, 23.0)) > 0.98) {
		seed += floor((globalTime + rand(vec2(outer.y, 49.0))) * 3.0);
	}
	
	return float(rand(seed) > 0.5);
}
            
            fixed4 frag (v2f _iParam) : SV_Target
            {
                float2 fragCoord = gl_FragCoord;

	vec2 position = fragCoord.xy / iResolution.xy;
	vec2 uv = vec2(position.x, 1.0-position.y);
    position.x /= iResolution.x / iResolution.y;
	float globalTime = iTime * _RainSpeed;
	
	float scaledown = _DropSize;
	float rx = fragCoord.x / (40.0 * scaledown);
	float mx = 40.0*scaledown*fract(position.x * 30.0 * scaledown);
	vec4 result;
	
	if (mx > 12.0 * scaledown) {
		result = vec4(0.0, 0.0, 0.0, 0.0);
	} else 
	{
        float x = floor(rx);
		float r1x = floor(fragCoord.x / (15.0));
		

		float ry = position.y*600.0 + rand(vec2(x, x * 3.0)) * 100000.0 + globalTime* rand(vec2(r1x, 23.0)) * 120.0;
		float my = mod(ry, 15.0);
		if (my > 12.0 * scaledown) {
			result = vec4(0.0, 0.0, 0.0, 0.0);
		} else {
		
			float y = floor(ry / 15.0);
			
			float b = rchar(vec2(rx, floor((ry) / 15.0)), vec2(mx, my) / 12.0, globalTime);
			float col = max(mod(-y, 24.0) - 4.0, 0.0) / 20.0;
			vec3 c = col < 0.8 ? vec3(0.0, col / 0.8, 0.0) : mix(vec3(0.0, 1.0, 0.0), vec3(1.0, 1.0,1.0), (col - 0.8) / 0.2);
			
			result = vec4(c * b, 1.0)  ;
		}
	}
	
	position.x += 0.05;

	scaledown = _DropSize;
	rx = fragCoord.x / (40.0 * scaledown);
	mx = 40.0*scaledown*fract(position.x * 30.0 * scaledown);
	
	if (mx > 12.0 * scaledown) {
		result += vec4(0.0,0.0,0.0,0.0);
	} else 
	{
        float x = floor(rx);
		float r1x = floor(fragCoord.x / (12.0));
		

		float ry = position.y*700.0 + rand(vec2(x, x * 3.0)) * 100000.0 + globalTime* rand(vec2(r1x, 23.0)) * 120.0;
		float my = mod(ry, 15.0);
		if (my > 12.0 * scaledown) {
			result += vec4(0.0,0.0,0.0,0.0);
		} else {
		
			float y = floor(ry / 15.0);
			
			float b = rchar(vec2(rx, floor((ry) / 15.0)), vec2(mx, my) / 12.0, globalTime);
			float col = max(mod(-y, 24.0) - 4.0, 0.0) / 20.0;
			vec3 c = col < 0.8 ? vec3(0.0, col / 0.8, 0.0) : mix(vec3(0.0, 1.0, 0.0), vec3(1.0,1.0,1.0), (col - 0.8) / 0.2);
			
			result += vec4(c * b, 1.0)  ;
		}
	}
	
	result = result * length(texture(iChannel0,uv).rgb) + 0.22 * vec4(0.,texture(iChannel0,uv).g,0.,1.);
	if(result.b < 0.5)
	result.b = result.g * 0.5 ;
	return result;
            }
            ENDCG
        }
    }
}
