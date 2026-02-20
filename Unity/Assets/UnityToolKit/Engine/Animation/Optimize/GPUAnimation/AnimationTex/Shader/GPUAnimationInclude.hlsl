/*
 * GPU动画 Shader Include
 * 借鉴自 Unity3D-ToolChain_StriteR 
 * 
 * 用法：在shader中 #include 此文件
 * 需要配合 #pragma multi_compile_local _ _ANIM_BONE _ANIM_VERTEX
 * 
 * 骨骼动画模式(_ANIM_BONE)：从纹理中采样骨骼矩阵，通过UV1/UV2传递骨骼索引和权重
 * 顶点动画模式(_ANIM_VERTEX)：从纹理中采样顶点位置和法线
 */

#ifndef GPU_ANIMATION_INCLUDE_HLSL
#define GPU_ANIMATION_INCLUDE_HLSL

TEXTURE2D(_AnimTex);
SAMPLER(sampler_AnimTex);
float4 _AnimTex_TexelSize;

UNITY_INSTANCING_BUFFER_START(PropsGPUAnim)
    UNITY_DEFINE_INSTANCED_PROP(int, _AnimFrameBegin)
    UNITY_DEFINE_INSTANCED_PROP(int, _AnimFrameEnd)
    UNITY_DEFINE_INSTANCED_PROP(float, _AnimFrameInterpolate)
UNITY_INSTANCING_BUFFER_END(PropsGPUAnim)

#define _FrameBegin UNITY_ACCESS_INSTANCED_PROP(PropsGPUAnim, _AnimFrameBegin)
#define _FrameEnd UNITY_ACCESS_INSTANCED_PROP(PropsGPUAnim, _AnimFrameEnd)
#define _FrameInterpolate UNITY_ACCESS_INSTANCED_PROP(PropsGPUAnim, _AnimFrameInterpolate)

// ======================== 骨骼动画模式 ========================

/// 采样单个骨骼的变换矩阵（3行存储为3个像素）
float4x4 SampleTransformMatrix(uint sampleFrame, uint transformIndex)
{
    float2 index = float2(.5h + transformIndex * 3, .5h + sampleFrame);
    return float4x4(
        SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex, index * _AnimTex_TexelSize.xy, 0),
        SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex, (index + float2(1, 0)) * _AnimTex_TexelSize.xy, 0),
        SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex, (index + float2(2, 0)) * _AnimTex_TexelSize.xy, 0),
        float4(0, 0, 0, 1)
    );
}

/// 采样多骨骼加权混合矩阵
float4x4 SampleTransformMatrix(uint sampleFrame, uint4 transformIndex, float4 transformWeights)
{
    return SampleTransformMatrix(sampleFrame, transformIndex.x) * transformWeights.x
        + SampleTransformMatrix(sampleFrame, transformIndex.y) * transformWeights.y
        + SampleTransformMatrix(sampleFrame, transformIndex.z) * transformWeights.z
        + SampleTransformMatrix(sampleFrame, transformIndex.w) * transformWeights.w;
}

/// 骨骼动画采样：对位置和法线进行蒙皮变换（含帧插值）
void SampleTransform(uint4 transformIndexes, float4 transformWeights, inout float3 positionOS, inout float3 normalOS)
{
    float4x4 sampleMatrix = lerp(
        SampleTransformMatrix(_FrameBegin, transformIndexes, transformWeights),
        SampleTransformMatrix(_FrameEnd, transformIndexes, transformWeights),
        _FrameInterpolate
    );
    normalOS = mul((float3x3)sampleMatrix, normalOS);
    positionOS = mul(sampleMatrix, float4(positionOS, 1)).xyz;
}

// ======================== 顶点动画模式 ========================

/// 采样顶点位置
float3 SamplePosition(uint vertexID, uint frame)
{
    return SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex,
        float2((vertexID * 2 + .5) * _AnimTex_TexelSize.x, frame * _AnimTex_TexelSize.y), 0).xyz;
}

/// 采样顶点法线
float3 SampleNormal(uint vertexID, uint frame)
{
    return SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex,
        float2((vertexID * 2 + 1 + .5) * _AnimTex_TexelSize.x, frame * _AnimTex_TexelSize.y), 0).xyz;
}

/// 顶点动画采样：直接从纹理读取顶点位置和法线（含帧插值）
void SampleVertex(uint vertexID, inout float3 positionOS, inout float3 normalOS)
{
    positionOS = lerp(SamplePosition(vertexID, _FrameBegin), SamplePosition(vertexID, _FrameEnd), _FrameInterpolate);
    normalOS = lerp(SampleNormal(vertexID, _FrameBegin), SampleNormal(vertexID, _FrameEnd), _FrameInterpolate);
}

#endif // GPU_ANIMATION_INCLUDE_HLSL
