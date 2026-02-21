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

        #endregion

        /// <summary>
        /// 对shader设置纹理贴图，同时设置shader的（如何读取纹理贴图）关键字
        /// </summary>
        public static void ApplyMaterial(this GPUAnimationData data, Material sharedMaterial)
        {
            sharedMaterial.SetTexture(IDAnimationTex, data.BakeTexture);
            sharedMaterial.EnableKeywords(data.BakedMode);
        }

        public static void ApplyPropertyBlock(this AnimationTickOutput output, MaterialPropertyBlock block)
        {
            block.SetInt(IDFrameBegin, output.Cur);
            block.SetInt(IDFrameEnd, output.Next);
            block.SetFloat(IDFrameInterpolate, output.Interpolate);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="transformIndex"></param>
        /// <param name="row"></param>
        /// <param name="frame"></param>
        /// <returns></returns>
        public static int2 GetTransformPixel(int transformIndex, int row, int frame)
        {
            return new int2(transformIndex * 3 + row, frame);
        }

        public static int2 GetVertexPositionPixel(int vertexIndex, int frame)
        {
            return new int2(vertexIndex * 2, frame);
        }

        public static int2 GetVertexNormalPixel(int vertexIndex, int frame)
        {
            return new int2(vertexIndex * 2 + 1, frame);
        }
    }
}