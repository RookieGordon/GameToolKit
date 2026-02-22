/* 
****************************************************
* 作者：Gordon
* 创建时间：2025/06/22 16:16:26
* 功能描述：动画烘焙的数据定义
****************************************************
*/

using System;
using UnityEngine;

namespace UnityToolKit.Engine.Animation
{
    public enum EGPUAnimationMode
    {
        /// <summary>
        /// 顶点动画
        /// </summary>
        _ANIM_VERTEX = 1,
        /// <summary>
        /// 骨骼动画
        /// </summary>
        _ANIM_BONE = 2,
    }

    [Serializable]
    public struct GPUAnimationExposeBone
    {
        public string Name;
        public int Index;
        public Vector3 Position;
        public Vector3 Direction;
    }
}