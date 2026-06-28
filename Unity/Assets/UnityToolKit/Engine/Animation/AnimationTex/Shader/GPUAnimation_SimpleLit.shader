/*
 * GPU动画示例Shader
 *
 * 【适用范围】
 *   这是一个用于展示 GPU 动画功能的最小化示例 Shader，
 *   仅包含基础渲染功能，适合作为模板进行二次开发。
 *
 * 【渲染管线】
 *   - 目标管线：Universal Render Pipeline (URP)
 *   - 渲染类型：不透明 (Opaque)
 *
 * 【光照模型】
 *   - 仅计算主方向光的 Half-Lambert 漫反射：diffuse = dot(N, L) * 0.5 + 0.5
 *   - 不包含：高光反射(Specular)、环境光/GI、额外光源(Additional Lights)、雾效(Fog)
 *
 * 【纹理采样】
 *   - _MainTex：基础颜色贴图（单张），仅使用 UV0 通道采样
 *   - _AnimTex：动画数据纹理，Point 采样模式，在顶点阶段读取（非常规贴图，存储的是动画帧数据）
 *   - 不包含：法线贴图、金属度/粗糙度贴图、自发光贴图、遮挡贴图等
 *
 * 【Pass 说明】
 *   - Pass 0 (ForwardLit)：前向渲染主 Pass，输出带 Half-Lambert 漫反射的颜色
 *     未设置 LightMode 标签，作为默认 Pass 在所有渲染上下文（含材质预览）中均可执行
 *   - Pass 1 (ShadowCaster)：阴影投射 Pass，仅写入深度，确保 GPU 动画角色可以正确投射阴影
 *
 * 【GPU 动画模式】（通过 shader_feature_local 编译时切换）
 *   - ANIM_BONE：骨骼动画模式，通过 UV1(TEXCOORD1) 传递 4 个骨骼索引，
 *                UV2(TEXCOORD2) 传递 4 个骨骼权重，在顶点阶段采样骨骼矩阵并蒙皮
 *   - ANIM_VERTEX：顶点动画模式，通过 SV_VertexID 直接从纹理读取每个顶点的位置和法线
 *
 * 【GPU Instancing】
 *   - 支持 GPU Instancing（multi_compile_instancing）
 *   - 帧参数（_AnimFrameBegin / _AnimFrameEnd / _AnimFrameInterpolate）声明在
 *     UNITY_INSTANCING_BUFFER 中，每个实例可独立播放不同动画帧，同时保持合批
 *
 * 【已知限制】
 *   - DisableBatching = True：禁用了 Unity 的静态/动态合批（因为顶点在 Shader 中变形，
 *     合批会破坏模型空间坐标）。合批通过 GPU Instancing 实现。
 *   - 不支持 SRP Batcher（Instanced Property 不在 UnityPerMaterial CBuffer 中）
 *   - 片元着色器仅输出漫反射颜色，不写入 GBuffer，不适用于延迟渲染路径
 */
Shader "UnityToolKit/GPUAnimation/SimpleLit"
{
    Properties
    {
        [NoScaleOffset] _MainTex ("Texture", 2D) = "white" {}

        [Header(GPU Animation)]
        [NoScaleOffset] _AnimTex ("Animation Texture", 2D) = "black" {}
        _AnimFrameBegin ("Begin Frame", Int) = 0
        _AnimFrameEnd ("End Frame", Int) = 0
        _AnimFrameInterpolate ("Frame Interpolate", Range(0, 1)) = 0
        [HideInInspector] _AnimRowsPerFrame ("Rows Per Frame", Int) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "DisableBatching" = "True"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "GPUAnimationInclude.hlsl"
        #pragma multi_compile_instancing
        #pragma shader_feature_local _ ANIM_BONE ANIM_VERTEX

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            #if defined(ANIM_VERTEX)
                uint vertexID : SV_VertexID;
            #elif defined(ANIM_BONE)
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

            #if defined(ANIM_BONE)
                SampleTransform(v.transformIndexes, v.transformWeights, v.positionOS, v.normalOS);
            #elif defined(ANIM_VERTEX)
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
            #if defined(ANIM_VERTEX)
                uint vertexID : SV_VertexID;
            #elif defined(ANIM_BONE)
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

            #if defined(ANIM_BONE)
                SampleTransform(v.transformIndexes, v.transformWeights, v.positionOS, v.normalOS);
            #elif defined(ANIM_VERTEX)
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
