/*
 * GPU动画 Shader Include
 * 借鉴自 Unity3D-ToolChain_StriteR
 * 
 * 【用法】
 *   在 Shader 中 #include 此文件，并添加：
 *   #pragma shader_feature_local _ ANIM_BONE ANIM_VERTEX
 *   #pragma multi_compile_instancing
 *
 * 【数据布局】
 *   动画数据存储在一张 Texture2D(_AnimTex) 中，格式 RGBAHalf，FilterMode = Point。
 *   · X 轴 (宽度方向): 骨骼/顶点的数据通道
 *   · Y 轴 (高度方向): 帧序号（多个 AnimationClip 纵向拼接）
 *
 *   骨骼模式(_ANIM_BONE):
 *     每根骨骼占 2 列像素（压缩存储：四元数 XYZW + 平移 XYZ），
 *     相比原始矩阵行存储（3 像素）减少 33% 纹理宽度
 *     像素坐标: X = boneIndex * 2 + 0 (四元数),  X = boneIndex * 2 + 1 (平移),  Y = frame
 *     Shader 中通过 QuatTransToMatrix() 将四元数+平移重建为 4x4 矩阵
 *     Mesh 的 UV1(TEXCOORD1) 存储 4 个骨骼索引，UV2(TEXCOORD2) 存储 4 个骨骼权重
 *
 *   顶点模式(_ANIM_VERTEX):
 *     每个顶点需 2 个像素（Position + Normal），每帧共 vertexCount*2 个像素。
 *     行折叠（Row Folding）：将每帧的线性像素序列折叠成多行，避免纹理横纵比极端（如 10000×60）。
 *     折叠后坐标：
 *       linearIndex = vertexID * 2 + channel  (0=Pos, 1=Nrm)
 *       pixelX = linearIndex % texWidth
 *       pixelY = frame * _AnimRowsPerFrame + linearIndex / texWidth
 *     _AnimRowsPerFrame = ceil(vertexCount * 2 / texWidth)，由 CPU 端烘焙时计算并设置到 Material。
 *     通过 SV_VertexID 索引，无需额外 UV 通道。
 *
 * 【帧插值】
 *   CPU 端计算 _AnimFrameBegin / _AnimFrameEnd / _AnimFrameInterpolate，
 *   Shader 中对两帧采样结果做 lerp 线性插值，保证动画平滑。
 *   这三个参数声明在 UNITY_INSTANCING_BUFFER 中，支持 GPU Instancing。
 *
 * 【NPOT 纹理与 Clamp WrapMode】
 *   纹理尺寸使用 NPOT（Non-Power-Of-Two），避免 NextPowerOfTwo 导致的大量空间浪费。
 *   例如原始数据 130×500，POT 对齐后膨胀为 256×512；而 2049×900 更会膨胀到 4096×1024。
 *
 *   为了兼容 OpenGL ES 2.0 等旧设备 GPU，纹理 U/V 轴的 WrapMode 统一设为 Clamp。
 *   旧 GPU 的 "limited NPOT" 限制要求 NPOT 纹理必须使用 Clamp，否则 Repeat 采样
 *   会导致 incomplete texture（全黑/噪点/未定义行为）。
 *
 *   循环动画的帧回绕不依赖 GPU 的 Repeat 模式——CPU 端 AnimationTicker 已对帧索引
 *   做了取模运算（curFrame % FrameCount），传入 Shader 的帧索引始终在 [0, totalFrames)
 *   范围内，UV 永远不会超出 [0, 1) 区间。
 *
 * 【像素中心偏移 +0.5】
 *   纹理 UV 坐标 (0,0) 对应纹理左下角，而非第一个像素的中心。
 *   在 Point Filter 模式下，若 UV 恰好落在像素边界，可能采样到相邻像素。
 *   因此所有采样坐标在整数像素坐标上加 0.5，确保命中像素中心：
 *     uv = (pixelCoord + 0.5) * _AnimTex_TexelSize.xy
 */

#ifndef GPU_ANIMATION_INCLUDE_HLSL
#define GPU_ANIMATION_INCLUDE_HLSL

// ======================== 公共声明 ========================

TEXTURE2D(_AnimTex);
SAMPLER(sampler_AnimTex);
float4 _AnimTex_TexelSize;  // (1/width, 1/height, width, height)

// 逐实例帧参数（通过 MaterialPropertyBlock 设置，支持 GPU Instancing）
UNITY_INSTANCING_BUFFER_START(PropsGPUAnim)
    UNITY_DEFINE_INSTANCED_PROP(int, _AnimFrameBegin)       // 当前帧行号
    UNITY_DEFINE_INSTANCED_PROP(int, _AnimFrameEnd)         // 下一帧行号
    UNITY_DEFINE_INSTANCED_PROP(float, _AnimFrameInterpolate) // 两帧间插值因子 [0, 1)
UNITY_INSTANCING_BUFFER_END(PropsGPUAnim)

// 顶点模式行折叠参数（通过 Material 设置，所有实例共享）
// = ceil(vertexCount * 2 / texWidth)，骨骼模式始终为 1
int _AnimRowsPerFrame;

#define _FrameBegin       UNITY_ACCESS_INSTANCED_PROP(PropsGPUAnim, _AnimFrameBegin)
#define _FrameEnd         UNITY_ACCESS_INSTANCED_PROP(PropsGPUAnim, _AnimFrameEnd)
#define _FrameInterpolate UNITY_ACCESS_INSTANCED_PROP(PropsGPUAnim, _AnimFrameInterpolate)

// ======================== 骨骼动画模式 (ANIM_BONE) ========================

/// 从四元数和平移向量重建 4x4 变换矩阵
/// 骨骼数据压缩: 每根骨骼每帧仅用 2 像素（四元数 XYZW + 平移 XYZ），
/// 替代原先的 3 像素（矩阵的 3 行），纹理宽度减少 33%
float4x4 QuatTransToMatrix(float4 q, float3 t)
{
    float x2 = q.x * 2, y2 = q.y * 2, z2 = q.z * 2;
    float xx = q.x * x2, xy = q.x * y2, xz = q.x * z2;
    float yy = q.y * y2, yz = q.y * z2, zz = q.z * z2;
    float wx = q.w * x2, wy = q.w * y2, wz = q.w * z2;

    return float4x4(
        1 - yy - zz,  xy - wz,      xz + wy,      t.x,
        xy + wz,      1 - xx - zz,  yz - wx,       t.y,
        xz - wy,      yz + wx,      1 - xx - yy,   t.z,
        0,            0,            0,              1
    );
}

/// 采样单根骨骼在指定帧的蒙皮矩阵
/// 纹理中每根骨骼存储 2 个像素: 像素0 = 四元数(XYZW), 像素1 = 平移(XYZ)
/// @param sampleFrame     帧序号（纹理 Y 轴行号）
/// @param transformIndex  骨骼序号
/// @return 4x4 蒙皮矩阵 (从四元数+平移重建)
float4x4 SampleTransformMatrix(uint sampleFrame, uint transformIndex)
{
    // +0.5: 像素中心偏移，避免 Point Filter 在边界采样错误
    float2 index = float2(transformIndex * 2 + .5h, sampleFrame + .5h);
    // 像素 0: 四元数 (x, y, z, w)
    float4 q = SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex, index * _AnimTex_TexelSize.xy, 0);
    // 像素 1: 平移 (x, y, z, _)
    float3 t = SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex, (index + float2(1, 0)) * _AnimTex_TexelSize.xy, 0).xyz;
    return QuatTransToMatrix(q, t);
}

/// 采样 4 根骨骼的蒙皮矩阵并按权重加权混合
/// @param sampleFrame      帧序号
/// @param transformIndex   uint4，4 个骨骼索引（来自 Mesh UV1 / TEXCOORD1）
/// @param transformWeights float4，4 个骨骼权重（来自 Mesh UV2 / TEXCOORD2，总和为 1）
/// @return 加权混合后的蒙皮矩阵
float4x4 SampleTransformMatrix(uint sampleFrame, uint4 transformIndex, float4 transformWeights)
{
    return SampleTransformMatrix(sampleFrame, transformIndex.x) * transformWeights.x
         + SampleTransformMatrix(sampleFrame, transformIndex.y) * transformWeights.y
         + SampleTransformMatrix(sampleFrame, transformIndex.z) * transformWeights.z
         + SampleTransformMatrix(sampleFrame, transformIndex.w) * transformWeights.w;
}

/// 骨骼动画蒙皮入口：采样前后两帧的加权矩阵，lerp 插值后变换顶点位置和法线
/// @param transformIndexes  4 骨骼索引 (TEXCOORD1)
/// @param transformWeights  4 骨骼权重 (TEXCOORD2)
/// @param positionOS        [inout] 模型空间顶点位置，就地修改为蒙皮后的位置
/// @param normalOS          [inout] 模型空间法线，就地修改为蒙皮后的法线（已归一化）
void SampleTransform(uint4 transformIndexes, float4 transformWeights, inout float3 positionOS, inout float3 normalOS)
{
    // 对 FrameBegin 和 FrameEnd 分别采样加权混合矩阵，再按插值因子 lerp
    float4x4 sampleMatrix = lerp(
        SampleTransformMatrix(_FrameBegin, transformIndexes, transformWeights),
        SampleTransformMatrix(_FrameEnd,   transformIndexes, transformWeights),
        _FrameInterpolate
    );

    positionOS = mul(sampleMatrix, float4(positionOS, 1)).xyz;
    // 矩阵变换 + lerp 后法线不再是单位向量，必须归一化
    normalOS = normalize(mul((float3x3)sampleMatrix, normalOS));
}

// ======================== 顶点动画模式 (ANIM_VERTEX) ========================
//
// 行折叠（Row Folding）：将每帧 vertexCount*2 像素的线性数据折叠成多行，
// 避免纹理宽度 = vertexCount*2 导致的极端横纵比。
// 坐标映射：
//   linearIndex = vertexID * 2 + channel  (0=Position, 1=Normal)
//   pixelX = linearIndex % texWidth       (texWidth = _AnimTex_TexelSize.z)
//   pixelY = frame * _AnimRowsPerFrame + linearIndex / texWidth

/// 采样指定顶点在指定帧的位置（行折叠坐标）
float3 SamplePosition(uint vertexID, uint frame)
{
    uint texWidth = (uint)_AnimTex_TexelSize.z;
    uint linearIdx = vertexID * 2;
    float2 uv = float2(
        (linearIdx % texWidth + .5) * _AnimTex_TexelSize.x,
        (frame * _AnimRowsPerFrame + linearIdx / texWidth + .5) * _AnimTex_TexelSize.y);
    return SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex, uv, 0).xyz;
}

/// 采样指定顶点在指定帧的法线（行折叠坐标）
float3 SampleNormal(uint vertexID, uint frame)
{
    uint texWidth = (uint)_AnimTex_TexelSize.z;
    uint linearIdx = vertexID * 2 + 1;
    float2 uv = float2(
        (linearIdx % texWidth + .5) * _AnimTex_TexelSize.x,
        (frame * _AnimRowsPerFrame + linearIdx / texWidth + .5) * _AnimTex_TexelSize.y);
    return SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex, uv, 0).xyz;
}

/// 顶点动画入口：采样前后两帧的顶点位置和法线，lerp 插值
/// @param vertexID   顶点序号 (SV_VertexID)
/// @param positionOS [inout] 模型空间顶点位置，就地替换为动画帧中的位置
/// @param normalOS   [inout] 模型空间法线，就地替换为动画帧中的法线（已归一化）
void SampleVertex(uint vertexID, inout float3 positionOS, inout float3 normalOS)
{
    positionOS = lerp(SamplePosition(vertexID, _FrameBegin),
                      SamplePosition(vertexID, _FrameEnd),
                      _FrameInterpolate);
    // lerp 后法线可能不再是单位向量，归一化保证光照计算正确
    normalOS = normalize(lerp(SampleNormal(vertexID, _FrameBegin),
                              SampleNormal(vertexID, _FrameEnd),
                              _FrameInterpolate));
}

#endif // GPU_ANIMATION_INCLUDE_HLSL
