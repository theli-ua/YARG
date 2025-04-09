// Port of https://www.shadertoy.com/view/ldjBW1
// Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License.

Shader "MatrixRainShaderToy"
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
                
                
            #include "UnityCG.cginc"
            #include "ShaderToy.cginc"
            

            #define FLT_MIN 1.175494351e-38
                    
            #define R fract(1e2*sin(p.x*8.+p.y))
                    
            fixed4 mainImage(vec2 u) {
                fixed4 o = fixed4(0.0, 0.0, 0.0, 0.0);

                vec3 v=vec3(u,1)/iResolution-.5,
                    s=.5/abs(v),
                    i=ceil(8e2*(s.z=min(s.y,s.x))*(s.y<s.x?v.xzz:v.zyz)),
                    j=fract(i*=.1),
                    p=vec3(9,int(iTime*(9.+8.*sin(i-=j).x)),0)+i;
               o-=o,o.g=R/s.z;p*=j;o*=R>.5&&j.x<.6&&j.y<.8?1.:0.;
               return o;
            }

            fixed4 frag(v2f _iParam) : SV_Target {
                return mainImage(gl_FragCoord);
            }

            ENDCG
        }
    }
}
