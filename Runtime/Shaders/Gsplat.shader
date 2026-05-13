// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT
//
// Quest environment-depth occlusion follows Meta's official integration guide:
//   https://developers.meta.com/horizon/documentation/unity/unity-depthapi-occlusions-advanced-usage/
// Renderer-driven _EnvironmentDepthBias is set per-instance via the MaterialPropertyBlock.

Shader "Gsplat/Standard"
{
    Properties {}
    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
        }

        Pass
        {
            ZWrite Off
            Blend One OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require compute
            #pragma multi_compile SH_BANDS_0 SH_BANDS_1 SH_BANDS_2 SH_BANDS_3
            #pragma multi_compile UNCOMPRESSED SPARK
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #pragma shader_feature_local _ GSPLAT_BIRP

            // ── Render-pipeline-specific includes ────────────────────────────────────────
            // Meta ships two flavours of the occlusion header — one per pipeline. Both
            // define the same META_DEPTH_* macros, so the rest of the shader is pipeline-
            // agnostic once one of them is in scope.
            #ifdef GSPLAT_BIRP
                // BiRP path
                #include "UnityCG.cginc"
                #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusionBiRP.cginc"
                #define GSPLAT_GAMMA_TO_LINEAR(rgb) GammaToLinearSpace(rgb)
            #else
                // URP path (default)
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
                #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"
                #define GSPLAT_GAMMA_TO_LINEAR(rgb) FastSRGBToLinear(rgb)
            #endif

            #include "Gsplat.hlsl"
            #ifdef UNCOMPRESSED
                #include "GsplatUncompressed.hlsl"
            #endif
            #ifdef SPARK
                #include "GsplatSpark.hlsl"
            #endif


            bool _GammaToLinear;
            int _SplatCount;
            int _SplatInstanceSize;
            int _SHDegree;
            float4x4 _MATRIX_M;
            float _Brightness;
            float _ScaleFactor;
            float _EnvironmentDepthBias;
            StructuredBuffer<uint> _OrderBuffer;

            struct appdata
            {
                float4 vertex : POSITION;
                
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                uint instanceID : SV_InstanceID;
                #endif

                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            bool InitSource(appdata v, out SplatSource source)
            {
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                source.order = v.instanceID * _SplatInstanceSize + asuint(v.vertex.z);
                #else
                source.order = unity_InstanceID * _SplatInstanceSize + asuint(v.vertex.z);
                #endif

                if (source.order >= _SplatCount)
                    return false;

                source.id = _OrderBuffer[source.order];
                source.cornerUV = float2(v.vertex.x, v.vertex.y) * _ScaleFactor;
                return true;
            }

            // Meta's guide step 2: declare the world-position varying via their macro. Expands
            // to `float3 posWorld : TEXCOORD1;` when HARD/SOFT_OCCLUSION is set, else nothing.
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                META_DEPTH_VERTEX_OUTPUT(1)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                #ifdef GSPLAT_BIRP
                    UNITY_INITIALIZE_OUTPUT(v2f, o);
                #else
                    ZERO_INITIALIZE(v2f, o);
                #endif
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                o.vertex = discardVec;

                SplatSource source;
                if (!InitSource(v, source))
                    return o;

                SplatCenter center;
                SplatCorner corner;
                float4 color;
                if (!InitSplatData(source, mul(UNITY_MATRIX_V, _MATRIX_M), center, corner, color))
                    return o;

                #ifndef SH_BANDS_0
                    float3 dir = normalize(mul(center.view, (float3x3)center.modelView));
                    float3 sh[SH_COEFFS];
                    InitSH(source.id, sh);
                    color.rgb += EvalSH(sh, dir, _SHDegree);
                #endif

                ClipCorner(corner, color.w);

                o.vertex = center.proj + float4(corner.offset.x, _ProjectionParams.x * corner.offset.y, 0, 0);
                o.color = color;
                o.uv = corner.uv;
               
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(o, center.model);

                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float A = dot(i.uv, i.uv);
                if (A > 1.0) discard;

                float2 absUV = abs(i.uv);
                float maxUV = max(absUV.x, absUV.y);

                float falloff = -exp((maxUV - _ScaleFactor * 1.16) * 25 * _ScaleFactor);
                float alpha = (exp(-A * 4.0) + falloff) * i.color.a;

                if (alpha < 1.0 / 255.0) discard;

                float4 outColor = _GammaToLinear
                    ? float4(GSPLAT_GAMMA_TO_LINEAR(i.color.rgb) * alpha * _Brightness, alpha)
                    : float4(i.color.rgb * alpha * _Brightness, alpha);

                META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY(i, outColor, _EnvironmentDepthBias);

                return outColor;
            }
            ENDHLSL
        }
    }
}
