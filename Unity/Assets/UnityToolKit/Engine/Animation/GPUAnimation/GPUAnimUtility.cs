using Unity.Mathematics;
using UnityEngine;
using UnityToolKit.Engine.Render;

namespace UnityToolKit.Engine.Animation
{
    public static class GPUAnimUtil
    {
        #region ShaderProperties

        static readonly int IDAnimationTex = Shader.PropertyToID("_AnimTex");
        static readonly int IDFrameBegin = Shader.PropertyToID("_AnimFrameBegin");
        static readonly int IDFrameEnd = Shader.PropertyToID("_AnimFrameEnd");
        static readonly int IDFrameInterpolate = Shader.PropertyToID("_AnimFrameInterpolate");
        static readonly int IDRowsPerFrame = Shader.PropertyToID("_AnimRowsPerFrame");

        #endregion

        /// <summary>
        /// 对shader设置纹理贴图，同时设置shader的（如何读取纹理贴图）关键字
        /// </summary>
        public static void ApplyMaterial(this GPUAnimationData data, Material sharedMaterial)
        {
            if (data.BakeTextures != null && data.BakeTextures.Length > 0)
                sharedMaterial.SetTexture(IDAnimationTex, data.BakeTextures[0]);
            sharedMaterial.EnableKeywords(data.BakedMode);
        }

        public static void ApplyPropertyBlock(this AnimationTickOutput output, MaterialPropertyBlock block)
        {
            block.SetInt(IDFrameBegin, output.Cur);
            block.SetInt(IDFrameEnd, output.Next);
            block.SetFloat(IDFrameInterpolate, output.Interpolate);
        }

        /// <summary>
        /// 设置顶点模式行折叠参数到 Material（所有实例共享，只需设置一次）
        /// </summary>
        public static void SetRowsPerFrame(Material material, int rowsPerFrame)
        {
            material.SetInt(IDRowsPerFrame, rowsPerFrame);
        }

        /// <summary>
        /// 骨骼压缩格式：每根骨骼占 2 列像素（四元数 + 平移），row=0 为四元数，row=1 为平移
        /// </summary>
        public static int2 GetTransformPixel(int transformIndex, int row, int frame)
        {
            return new int2(transformIndex * 2 + row, frame);
        }

        /// <summary>
        /// 通过 MaterialPropertyBlock 逐实例设置动画纹理（支持多纹理切换）
        /// </summary>
        public static void SetAnimTexture(MaterialPropertyBlock block, Texture2D texture)
        {
            block.SetTexture(IDAnimationTex, texture);
        }

        /// <summary>
        /// 顶点动画行折叠像素坐标映射。
        /// 一帧的线性顶点数据 (vertexCount*2 像素) 折叠成多行，避免纹理横纵比极端。
        /// linearIndex = vertexIndex * 2 + channel (0=Position, 1=Normal)
        /// x = linearIndex % foldedWidth
        /// y = frame * rowsPerFrame + linearIndex / foldedWidth
        /// </summary>
        public static int2 GetVertexPixel(int linearIndex, int frame, int foldedWidth, int rowsPerFrame)
        {
            return new int2(
                linearIndex % foldedWidth,
                frame * rowsPerFrame + linearIndex / foldedWidth
            );
        }
    }
}