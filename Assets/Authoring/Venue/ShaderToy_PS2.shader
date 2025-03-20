Shader "ShaderToy_PS2"
{
    Properties
    {
        [NoScaleOffset] _MainTex ("MainTexture", 2D) = "white" {}
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
            #define fract frac
            #define iTime _Time.y
            #define atan atan2
            #define mix lerp
            #define texture tex2D
            #define iChannel0 _MainTex
                
            #include "UnityCG.cginc"
            sampler2D _MainTex;

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
#define MAX_STEPS 1000
#define MAX_DIST 100.
#define SURF_DIST .001
#define SURF_BIAS 1.0;
#define TAU 6.283185
#define PI 3.141592

#define LIGHT_RADIUS 2.

#define SCENE_OBJ_COUNT 5

mat2 Rot(float a) {
    float s=sin(a), c=cos(a);
    return mat2(c, -s, s, c);
}

float sdBox(vec3 p, vec3 s) {
    p = abs(p)-s;
	return length(max(p, 0.))+min(max(p.x, max(p.y, p.z)), 0.);
}

float Hash21(vec2 p)
{
    p = fract(p*vec2(443.53, 331.13));
    p += dot(p, p+2444.123);
    return fract(p.x*p.y);
}

struct SDF {
  float dist;
  int materialID;
};

SDF _SDF(float dist, int materialID)
{
        SDF a;
        a.dist = dist;
        a.materialID = materialID;
        return a;
}

SDF minSDF(SDF scene[SCENE_OBJ_COUNT])
{
    SDF min_s = scene[0];
    for(int i = 1; i < SCENE_OBJ_COUNT; i++)
        if(scene[i].dist < min_s.dist)
            min_s = scene[i];
            
    return min_s;
}

vec2 pillar_id;
SDF GetDist(vec3 p, vec3 rd) {
    SDF scene[SCENE_OBJ_COUNT]; 
    
    //Glass Cubes
    vec3 new_position = p-vec3(-1.,8.0,2.);
    // new_position.xz *= Rot(iTime*0.1);
    new_position.xz = mul(Rot(iTime*0.1), new_position.xz );
    // new_position.zy *= Rot(iTime*0.1);
    new_position.zy = mul(Rot(iTime*0.1), new_position.zy );
    
    scene[0] = _SDF(sdBox(new_position, vec3(0.6, 0.6, 0.6)), 0);
    
    new_position = p-vec3(4.,7.0,1.);
    // new_position.xz *= Rot(-iTime*0.15);
    new_position.xz = mul(Rot(-iTime*0.15), new_position.xz );
    new_position.zy = mul(Rot(iTime*0.1), new_position.zy);
    
    scene[1] = _SDF( sdBox(new_position, vec3(0.6, 0.6, 0.6)), 0);
    
    new_position = p-vec3(-3.,6.0,-2);
    // new_position.xz *= Rot(iTime*0.2);
    // new_position.zy *= Rot(-iTime*0.12);
    new_position.xz =mul(Rot(iTime*0.2),new_position.xz  );
    new_position.zy = mul(Rot(-iTime*0.12), new_position.zy);
    
    scene[2] = _SDF( sdBox(new_position, vec3(0.6, 0.6, .06)), 0 );
    
    //Pillars
    pillar_id = floor(p.xz);
    p.xz = fract(p.xz)-.5;

    // float2 s = {0.0, 0.0};
    // if (rd.xz.x < 0.0)
    // s.x = 1.0;
    // if (rd.xz.y < 0.0)
    // s.x = 1.0;
    
    vec2 rC = ((2.0 * step(0.0, rd.xz) - 1.0) * vec2(0.5, 0.5) - p.xz) / rd.xz;
    // vec2 rC = ((2.0 * step( rd.xz,0.0) - 1.0) * vec2(0.5, 0.5) - p.xz) / rd.xz;
    // vec2 rC = ((2.0 * s - 1.0) * vec2(0.5, 0.5) - p.xz) / rd.xz;
    scene[3] = _SDF(min(rC.x, rC.y) + 0.01, 1);
    
    if(pillar_id.x ==0 && pillar_id.y == 0) pillar_id = vec2(1.,0.); //Little fix for the missing cube at the center :D
    
    float height = Hash21(pillar_id*6.)*6.;
    scene[4] = _SDF(sdBox(p, vec3(.42, height > 1. && dot(pillar_id,pillar_id) < 81.0 ? height : -1.,.42)), 1);
    
    return minSDF(scene);
}



SDF RayMarch(vec3 ro, vec3 rd, vec2 uv, float side) {
	SDF dO=_SDF(0.,0);
    
    for(int i=0; i<MAX_STEPS; i++) {
    	vec3 p = ro + rd*dO.dist;
        SDF dS = GetDist(p, rd);
        dS.dist *= side;
        dO.dist += dS.dist*SURF_BIAS;
        dO.materialID = dS.materialID;
        if(dO.dist>MAX_DIST || abs(dS.dist)<SURF_DIST) break;
    }
    
    return dO;
}

vec3 GetNormal(vec3 p, vec3 rd) {
    vec2 e = vec2(.001, 0);
    vec3 n = GetDist(p, rd).dist - vec3(GetDist(p-e.xyy, rd).dist , GetDist(p-e.yxy, rd).dist,GetDist(p-e.yyx, rd).dist);
    
    return normalize(n);
}

float a = 1.2;
float b = 0.5;

vec3 applyFog(  vec3  col, float t, vec3  ro, vec3  rd, vec2 uv )
{
    float fogAmount = (a/b) * exp(-ro.y*b) * (1.0-exp(-t*rd.y*b))/rd.y;
    // float fogAmount = 0.0;
    vec3  fogColor  = vec3(0.439,0.494,1.000) * (1.-length(uv*LIGHT_RADIUS));
    return mix( col, fogColor, fogAmount );
    //
    // return col;
    
    // return float3(1.0, 0.0,0.0);
    // return fogColor;
}

vec3 mapColor(float i, float j)
{
    if(i == 0.)
        return vec3(1.,.0,.0)*j;
    
    if(i == 1.)
        return vec3(0.,1.,.0)*j;
        
    if(i == 2.)
        return vec3(0.200,0.000,1.000)*j;
        
    return vec3(1.,.0,1.)*j;
}

vec3 Render(inout vec3 ro, inout vec3 rd, vec2 uv)
{
    vec3 col = vec3(0.020,0.043,0.239) * (1.-length(uv*LIGHT_RADIUS));
    
    SDF sdf = RayMarch(ro, rd, uv, 1.);
    
    if(sdf.dist<MAX_DIST) {
        vec3 p = ro + rd * sdf.dist;
        vec3 n = GetNormal(p, rd);
        
        float dif = dot(n, normalize(vec3(0,1,0)))*.5+.5;
        col = vec3(dif+0.1, dif+0.1, dif+0.1);
        
        if(sdf.materialID == 1)
        {
            col *= vec3(0.541,0.600,1.000)*clamp(Hash21(pillar_id), 0.3, 1.);
            float colXZ = texture(iChannel0, p.xz*vec2(.1,1.)).r*0.3+0.7;
            float colYZ = texture(iChannel0, p.yz*vec2(.01,1.)).r*0.3+0.7;
            float colXY = texture(iChannel0, p.xy*vec2(1.,.01)).r*0.3+0.7;
            n = abs(n);
            col *= colYZ*n.x + colXZ*n.y + colXY*n.z;
            // col *= applyFog(col, sdf.dist, ro, rd, uv);
        }
        else
        {
            float IOR = 1.1;
            vec3 r = refract(rd, n, 1./IOR);
            
            vec3 rdIn = refract(rd, n, 1./IOR); // ray dir when entering
        
            vec3 pEnter = p - n*SURF_DIST*3.;
            float dIn = RayMarch(pEnter, rdIn, uv, -1.).dist; // inside the object

            vec3 pExit = pEnter + rdIn * dIn; // 3d position of exit
            vec3 nExit = -GetNormal(pExit, rdIn); 

            vec3 rdOut = refract(rdIn, nExit, IOR);
            if(dot(rdOut, rdOut)==0.) rdOut = reflect(rdIn, nExit);
            
            ro = pExit+rdOut*2.;
            rd = rdOut;
            col *= vec3(0.008,0.008,0.051);
        }
    }
    
    //Particles
    
    for(float j = 0.; j < 50.; j++)
    {
        for(float i = 0.; i < 4.; i++)
        {
            vec2 lightuv = uv + vec2(cos((iTime-j*0.02)*0.2+i*4000.)*0.8, sin((iTime-j*0.02)*0.5+i*5000.)*0.45);
            float cd = dot(lightuv, lightuv);
            float light = .000015/cd;

            col += mapColor(i,(50.-j)*0.002)*light*smoothstep(.0,.5,sdf.dist+2.*i);
        }
    }
    
    return col;
}

fixed4 mainImage( vec2 fragCoord )
{
    vec2 uv = (fragCoord-.5*iResolution.xy)/iResolution.y;

    vec3 ro = vec3(0.5, 14.+cos(iTime*0.1), 0.5);

    vec3 rd = normalize(vec3(uv.x, uv.y, 1.));
    
    // rd.zy *= Rot(-PI/2.);
    // rd.xz *= Rot(cos(iTime*0.15)*0.5);
    rd.zy = mul(Rot(-PI/2.), rd.zy);
    rd.xz = mul(Rot(cos(iTime*0.15)*0.5), rd.xz);
    
    vec3 col = Render(ro, rd, uv);
    vec3 refraction = Render(ro, rd, uv);
    
    col += refraction;
    
    col = pow(col, vec3(.4545, .4545, .4545));	// gamma correction
    return vec4(col,1.0);
}
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
                   
            fixed4 frag(v2f _iParam) : SV_Target {
                // return float4(1.0, 0.0, 0.0,0.0);
                return mainImage(gl_FragCoord);
            }

            ENDCG
        }
    }
}
