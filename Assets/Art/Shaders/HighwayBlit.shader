Shader "HighwayBlit"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderQueue" = "Transparent"}
        ZTest Always ZWrite Off Cull Off
        Pass
        {
            Name "HighwayBlitPass"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma hull hull
            #pragma domain domain
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Attributes
            {
                float4 positionHCS   : POSITION;
                float2 uv           : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ControlPoint
            {
                float4 positionCS : INTERNALTESSPOS;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct TessellationFactors
            {
                float edge[3] : SV_TessFactor;
                float inside : SV_InsideTessFactor;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D_X(_MainTex);
            SAMPLER(sampler_MainTex);
   
            float2 _FadeParams;
            float _CurveFactor;
            float _IsFading;
            float _TessellationFactor;
            float _DebugTessellation;

            // Vertex shader just passes through the position and UV
            ControlPoint vert(Attributes input)
            {
                ControlPoint output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.positionCS = input.positionHCS;
                output.uv = input.uv;
                return output;
            }

            // Hull shader - responsible for tessellation factor calculation
            TessellationFactors PatchConstantFunction(InputPatch<ControlPoint, 3> patch)
            {
                TessellationFactors f;
                
                // Set tessellation factors for edges and interior
                f.edge[0] = _TessellationFactor;
                f.edge[1] = _TessellationFactor;
                f.edge[2] = _TessellationFactor;
                f.inside = _TessellationFactor;
                
                return f;
            }

            [domain("tri")]
            [partitioning("fractional_even")]
            [outputtopology("triangle_cw")]
            [outputcontrolpoints(3)]
            [patchconstantfunc("PatchConstantFunction")]
            ControlPoint hull(InputPatch<ControlPoint, 3> patch, uint id : SV_OutputControlPointID)
            {
                return patch[id];
            }

            // Domain shader - responsible for computing the final position of each vertex
            [domain("tri")]
            Varyings domain(TessellationFactors factors, OutputPatch<ControlPoint, 3> patch, float3 barycentricCoordinates : SV_DomainLocation)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(patch[0]);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // Barycentric interpolation for triangles
                float4 p0 = barycentricCoordinates.x;
                float4 p1 = barycentricCoordinates.y;
                float4 p2 = barycentricCoordinates.z;

                // Barycentric interpolation of position
                float4 positionCS = 
                    patch[0].positionCS * p0 +
                    patch[1].positionCS * p1 +
                    patch[2].positionCS * p2;

                // Barycentric interpolation of UV coordinates
                float2 texUV = 
                    patch[0].uv * p0 +
                    patch[1].uv * p1 +
                    patch[2].uv * p2;

                // float depth = tex2Dlod(sampler_CameraDepthTexture, float4(texUV, 0.0, 0.0));
                // float depth = 0.5;
                float depth = _CameraDepthTexture.SampleLevel(sampler_CameraDepthTexture, texUV, 0.0, 0.0);
                float sceneEyeDepth = LinearEyeDepth(depth, _ZBufferParams);
                positionCS.y -= (sceneEyeDepth - 3.0) * _CurveFactor * 0.2 * (positionCS.x * positionCS.x);
                // Ensure vertex positions stay precisely where they should be
                positionCS = float4(positionCS.xy, 0.0, 1.0);

                output.positionCS = positionCS;

                #if UNITY_UV_STARTS_AT_TOP
                output.positionCS.y *= -1;
                #endif

                // Clamp UVs to prevent edge sampling issues
                output.uv = saturate(texUV);
                return output;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                // Apply curving
                IN.uv.y += pow(abs(IN.uv.x - 0.5), 2) * _CurveFactor;
                
                float4 color = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, IN.uv);
                
                // Sample the depth from the Camera depth texture.
                #if UNITY_REVERSED_Z
                    real depth = SampleSceneDepth(IN.uv);
                #else
                    // Adjust Z to match NDC for OpenGL ([-1, 1])
                    real depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, SampleSceneDepth(IN.uv));
                #endif

                float sceneEyeDepth = LinearEyeDepth(depth, _ZBufferParams);

                float rate = _FadeParams.y != _FadeParams.x ? 1.0 / (_FadeParams.y - _FadeParams.x) : 0.0;
                float alpha = smoothstep(0.0, 1.0, ((min(max(sceneEyeDepth, _FadeParams.x), _FadeParams.y)) - _FadeParams.x) * rate);
                color.a = color.a == 0.0 ? 0.0 : max(1.0 - _IsFading, min(color.a, 1.0 - alpha));

                // Debug tessellation pattern if enabled
                if (_DebugTessellation > 0.5) {
                    // Display a checkerboard pattern showing the tessellation
                    float2 frac_uv = frac(IN.uv * _TessellationFactor);
                    float checker = (frac_uv.x < 0.5) != (frac_uv.y < 0.5);
                    color.rgb = lerp(color.rgb, float3(checker, checker, 1), 0.3);
                    color.a = 1.0;
                }
                
                return color;
            }
            ENDHLSL
        }
    }
}
