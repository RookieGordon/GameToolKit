/*
 * GPU动画示例Shader
 * 借鉴自 Unity3D-ToolChain_StriteR
 *
 * 支持两种GPU动画模式:
 * - _ANIM_BONE: 骨骼动画（通过UV1传递骨骼索引，UV2传递骨骼权重）
 * - _ANIM_VERTEX: 顶点动画（通过SV_VertexID索引顶点数据）
 */
Shader "UnityToolKit/GPUAnimation"
{
    Properties
    {
        [NoScaleOffset] _MainTex ("Texture", 2D) = "white" {}

        [Header(GPU Animation)]
        [NoScaleOffset] _AnimTex ("Animation Texture", 2D) = "black" {}
        _AnimFrameBegin ("Begin Frame", Int) = 0
        _AnimFrameEnd ("End Frame", Int) = 0
        _AnimFrameInterpolate ("Frame Interpolate", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "DisableBatching" = "True"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "GPUAnimationInclude.hlsl"
        #pragma multi_compile_instancing
        #pragma shader_feature_local _ANIM_BONE _ANIM_VERTEX

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            #if defined(_ANIM_VERTEX)
                uint vertexID : SV_VertexID;
            #elif defined(_ANIM_BONE)
                float4 transformIndexes : TEXCOORD1;
                float4 transformWeights : TEXCOORD2;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float diffuse : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

            #if defined(_ANIM_BONE)
                SampleTransform(v.transformIndexes, v.transformWeights, v.positionOS, v.normalOS);
            #elif defined(_ANIM_VERTEX)
                SampleVertex(v.vertexID, v.positionOS, v.normalOS);
            #endif

                Light mainLight = GetMainLight();
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.diffuse = saturate(dot(normalWS, mainLight.direction));
                o.positionCS = TransformObjectToHClip(v.positionOS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                return col * (i.diffuse * 0.5 + 0.5);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct ShadowAttributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
            #if defined(_ANIM_VERTEX)
                uint vertexID : SV_VertexID;
            #elif defined(_ANIM_BONE)
                float4 transformIndexes : TEXCOORD1;
                float4 transformWeights : TEXCOORD2;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            float3 _LightDirection;

            ShadowVaryings ShadowVertex(ShadowAttributes v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                ShadowVaryings o;

            #if defined(_ANIM_BONE)
                SampleTransform(v.transformIndexes, v.transformWeights, v.positionOS, v.normalOS);
            #elif defined(_ANIM_VERTEX)
                SampleVertex(v.vertexID, v.positionOS, v.normalOS);
            #endif

                float3 positionWS = TransformObjectToWorld(v.positionOS);
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));

            #if UNITY_REVERSED_Z
                o.positionCS.z = min(o.positionCS.z, o.positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #else
                o.positionCS.z = max(o.positionCS.z, o.positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #endif

                return o;
            }

            float4 ShadowFragment(ShadowVaryings i) : SV_TARGET
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
