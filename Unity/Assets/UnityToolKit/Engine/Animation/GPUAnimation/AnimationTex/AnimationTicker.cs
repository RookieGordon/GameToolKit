/* 
****************************************************
* 作者：Gordon
* 创建时间：2025/06/22 17:39:49
* 功能描述：动画烘焙的数据
****************************************************
*/

using System;
using ToolKit.Common;
using UnityEngine;

namespace UnityToolKit.Engine.Animation
{
    /// <summary>
    /// 动画片段数据
    /// </summary>
    [Serializable]
    public struct AnimationTickerClip
    {
        public string Name;
        /// <summary>
        /// 当前动画片段，在纹理贴图中的高度轴的起始位置
        /// </summary>
        public int FrameBegin;
        public int FrameCount;
        public bool Loop;

        public float Length;
        public float FrameRate;
        public AnimationTickEvent[] Events;

        public AnimationTickerClip(string name, int startFrame, float frameRate, float length, bool loop,
            AnimationTickEvent[] events)
        {
            Name = name;
            FrameBegin = startFrame;
            FrameRate = frameRate;
            FrameCount = (int)(frameRate * length);
            Events = events;
            Length = length;
            Loop = loop;
        }
    }

    [Serializable]
    public struct AnimationTickEvent
    {
        /// <summary>
        /// 当前事件触发时的帧数
        /// </summary>
        public float keyFrame;
        /// <summary>
        /// 事件名
        /// </summary>
        public string identity;

        public AnimationTickEvent(AnimationEvent aniEvent, float frameRate)
        {
            keyFrame = aniEvent.time * frameRate;
            identity = aniEvent.functionName;
        }
    }

    public struct AnimationTickOutput
    {
        public int Cur;
        public int Next;
        public float Interpolate;
    }

    public class AnimationTicker : IClearable
    {
        public int AnimIndex { get; private set; }

        /// <summary>
        /// 时间轴
        /// </summary>
        public float TimeElapsed { get; private set; }

        public AnimationTickerClip Anim => _animations[AnimIndex];
        private AnimationTickerClip[] _animations;

        public void Setup(AnimationTickerClip[] animations)
        {
            _animations = animations;
        }

        public void Clear()
        {
            AnimIndex = 0;
            TimeElapsed = 0;
        }

        public void SetTime(float time) => TimeElapsed = time;

        public void SetNormalizedTime(float scale)
        {
            if (AnimIndex < 0 || AnimIndex >= _animations.Length)
            {
                return;
            }

            TimeElapsed = _animations[AnimIndex].Length * scale;
        }

        public float GetNormalizedTime()
        {
            if (AnimIndex < 0 || AnimIndex >= _animations.Length)
            {
                return 0f;
            }

            return TimeElapsed / _animations[AnimIndex].Length;
        }

        public void SetAnimation(int animIndex)
        {
            TimeElapsed = 0f;
            if (animIndex < 0 || animIndex >= _animations.Length)
            {
                UnityEngine.Debug.LogError($"Invalid Animation Index Found: {animIndex}");
                return;
            }

            AnimIndex = animIndex;
        }

        public bool Tick(float deltaTime, out AnimationTickOutput output, Action<string> onEvents = null)
        {
            output = default;
            if (AnimIndex < 0 || AnimIndex >= _animations.Length)
            {
                return false;
            }

            AnimationTickerClip param = _animations[AnimIndex];
            if (onEvents != null)
            {
                TickEvents(param, TimeElapsed, deltaTime, onEvents);
            }

            TimeElapsed += deltaTime;

            int curFrame = 0;
            int nextFrame = 0;
            if (param.Loop)
            {
                curFrame = (int)(TimeElapsed * param.FrameRate) % param.FrameCount;
                nextFrame = (curFrame + 1) % param.FrameCount;
            }
            else
            {
                curFrame = Mathf.Clamp((int)(TimeElapsed * param.FrameRate), 0, param.FrameCount - 1);
                nextFrame = Mathf.Clamp(curFrame + 1, 0, param.FrameCount - 1);
            }

            float framePassed = TimeElapsed * param.FrameRate - curFrame;

            output.Cur = curFrame + param.FrameBegin;
            output.Next = nextFrame + param.FrameBegin;
            output.Interpolate = framePassed / param.FrameRate;

            return true;
        }

        private static void TickEvents(AnimationTickerClip param, float timeElapsed, float deltaTime,
            Action<string> onEvents)
        {
            if (param.Events == null || param.Events.Length <= 0)
            {
                return;
            }

            foreach (var aniEvent in param.Events)
            {
                if (aniEvent.keyFrame < timeElapsed && aniEvent.keyFrame >= timeElapsed - deltaTime)
                {
                    onEvents?.Invoke(aniEvent.identity);
                }
            }
        }
    }
}