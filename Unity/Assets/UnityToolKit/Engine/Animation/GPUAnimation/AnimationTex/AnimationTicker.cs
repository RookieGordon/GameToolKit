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
        /// <summary>
        /// 该动画片段所属的 Atlas 纹理索引（对应 GPUAnimationData.BakeTextures 数组下标）
        /// </summary>
        public int TextureIndex;
        public AnimationTickEvent[] Events;

        public AnimationTickerClip(string name, int startFrame, float frameRate, float length, bool loop,
            AnimationTickEvent[] events, int textureIndex = 0)
        {
            Name = name;
            FrameBegin = startFrame;
            FrameRate = frameRate;
            FrameCount = (int)(frameRate * length);
            Events = events;
            Length = length;
            Loop = loop;
            TextureIndex = textureIndex;
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
            if (_animations == null || animIndex < 0 || animIndex >= _animations.Length)
            {
                UnityEngine.Debug.LogError($"Invalid Animation Index Found: {animIndex}");
                return;
            }

            AnimIndex = animIndex;
        }

        public bool Tick(float deltaTime, out AnimationTickOutput output, Action<string> onEvents = null)
        {
            output = default;
            if (_animations == null || AnimIndex < 0 || AnimIndex >= _animations.Length)
            {
                return false;
            }

            AnimationTickerClip param = _animations[AnimIndex];
            if (onEvents != null)
            {
                TickEvents(param, TimeElapsed, deltaTime, onEvents);
            }

            TimeElapsed += deltaTime;

            int curFrame;
            int nextFrame;
            float framePassed;
            if (param.Loop)
            {
                framePassed = (TimeElapsed % param.Length) * param.FrameRate;
                curFrame = Mathf.FloorToInt(framePassed) % param.FrameCount;
                nextFrame = (curFrame + 1) % param.FrameCount;
            }
            else
            {
                framePassed = Mathf.Min(param.Length, TimeElapsed) * param.FrameRate;
                curFrame = Mathf.Min(Mathf.FloorToInt(framePassed), param.FrameCount - 1);
                nextFrame = Mathf.Min(curFrame + 1, param.FrameCount - 1);
            }

            curFrame += param.FrameBegin;
            nextFrame += param.FrameBegin;
            framePassed %= 1f;

            output = new AnimationTickOutput
            {
                Cur = curFrame,
                Next = nextFrame,
                Interpolate = framePassed
            };

            return true;
        }

        private static void TickEvents(AnimationTickerClip param, float timeElapsed, float deltaTime,
            Action<string> onEvents)
        {
            if (param.Events == null || param.Events.Length <= 0)
            {
                return;
            }

            float lastFrame = timeElapsed * param.FrameRate;
            float nextFrame = lastFrame + deltaTime * param.FrameRate;

            // 对于循环动画，需要计算循环偏移量，使事件在每次循环时都能正确触发
            float checkOffset = param.Loop
                ? param.FrameCount * Mathf.Floor(nextFrame / param.FrameCount)
                : 0f;

            foreach (var aniEvent in param.Events)
            {
                float frameCheck = checkOffset + aniEvent.keyFrame;
                // 首次 Tick 时 timeElapsed == 0，使用闭区间 [0, nextFrame] 以包含起始帧事件；
                // 后续 Tick 使用半开区间 (lastFrame, nextFrame] 避免同一事件重复触发。
                bool inRange = timeElapsed <= 0f
                    ? (frameCheck >= lastFrame && frameCheck <= nextFrame)
                    : (frameCheck > lastFrame && frameCheck <= nextFrame);
                if (inRange)
                {
                    onEvents?.Invoke(aniEvent.identity);
                }
            }
        }
    }
}