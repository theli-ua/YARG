Shader "Sketch"
{
    Properties
    {
        [NoScaleOffset] _Yarg_SoundTex ("SoundTexture", 2D) = "white" {}
        [NoScaleOffset] _Yarg_AlbumCover ("AlbumCover", 2D) = "white" {}
        [NoScaleOffset] _Noise ("_Noise", 2D) = "white" {}
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
            float mod(float x, float y)
			{
			  return x - y * floor(x/y);
			}
                
            #include "UnityCG.cginc"

            sampler2D _Yarg_SoundTex;
            sampler2D _Yarg_AlbumCover;
            sampler2D _Noise;

            float4 _Yarg_AlbumCover_ST;

          
            struct appdata_t
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f {    
                float4 pos : SV_POSITION;    
                float2 scrPos : TEXCOORD0;   
            };

            v2f vert(appdata_t v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.scrPos = TRANSFORM_TEX(v.uv, _Yarg_AlbumCover);
                o.scrPos.x = - o.scrPos.x;

                return o;
            }


/* 
    Author: Daniel Taylor
	License Creative Commons Attribution-NonCommercial-ShareAlike 3.0 Unported License.

	Tried my hand at a sketch-looking shader.

	I'm sure that someone has used this exact method before, but oh well. I like to 
	think that this one is very readable (aka I'm not very clever with optimizations).
	There's little noise in the background, which is a good sign, however it's easy to
	create a scenerio that tricks it (the 1961 Commerical video is a good example).
	Also, text (or anything thin) looks really bad on it, don't really know how to fix
	that.

	Also, if the Shadertoy devs are reading this, the number one feature request that
	I have is a time slider. Instead of waiting for the entire video to loop back to
	the end, be able to fast forward to a specific part. It'd really help, I swear.

	Previous work:
	https://www.shadertoy.com/view/XtVGD1 - the grandaddy of all sketch shaders, by flockaroo
*/

#define PI2 6.28318530717959

//#define RANGE 16.
//#define STEP 2.
//#define ANGLENUM 4.
#define RANGE 16
#define STEP 2
#define ANGLENUM 4

// Grayscale mode! This is for if you didn't like drawing with colored pencils as a kid
//#define GRAYSCALE

// Here's some magic numbers, and two groups of settings that I think looks really nice. 
// Feel free to play around with them!

#define MAGIC_GRAD_THRESH 0.1

// Setting group 1:
/*#define MAGIC_SENSITIVITY     4.
#define MAGIC_COLOR           1.*/

// Setting group 2:
//#define MAGIC_SENSITIVITY     10.
#define MAGIC_SENSITIVITY     10.
#define MAGIC_COLOR           0.5

//---------------------------------------------------------
// Your usual image functions and utility stuff
//---------------------------------------------------------
vec4 getCol(vec2 pos)
{
    //vec2 uv = pos / iResolution.xy;
    //return texture(iChannel0, uv);
    return tex2D(_Yarg_AlbumCover, pos);
}

float getVal(vec2 pos)
{
    vec4 c=getCol(pos);
    return dot(c.xyz, vec3(0.2126, 0.7152, 0.0722));
}

vec2 getGrad(vec2 pos, float eps)
{
   	vec2 d=vec2(eps,0);
    return vec2(
        getVal(pos+d.xy)-getVal(pos-d.xy),
        getVal(pos+d.yx)-getVal(pos-d.yx)
    )/eps/2.;
}

void pR(inout vec2 p, float a) {
	p = cos(a)*p + sin(a)*vec2(p.y, -p.x);
}
float absCircular(float t)
{
    float a = floor(t + 0.5);
    return mod(abs(a - t), 1.0);
}

//---------------------------------------------------------
// Let's do this!
//---------------------------------------------------------
vec4 mainImage( vec2 fragCoord )
{   
    vec4 fragColor;
    vec2 pos = fragCoord;
    float weight = 1.0;
    
    for (float j = 0.; j < ANGLENUM; j += 1.)
    {
        vec2 dir = vec2(1, 0);
        pR(dir, j * PI2 / (2. * ANGLENUM));
        
        vec2 grad = vec2(-dir.y, dir.x);
        
        for (float i = -RANGE; i <= RANGE; i += STEP)
        {
            vec2 pos2 = pos + normalize(dir)*(i / 512.0);
            
            // video texture wrap can't be set to anything other than clamp  (-_-)
            //if (pos2.y < 0. || pos2.x < 0. || pos2.x > iResolution.x || pos2.y > iResolution.y)
            //   continue;
            
            vec2 g = getGrad(pos2, 0.01);
            if (length(g) < MAGIC_GRAD_THRESH)
                continue;
            
            weight -= pow(abs(dot(normalize(grad), normalize(g))), MAGIC_SENSITIVITY) / floor((2. * RANGE + 1.) / STEP) / ANGLENUM;
        }
    }
    
//#ifndef GRAYSCALE
 // vec4 col = getCol(pos);
//#else
    float c = getVal(pos);
    vec4 col = (c, c, c, c);
//#endif
    
    vec4 background = mix(col, vec4(1,1,1,1), MAGIC_COLOR);
    
    // I couldn't get this to look good, but I guess it's almost obligatory at this point...
    /*float distToLine = absCircular(fragCoord.y / (iResolution.y/8.));
    background = mix(vec4(0.6,0.6,1,1), background, smoothstep(0., 0.03, distToLine));*/
    
    
    // because apparently all shaders need one of these. It's like a law or something.
    float r = length(pos - iResolution.xy*.5) / iResolution.x;
    //float vign = 1. - r*r*r;
    
    //vec4 a = texture(iChannel1, pos/iResolution.xy);
    vec4 a = tex2D(_Noise, pos);
    
    fragColor = mix(vec4(0,0,0,0), background, weight) + a.xxxx/25.;
    //fragColor = getCol(pos);
    return fragColor;
}
                    
            fixed4 frag(v2f _iParam) : SV_Target {
                //return tex2D(_Yarg_AlbumCover, _iParam.scrPos);
                //return fixed4(1.0,0,0,0);
                return mainImage(_iParam.scrPos);
            }

            ENDCG
        }
    }
}
