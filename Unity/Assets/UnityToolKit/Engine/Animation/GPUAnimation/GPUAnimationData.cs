using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UnityToolKit.Engine.Animation
{
    public class GPUAnimationData : ScriptableObject
    {
        public EGPUAnimationMode BakedMode;
        public Mesh BakedMesh;
        public Texture2D BakeTexture;
        public AnimationTickerClip[] AnimationClips;
        public GPUAnimationExposeBone[] ExposeTransforms;
    }
}
