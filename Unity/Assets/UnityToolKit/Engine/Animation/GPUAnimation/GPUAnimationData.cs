using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UnityToolKit.Engine.Animation
{
    public class GPUAnimationData : ScriptableObject
    {
        public EGPUAnimationMode BakedMode;
        public Mesh BakedMesh;
        public Texture2D[] BakeTextures;
        public AnimationTickerClip[] AnimationClips;
        public GPUAnimationExposeBone[] ExposeTransforms;

        /// <summary>
        /// 顶点模式行折叠参数：每个逻辑帧占据的纹理行数。
        /// 骨骼模式始终为 1（不折叠），顶点模式 = ceil(vertexCount * 2 / foldedWidth)。
        /// </summary>
        public int RowsPerFrame = 1;
    }
}
