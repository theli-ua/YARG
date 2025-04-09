#define iResolution _ScreenParams
#define gl_FragCoord ((_iParam.scrPos.xy/_iParam.scrPos.w) * _ScreenParams.xy)
#define vec2 float2
#define vec3 float3
#define vec4 float4
#define mat2 float2x2
#define mat3 float3x3
#define fract frac
#define iTime _Time.y
#define atan atan2
#define mix lerp
#define texture tex2D
#define iChannel0 _MainTex

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
