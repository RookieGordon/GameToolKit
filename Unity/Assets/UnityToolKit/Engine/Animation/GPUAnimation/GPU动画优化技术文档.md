# GPU动画优化技术文档

## 概述

本项目实现了三种GPU端动画优化技术，旨在将动画计算从CPU迁移到GPU，降低CPU瓶颈，提升大规模角色渲染的性能。

| 技术 | 核心思路 | 适用场景 |
|------|----------|----------|
| **AnimationTexture - 骨骼模式** | 将骨骼矩阵逐帧烘焙到纹理，Shader中采样矩阵进行蒙皮 | 大量同模型角色、单体角色去Animator |
| **AnimationTexture - 顶点模式** | 将变形后的顶点位置/法线逐帧烘焙到纹理，Shader中直接读取 | 顶点数较少的模型、特效型动画 |
| **GPUSkinnedMesh** | 保留Animator，用ComputeShader在GPU端实时执行骨骼蒙皮 | 需要动画混合/IK/Ragdoll但想减轻CPU蒙皮开销 |

---

## 一、AnimationTexture（动画纹理烘焙）

### 1.1 原理

在Editor中将动画数据预烘焙（Bake）到一张纹理贴图中，运行时完全脱离Animator/Animation系统，由自定义的`AnimationTicker`按时间轴计算当前帧，Shader从纹理采样数据完成顶点变换。

**纹理布局：**
- **U轴（宽度）**：数据序号（骨骼/顶点）
- **V轴（高度）**：帧序号（所有动画片段依次排列）
- **格式**：`RGBAHalf`（16位半精度浮点，精度足够且省显存）

### 1.2 骨骼模式（`_ANIM_BONE`）

**烘焙流程：**
1. 实例化FBX模型，获取`SkinnedMeshRenderer`
2. 逐帧`SampleAnimation` → 获取每个骨骼的`localToWorldMatrix × bindPose`，得到3×4矩阵
3. 每个骨骼占3个像素（矩阵的3行），写入纹理
4. 将骨骼索引写入Mesh的UV1，骨骼权重写入UV2
5. 清除Mesh的`boneWeights`和`bindposes`（不再需要CPU蒙皮）

**纹理宽度** = `骨骼数 × 3`（向上取2的幂次）

**Shader端：**
```hlsl
// 采样第sampleFrame帧、第transformIndex个骨骼的变换矩阵
float4x4 SampleTransformMatrix(uint sampleFrame, uint transformIndex)
{
    float2 index = float2(.5 + transformIndex * 3, .5 + sampleFrame);
    return float4x4(
        SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex, index * _AnimTex_TexelSize.xy, 0),
        SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex, (index + float2(1,0)) * _AnimTex_TexelSize.xy, 0),
        SAMPLE_TEXTURE2D_LOD(_AnimTex, sampler_AnimTex, (index + float2(2,0)) * _AnimTex_TexelSize.xy, 0),
        float4(0, 0, 0, 1)
    );
}
```

**优点：**
- 支持多骨骼加权混合，动画质量与原始Animator一致
- 纹理尺寸较小（宽度仅与骨骼数相关）
- 支持暴露骨骼节点（Expose Bone），可挂载武器等

**缺点：**
- 需要在UV1/UV2中额外存储骨骼索引和权重
- Shader端每个顶点需要采样多次纹理（4个骨骼 × 3行 × 2帧 = 24次采样）

### 1.3 顶点模式（`_ANIM_VERTEX`）

**烘焙流程：**
1. 实例化FBX模型，获取`SkinnedMeshRenderer`
2. 逐帧`SampleAnimation` → `BakeMesh` → 获取变形后的顶点位置和法线
3. 每个顶点占2个像素（位置 + 法线），写入纹理
4. 清除Mesh的法线、切线、骨骼数据

**纹理宽度** = `顶点数 × 2`（向上取2的幂次）

**Shader端：**
```hlsl
// 通过 SV_VertexID 索引顶点数据
void SampleVertex(uint vertexID, inout float3 positionOS, inout float3 normalOS)
{
    positionOS = lerp(
        SamplePosition(vertexID, _FrameBegin),
        SamplePosition(vertexID, _FrameEnd),
        _FrameInterpolate);
    normalOS = lerp(
        SampleNormal(vertexID, _FrameBegin),
        SampleNormal(vertexID, _FrameEnd),
        _FrameInterpolate);
}
```

**优点：**
- 不需要UV1/UV2存储骨骼数据
- 每个顶点只需4次纹理采样（2帧 × 2数据）
- 支持任意变形（不限于骨骼蒙皮，如布料、Blendshape等）

**缺点：**
- 纹理尺寸大（宽度与顶点数成正比）
- 高面数模型的纹理可能非常大，占用显存
- 不支持暴露骨骼节点

### 1.4 运行时使用

#### 单体模式（GPUAnimationController）

```csharp
// 挂载 GPUAnimationController 组件
// 设置 GPUAnimData（烘焙产物）和 Material（使用 UnityToolKit/GPUAnimation shader）

controller.Init();
controller.SetAnimation(0);           // 设置动画索引
controller.Tick(Time.deltaTime);      // 每帧驱动
controller.OnAnimEvent = (name) => {} // 监听动画事件
```

#### GPU Instancing 批量模式

```csharp
// 创建 AnimationTicker 数组驱动每个实例
// 通过 MaterialPropertyBlock + FloatArray 批量传递帧参数
// 使用 Graphics.DrawMeshInstanced 一次DrawCall渲染所有实例

block.SetFloatArray("_AnimFrameBegin", curFrames);
block.SetFloatArray("_AnimFrameEnd", nextFrames);
block.SetFloatArray("_AnimFrameInterpolate", interpolates);
Graphics.DrawMeshInstanced(mesh, 0, material, matrices, count, block);
```

### 1.5 Shader关键字机制

枚举 `EGPUAnimationMode` 的值名称直接作为Shader关键字：
- `_ANIM_BONE` → 启用骨骼动画分支
- `_ANIM_VERTEX` → 启用顶点动画分支

通过 `MaterialKeywordExtension.EnableKeywords()` 自动根据枚举值启用/禁用对应关键字。Shader中通过 `#pragma shader_feature_local _ANIM_BONE _ANIM_VERTEX` 声明变体。

### 1.6 动画事件

`AnimationTickEvent` 在烘焙时从 `AnimationClip.events` 提取事件的帧位置和名称。运行时 `AnimationTicker.TickEvents()` 在帧推进的窗口内检测事件触发，支持循环动画的重复触发。

---

## 二、GPUSkinnedMesh（GPU蒙皮网格）

### 2.1 原理

保留Unity原有的Animator/Animation系统驱动骨骼运动，但将蒙皮变换（骨骼矩阵 × 顶点）从CPU迁移到GPU。通过ComputeShader在GPU端执行4骨骼加权蒙皮计算。

**流程：**
1. 初始化时将原始顶点、法线、骨骼权重上传到GPU Buffer
2. 每帧（LateUpdate）：
   - CPU端计算骨骼矩阵 = `rootWorldToLocal × bone.localToWorld × bindPose`
   - 将骨骼矩阵上传到ComputeBuffer
   - Dispatch ComputeShader执行蒙皮
   - 回读结果到Mesh

### 2.2 ComputeShader核心

```hlsl
[numthreads(64, 1, 1)]
void CSSkinning(uint3 id : SV_DispatchThreadID)
{
    // 遍历4个骨骼权重
    for (int i = 0; i < 4; i++)
    {
        float4x4 boneMatrix = _BoneMatrixBuffer[boneIndex];
        skinnedPos += mul(boneMatrix, float4(vertex.Position, 1.0)).xyz * weight;
        skinnedNormal += mul((float3x3)boneMatrix, vertex.Normal) * weight;
    }
}
```

### 2.3 使用方式

```csharp
// 1. 保留原始的 SkinnedMeshRenderer + Animator
// 2. 在同一 GameObject 上添加 GPUSkinnedMeshRenderer
// 3. 设置 SourceSkin 指向原始 SkinnedMeshRenderer
// 4. 设置 SkinningShader 指向 GPUSkinning.compute
// 5. 原始 SkinnedMeshRenderer 可隐藏（禁用渲染但保留骨骼驱动）
```

### 2.4 优缺点

**优点：**
- 保留完整的Animator功能（动画混合、过渡、IK、状态机）
- 骨骼矩阵数量远小于顶点数，CPU→GPU传输量很小
- 支持Ragdoll、程序化动画等运行时骨骼修改

**缺点：**
- 仍需CPU端计算骨骼矩阵（只免去了蒙皮计算）
- 当前实现使用 `GetData` 回读，存在GPU→CPU同步开销
- 不适合GPU Instancing批量渲染（每个实例骨骼状态不同）
- Animator本身的开销未消除

---

## 三、技术对比与选型指南

| 维度 | AnimationTexture 骨骼模式 | AnimationTexture 顶点模式 | GPUSkinnedMesh |
|------|--------------------------|--------------------------|----------------|
| **CPU开销** | 极低（仅TimerTick） | 极低（仅TimerTick） | 中（Animator + 矩阵计算） |
| **GPU开销** | 中（多次纹理采样） | 低（少量纹理采样） | 低（ComputeShader分发） |
| **显存占用** | 小（与骨骼数成正比） | 大（与顶点数成正比） | 小（Buffer开销） |
| **动画混合** | 不支持 | 不支持 | 完整支持 |
| **IK/Ragdoll** | 不支持 | 不支持 | 支持 |
| **GPU Instancing** | 支持（关键优势） | 支持 | 不适用 |
| **动画精度** | 高 | 高 | 与原始一致 |
| **挂载点** | 支持（Expose Bone） | 不支持 | 支持（原始骨骼） |
| **适用模型** | 骨骼蒙皮模型 | 低面数模型 | 任意蒙皮模型 |
| **预处理** | 需要Editor烘焙 | 需要Editor烘焙 | 无需预处理 |

### 推荐选型

| 场景 | 推荐方案 |
|------|----------|
| 大量同模型NPC/小兵（100+） | AnimationTexture 骨骼模式 + GPU Instancing |
| 低面数粒子效果般的角色动画 | AnimationTexture 顶点模式 |
| 主角/Boss（需要IK、动画混合） | GPUSkinnedMesh 或保留原始Animator |
| 中等数量不同模型角色（10~50） | GPUSkinnedMesh |
| 场景装饰动画（旗帜、植物） | AnimationTexture 顶点模式 |

---

## 四、文件结构

```
Engine/Animation/GPUAnimation/
├── GPUAnimDefine.cs              # 枚举、数据结构定义
├── GPUAnimUtility.cs             # Shader属性设置、像素坐标计算
├── AnimationTex/                 # 动画纹理方案
│   ├── AnimationTicker.cs        # 动画计时器（帧计算、事件触发）
│   ├── GPUAnimationController.cs # 运行时控制器组件
│   └── Shader/
│       ├── GPUAnimationInclude.hlsl  # 动画纹理采样函数库
│       └── GPUAnimation_Example.shader # 示例Shader（URP）
├── GPUSkinnedMesh/               # GPU蒙皮方案
│   ├── GPUSkinnedMeshRenderer.cs # GPU蒙皮渲染器组件
│   └── Shader/
│       └── GPUSkinning.compute   # 蒙皮ComputeShader
└── GPUAnimation/                 # [遗留] storm team的另一套VAT实现
    └── Shader/                   #        使用CBUFFER+SV_InstanceID方案

Editor/Animation/
├── AnimationBakerWindow.cs                # 烘焙窗口主体（UI、公共方法）
├── AnimationBakerWindow_BoneBaker.cs      # 骨骼模式烘焙逻辑
├── AnimatonBakerWindow_VertexBaker.cs     # 顶点模式烘焙逻辑
└── GPUAnimationControllerEditor.cs        # Controller Inspector预览
```
