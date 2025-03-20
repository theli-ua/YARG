Shader "Level"
{
    Properties
    {
        [NoScaleOffset] _Yarg_SoundTex ("SoundTexture", 2D) = "white" {}
        [NoScaleOffset] _Level ("level", 2D) = "white" {}
    }
    SubShader
    {
        Blend SrcAlpha OneMinusSrcAlpha

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

            texture2D _Yarg_SoundTex;

            sampler2D _Level;

            float4 _Level_ST;

          
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
                o.scrPos = TRANSFORM_TEX(v.uv, _Level);
                o.scrPos.x = - o.scrPos.x;

                return o;
            }
 
                    
            fixed4 frag(v2f _iParam) : SV_Target {
              float level  = _Yarg_SoundTex.Load( int3(0,0,0)).x; 
              return fixed4(tex2D(_Level, _iParam.scrPos).rgb, step(1.0 - _iParam.scrPos.y, level));
            }

            ENDCG
        }
    }
}
