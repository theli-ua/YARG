Shader "ShaderToy_Cube"
{
    Properties
    {
        [NoScaleOffset] _SoundTex ("SoundTexture", 2D) = "white" {}
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
            #define mat3 float3x3
            #define fract frac
            #define iTime _Time.y
            #define atan atan2
            #define mix lerp
            float mod(float x, float y)
			{
			  return x - y * floor(x/y);
			}
                
            #include "UnityCG.cginc"

            sampler2D _SoundTex;
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

#if HW_PERFORMANCE==0
#define AA 1
#else
#define AA 2   // make this 2 or 3 for antialiasing
#endif

float hash( float n ) { return fract(sin(n)*13.5453123); }

float maxcomp( in vec3 v ) { return max( max( v.x, v.y ), v.z ); }

float udBox( vec3 p, vec3 b, float r )
{
    return length(max(abs(p)-b,0.0))-r;
}

vec4 texcube( sampler2D sam, in vec3 p, in vec3 n )
{
    vec3 a = n*n;
	// vec4 x = texture( sam, p.yz );
	vec4 x = tex2D( sam, p.yz );
	vec4 y = tex2D( sam, p.zx );
	vec4 z = tex2D( sam, p.yx );
	return (x*a.x + y*a.y + z*a.z) / (a.x + a.y + a.z);
    // return vec3( 1.0, 1.0, 1.0, 1.0 );
}

//---------------------------------

float freqs[4];

vec3 mapH( in vec2 pos )
{
	vec2 fpos = fract( pos ); 
	vec2 ipos = floor( pos );
	
    float f = 0.0;	
	float id = hash( ipos.x + ipos.y*57.0 );
	f += freqs[0] * clamp(1.0 - abs(id-0.20)/0.30, 0.0, 1.0 );
	f += freqs[1] * clamp(1.0 - abs(id-0.40)/0.30, 0.0, 1.0 );
	f += freqs[2] * clamp(1.0 - abs(id-0.60)/0.30, 0.0, 1.0 );
	f += freqs[3] * clamp(1.0 - abs(id-0.80)/0.30, 0.0, 1.0 );

    f = pow( clamp( f, 0.0, 1.0 ), 2.0 );
    float h = 2.5*f;

    return vec3( h, id, f );
}

vec3 map( in vec3 pos )
{
	vec2  p = fract( pos.xz ); 
    vec3  m = mapH( pos.xz );
	float d = udBox( vec3(p.x-0.5,pos.y-0.5*m.x,p.y-0.5), vec3(0.3,m.x*0.5,0.3), 0.1 );
    return vec3( d, m.yz );
}

const float surface = 0.001;

vec3 trace( vec3 ro, in vec3 rd, in float tmin, in float tmax )
{
    ro += tmin*rd;
    
	vec2 pos = floor(ro.xz);
    vec3 rdi = 1.0/rd;
    vec3 rda = abs(rdi);
	vec2 rds = sign(rd.xz);
	vec2 dis = (pos-ro.xz+ 0.5 + rds*0.5) * rdi.xz;
	
	vec3 res = vec3( -1.0, -1.0, -1.0 );

    // traverse regular grid (in 2D)
	vec2 mm = vec2(0.0 ,0.0);
	for( int i=0; i<28; i++ ) 
	{
        vec3 cub = mapH( pos );

        #if 1
            vec2 pr = pos+0.5-ro.xz;
			vec2 mini = (pr-0.5*rds)*rdi.xz;
	        float s = max( mini.x, mini.y );
            if( (tmin+s)>tmax ) break;
        #endif
        
        // intersect box
		vec3  ce = vec3( pos.x+0.5, 0.5*cub.x, pos.y+0.5 );
        vec3  rb = vec3(0.3,cub.x*0.5,0.3);
        vec3  ra = rb + 0.12;
		vec3  rc = ro - ce;
        float tN = maxcomp( -rdi*rc - rda*ra );
        float tF = maxcomp( -rdi*rc + rda*ra );
        if( tN < tF )//&& tF > 0.0 )
        {
            // raymarch
            float s = tN;
            float h = 1.0;
            for( int j=0; j<24; j++ )
            {
                h = udBox( rc+s*rd, rb, 0.1 ); 
                s += h;
                if( s>tF ) break;
            }

            if( h < (surface*s*2.0) )
            {
                res = vec3( s, cub.yz );
                break; 
            }
            
		}

        // step to next cell		
		mm = step( dis.xy, dis.yx ); 
		dis += mm*rda.xz;
        pos += mm*rds;
	}

    res.x += tmin;
    
	return res;
}

float usmoothstep( in float x )
{
    x = clamp(x,0.0,1.0);
    return x*x*(3.0-2.0*x);
}

float softshadow( in vec3 ro, in vec3 rd, in float mint, in float maxt, in float k )
{
    float res = 1.0;
    float t = mint;
    for( int i=0; i<50; i++ )
    {
        float h = map( ro + rd*t ).x;
        res = min( res, usmoothstep(k*h/t) );
        t += clamp( h, 0.05, 0.2 );
        if( res<0.001 || t>maxt ) break;
    }
    return clamp( res, 0.0, 1.0 );
}

vec3 calcNormal( in vec3 pos, in float t )
{
    vec2 e = vec2(1.0,-1.0)*surface*t;
    return normalize( e.xyy*map( pos + e.xyy ).x + 
					  e.yyx*map( pos + e.yyx ).x + 
					  e.yxy*map( pos + e.yxy ).x + 
					  e.xxx*map( pos + e.xxx ).x );
}

const vec3 light1 = vec3(  0.70, 0.52, -0.45 );
const vec3 light2 = vec3( -0.71, 0.000,  0.71 );
// const vec3 lpos = vec3(0.0,0.0,0.0) + 6.0*light1;
const vec3 lpos = vec3(0.0,0.0,0.0) + 6.0*vec3(  0.70, 0.52, -0.45 );

vec2 boundingVolume( vec2 tminmax, in vec3 ro, in vec3 rd )
{
    float bp = 2.7;
    float tp = (bp-ro.y)/rd.y;
    if( tp>0.0 ) 
    {
        if( ro.y>bp ) tminmax.x = max( tminmax.x, tp );
        else          tminmax.y = min( tminmax.y, tp );
    }
    bp = 0.0;
    tp = (bp-ro.y)/rd.y;
    if( tp>0.0 ) 
    {
        if( ro.y>bp ) tminmax.y = min( tminmax.y, tp );
    }
    return tminmax;
}

vec3 doLighting( in vec3 col, in float ks,
                 in vec3 pos, in vec3 nor, in vec3 rd )
{
    vec3  ldif = lpos - pos;
    float llen = length( ldif );
    ldif /= llen;
	float con = dot( light1,ldif);
	float occ = mix( clamp( pos.y/4.0, 0.0, 1.0 ), 1.0, 0.2*max(0.0,nor.y) );
    vec2 sminmax = vec2(0.01, 5.0);

    float sha = softshadow( pos, ldif, sminmax.x, sminmax.y, 32.0 );;
		
    float bb = smoothstep( 0.5, 0.8, con );
    float lkey = clamp( dot(nor,ldif), 0.0, 1.0 );
	vec3  lkat = vec3(1.0, 1.0,1.0);
          lkat *= vec3(bb*bb*0.6+0.4*bb,bb*0.5+0.5*bb*bb,bb).zyx;
          lkat /= 1.0+0.25*llen*llen;		
		  lkat *= 30.0;
          // lkat *= sha;
          lkat *= vec3(sha,0.6*sha+0.4*sha*sha,0.3*sha+0.7*sha*sha);
    
    float lbac = clamp( 0.5 + 0.5*dot( light2, nor ), 0.0, 1.0 );
          lbac *= smoothstep( 0.0, 0.8, con );
		  lbac /= 1.0+0.2*llen*llen;		
		  lbac *= 7.0;
	float lamb = 1.0 - 0.5*nor.y;
          lamb *= 1.0-smoothstep( 10.0, 25.0, length(pos.xz) );
		  lamb *= 0.25 + 0.75*smoothstep( 0.0, 0.8, con );
		  lamb *= 0.25;

    vec3 lin  = 1.0*vec3(1.60,0.70,0.30)*lkey*lkat*(0.5+0.5*occ);
         lin += 1.0*vec3(0.20,0.05,0.02)*lamb*occ*occ;
         lin += 1.0*vec3(0.70,0.20,0.08)*lbac*occ*occ;
         lin *= vec3(1.3,1.1,1.0);
    
    col = col*lin;

    vec3 hal = normalize(ldif-rd);
    vec3 spe = lkey*lkat*(0.5+0.5*occ)*5.0*
               pow( clamp(dot(hal, nor),0.0,1.0), 6.0+6.0*ks ) * 
               (0.04+0.96*pow(clamp(1.0-dot(hal,ldif),0.0,1.0),5.0));

    col += (0.4+0.6*ks)*spe*vec3(0.8,0.9,1.0);

    col = 1.4*col/(1.0+col);
    
    return col;
}

mat3 setLookAt( in vec3 ro, in vec3 ta, float cr )
{
	vec3  cw = normalize(ta-ro);
	vec3  cp = vec3(sin(cr), cos(cr),0.0);
	vec3  cu = normalize( cross(cw,cp) );
	vec3  cv = normalize( cross(cu,cw) );
    return mat3( cu, cv, cw );
}

vec3 render( in vec3 ro, in vec3 rd )
{
    vec3 col = vec3( 0.0, 0.0, 0.0 );

    vec2 tminmax = vec2(0.0, 40.0 );

    tminmax = boundingVolume( tminmax, ro, rd );

    // raytrace
    vec3 res = trace( ro, rd, tminmax.x, tminmax.y );
    if( res.y > -0.5 )
    {
        float t = res.x;
        vec3 pos = ro + t*rd;
        vec3 nor = calcNormal( pos, t );

        // material	
        col = 0.5 + 0.5*cos( 6.2831*res.y + vec3(0.0, 0.4, 0.8) );
        vec3 ff = texcube( _MainTex, 0.21*vec3(pos.x,4.0*res.z-pos.y,pos.z), nor ).xyz;
        ff = pow(ff,vec3(1.3, 1.3, 1.3))*1.1;
        col *= ff.x;

        // lighting
        col = doLighting( col, ff.x*ff.x*ff.x*2.0, pos, nor, rd );
        // col = float3(1.0,1.0,1.0);
        col *= 1.0 - smoothstep( 20.0, 40.0, t );
        
    }
    return col;
}

fixed4 mainImage( vec2 fragCoord )
{
	freqs[0] = tex2D( _SoundTex, vec2( 0.01, 0.25 ) ).x;
	freqs[1] = tex2D( _SoundTex, vec2( 0.07, 0.25 ) ).x;
	freqs[2] = tex2D( _SoundTex, vec2( 0.15, 0.25 ) ).x;
	freqs[3] = tex2D( _SoundTex, vec2( 0.30, 0.25 ) ).x;
    //-----------
    float time = 5.0 + 0.2*iTime;// + 20.0*iMouse.x/iResolution.x;
    
    vec3 tot = vec3(0.0, 0.0,0.0);
    // #if AA>1
    // for( int j=0; j<AA; j++ )
    // for( int i=0; i<AA; i++ )
    // {
    //     vec2 off = vec2(float(i),float(j))/float(AA);
    // #else
        vec2 off = vec2(0.0,0.0);
    // #endif        
        vec2 xy = (-iResolution.xy+2.0*(fragCoord+off)) / iResolution.y;

        // camera	
        vec3 ro = vec3( 8.5*cos(0.2+.33*time), 5.0+2.0*cos(0.1*time), 8.5*sin(0.1+0.37*time) );
        vec3 ta = vec3( -2.5+3.0*cos(1.2+.41*time), 0.0, 2.0+3.0*sin(2.0+0.38*time) );
        float roll = 0.2*sin(0.1*time);

        // camera tx
        mat3 ca = setLookAt( ro, ta, roll );
        // vec3 rd = normalize( ca * vec3(xy.xy,1.75) );
        // vec3 rd = normalize( mul(vec3(xy.xy,1.75), ca) );
        vec3 rd = normalize( mul(ca, vec3(xy.xy,1.75)) );
        
        vec3 col = render( ro, rd );
        col = pow( col, vec3(0.4545, 0.4545, 0.4545) );
        col = pow( col, vec3(0.8,0.93,1.0) );
        col = clamp(col,0.0,1.0);
        tot += col;
        
 //    #if AA>1
 //    }
	// tot /= float(AA*AA);
 //    #endif    
    
    // vigneting
	vec2 q = fragCoord.xy/iResolution.xy;
    tot *= 0.2 + 0.8*pow( 16.0*q.x*q.y*(1.0-q.x)*(1.0-q.y), 0.1 );

    return fixed4( tot, 1.0 );
                // return float4(1.0, 0.0, 0.0,0.0);
}

// void mainVR( out vec4 fragColor, in vec2 fragCoord, in vec3 fragRayOri, in vec3 fragRayDir )
// {
// 	freqs[0] = tex2D( _SoundTex, vec2( 0.01, 0.25 ) ).x;
// 	freqs[1] = tex2D( _SoundTex, vec2( 0.07, 0.25 ) ).x;
// 	freqs[2] = tex2D( _SoundTex, vec2( 0.15, 0.25 ) ).x;
// 	freqs[3] = tex2D( _SoundTex, vec2( 0.30, 0.25 ) ).x;

//     vec3 col = render( fragRayOri + vec3(0.0,4.0,0.0), fragRayDir );

//     col = pow( col, vec3(0.4545, 0.4545, 0.4545) );
//     col = pow( col, vec3(0.8,0.95,1.0) );

//     fragColor = vec4( col, 1.0 );
// }
                   
            fixed4 frag(v2f _iParam) : SV_Target {
                // return float4(1.0, 0.0, 0.0,0.0);
                return mainImage(gl_FragCoord);
            }

            ENDCG
        }
    }
}
