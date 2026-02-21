// jave.lin 2024/07/08
// storm team unlit texture
Shader "ST/ST_Unlit_Tex_Instanced_VAT"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _AnimTexture ("Texture", 2D) = "white" {}
        _ColorMul ("Color Mul", Color) = (1.0, 1.0, 1.0, 1.0)
        _ColorReplace ("Color Replace", Color) = (0.0, 0.0, 0.0, 0.0)
//        [Toggle(_SHADOW_ON)] _SHADOW_ON ("Shadow On", Float) = 1
        // [Toggle] _ALPHATEST ("Alpha Test On", Float) = 0
    }
    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
        }
        LOD 100

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Assets/Arts/Shaders/3D/Characters/ST_VertexAnimationTexture.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
        CBUFFER_END
        half4 _ST_AmbientColor;

        #define MAX_CLIP_NUM_PER_PREFAB 20
        // #define MAX_INSTANCE_NUM 682
        #define MAX_INSTANCE_NUM 170
        #define FLOAT4_COUNT_PER_INSTANCE 6
        #define FLOAT4_OFFSET_OF_MATRIX_ROW0 0
        #define FLOAT4_OFFSET_OF_MATRIX_ROW1 1
        #define FLOAT4_OFFSET_OF_MATRIX_ROW2 2
        #define FLOAT4_OFFSET_OF_CUSTOM_DATA0 3
        #define FLOAT4_OFFSET_OF_CUSTOM_DATA1 4
        #define FLOAT4_OFFSET_OF_CUSTOM_DATA2 5

        CBUFFER_START(PerPrefab)
            //X分量是offsetRows, Y分量是durationTime, Z分量是isLooping, W分量是fps
            float4 _ClipInfo[MAX_CLIP_NUM_PER_PREFAB];
        CBUFFER_END

        CBUFFER_START(PerInstance)
            /// 第0个float4: objectToWorld第0行
            /// 第1个float4: objectToWorld第1行
            /// 第2个float4: objectToWorld第2行
            /// 第3个float4: X分量是_ClipIndex, Y分量是_PlayTime, Z分量是播放速度，W分量待定
            /// 第4个float4: XYZ分量是_ColorMul, W分量待定
            /// 第5个float4: _ColorReplace
            float4 _InstanceData[MAX_INSTANCE_NUM * FLOAT4_COUNT_PER_INSTANCE];
        CBUFFER_END
        ENDHLSL

        Pass
        {
            HLSLPROGRAM
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            // #pragma multi_compile _ _SHADOW_ON
            #pragma multi_compile _ _SHADOWS_SOFT

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                half4 uv1 : TEXCOORD1;
                half4 uv2 : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 shadowCoord : TEXCOORD1; // jave.lin : shadow recieve 在给到 fragment 时，要有阴影坐标
                // float3 positionWS : TEXCOORD2;这个没用到，暂时注释掉
                // half3 normalWS : TEXCOORD3;这个没用到，暂时注释掉
                half4 uv2 : TEXCOORD2;
                #define _colorMul_ uv2.xyz
                #define _diffuse_ uv2.w
                half4 colorReplace : TEXCOORD3;
            };

            sampler2D _MainTex;

            Varyings vert(Attributes v, uint instanceID : SV_InstanceID)
            {
                Varyings o;

                uint baseOffset = instanceID * FLOAT4_COUNT_PER_INSTANCE;
                float4 objectToWorldRow0 = _InstanceData[baseOffset];
                float4 objectToWorldRow1 = _InstanceData[baseOffset + FLOAT4_OFFSET_OF_MATRIX_ROW1];
                float4 objectToWorldRow2 = _InstanceData[baseOffset + FLOAT4_OFFSET_OF_MATRIX_ROW2];
                float4 customInstanceData0 = _InstanceData[baseOffset + FLOAT4_OFFSET_OF_CUSTOM_DATA0];
                half4 customInstanceData1 = _InstanceData[baseOffset + FLOAT4_OFFSET_OF_CUSTOM_DATA1];
                half4 customInstanceData2 = _InstanceData[baseOffset + FLOAT4_OFFSET_OF_CUSTOM_DATA2];
                half4 clipInfo = _ClipInfo[customInstanceData0.x];
                float4 positionOS;
                float3 normalOS;
                AnimateBlend(v.positionOS, v.normal, v.uv1, v.uv2, clipInfo, customInstanceData0.y, customInstanceData0.z, positionOS, normalOS);

                Light mainLight = GetMainLight();
                half3 lightDir = mainLight.direction;

                float4x4 objectToWorld = float4x4(objectToWorldRow0, objectToWorldRow1, objectToWorldRow2, float4(0, 0, 0, 1));
                float3 positionWS = mul(objectToWorld, positionOS).xyz;
                o.positionCS = TransformWorldToHClip(positionWS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                half3 normalWS = normalize(mul((float3x3)objectToWorld, normalOS));
                o.shadowCoord = TransformWorldToShadowCoord(positionWS);
                // jave.lin : shadow recieve 将 世界坐标 转到 灯光坐标（阴影坐标）
                o._diffuse_ = saturate(dot(normalWS, lightDir));
                o._colorMul_ = customInstanceData1.xyz;
                o.colorReplace = customInstanceData2;

                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                Light mainLight = GetMainLight();
                // half4 finalCol = tex2D(_MainTex, i.uv);
                half4 finalCol = tex2Dbias(_MainTex, float4(i.uv, 0, -1));
                half shadow = MainLightRealtimeShadow(i.shadowCoord);
                //half3 _ambientLight = _GlossyEnvironmentColor.rgb;
                half3 _ambientLight = _ST_AmbientColor.rgb;
                // float3 _diffuseLight = { 0.6,0.6,0.6 };
                half3 _diffuseLight = mainLight.color;
                // jave.lin : 2024/10/08 美术需求，说是增加了环境光之后，不需要偏蓝处理
                // _ambientLight.z += _diffuseLight.z * 0.5;
                // _diffuseLight.z -= _diffuseLight.z * 0.5;
                _diffuseLight *= i._diffuse_ * shadow;
                finalCol.rgb *= (_ambientLight + _diffuseLight) * 1.1;

                finalCol.rgb *= i._colorMul_;
                finalCol.rgb = lerp(finalCol.rgb, i.colorReplace.rgb, i.colorReplace.a);
                finalCol.a = 1.0f;

                return finalCol;
            }
            ENDHLSL
        }

        Pass // jave.lin : 有 ApplyShadowBias
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct a2v
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
                half4 uv1 : TEXCOORD1;
                half4 uv2 : TEXCOORD2;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // 以下三个 uniform 在 URP shadows.hlsl 相关代码中可以看到没有放到 CBuffer 块中，所以我们只要在 定义为不同的 uniform 即可
            float3 _LightDirection;
            float4 _ShadowBias; // x: depth bias, y: normal bias
            half4 _MainLightShadowParams; // (x: shadowStrength, y: 1.0 if soft shadows, 0.0 otherwise)
            // jave.lin 直接将：Shadows.hlsl 中的 ApplyShadowBias copy 过来
            float3 ApplyShadowBias(float3 positionWS, float3 normalWS, float3 lightDirection)
            {
                float invNdotL = 1.0 - saturate(dot(lightDirection, normalWS));
                float scale = invNdotL * _ShadowBias.y;
                // normal bias is negative since we want to apply an inset normal offset
                positionWS = lightDirection * _ShadowBias.xxx + positionWS;
                positionWS = normalWS * scale.xxx + positionWS;
                return positionWS;
            }

            v2f vert(a2v v, uint instanceID : SV_InstanceID)
            {
                v2f o = (v2f)0;
                uint baseOffset = instanceID * FLOAT4_COUNT_PER_INSTANCE;
                float4 objectToWorldRow0 = _InstanceData[baseOffset];
                float4 objectToWorldRow1 = _InstanceData[baseOffset + FLOAT4_OFFSET_OF_MATRIX_ROW1];
                float4 objectToWorldRow2 = _InstanceData[baseOffset + FLOAT4_OFFSET_OF_MATRIX_ROW2];
                float4 customInstanceData0 = _InstanceData[baseOffset + FLOAT4_OFFSET_OF_CUSTOM_DATA0];
                half4 clipInfo = _ClipInfo[customInstanceData0.x];
                float4 positionOS;
                float3 normalOS;
                AnimateBlend(v.vertex, v.normal, v.uv1, v.uv2, clipInfo, customInstanceData0.y, customInstanceData0.z, positionOS, normalOS);
                float4x4 objectToWorld = float4x4(objectToWorldRow0, objectToWorldRow1, objectToWorldRow2, float4(0, 0, 0, 1));
                float3 worldPos = mul(objectToWorld, positionOS).xyz;
                float3 normalWS = normalize(mul((float3x3)objectToWorld, normalOS));
                worldPos = ApplyShadowBias(worldPos, normalWS, _LightDirection);
                o.vertex = TransformWorldToHClip(worldPos);
                // jave.lin : 参考 cat like coding 博主的处理方式
                #if UNITY_REVERSED_Z
                o.vertex.z = min(o.vertex.z, o.vertex.w * UNITY_NEAR_CLIP_VALUE);
                #else
    			o.vertex.z = max(o.vertex.z, o.vertex.w * UNITY_NEAR_CLIP_VALUE);
                #endif
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            real4 frag(v2f i) : SV_Target
            {
                #if _ALPHATEST_ON
                half4 col = tex2D(_MainTex, i.uv);
                clip(col.a - 0.001);
                #endif
                return 0;
            }
            ENDHLSL
        }

    }
}