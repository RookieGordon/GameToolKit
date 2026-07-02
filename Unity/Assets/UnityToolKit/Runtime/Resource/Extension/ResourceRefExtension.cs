using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.Video;

namespace UnityToolKit.Runtime.Resource
{
    public static class ResourceRefExtension
    {
        public static GameObject GetGameObject(this ResourceRef resourceRef)
        {
            return resourceRef.Get<GameObject>();
        }

        public static TextAsset GetTextAsset(this ResourceRef resourceRef)
        {
            return resourceRef.Get<TextAsset>();
        }

        public static VideoClip GetVideoClip(this ResourceRef resourceRef)
        {
            return resourceRef.Get<VideoClip>();
        }

        public static AudioClip GetAudioClip(this ResourceRef resourceRef)
        {
            return resourceRef.Get<AudioClip>();
        }

        public static AnimationClip GetAnimationClip(this ResourceRef resourceRef)
        {
            return resourceRef.Get<AnimationClip>();
        }

        public static RuntimeAnimatorController GetRuntimeAnimatorController(this ResourceRef resourceRef)
        {
            return resourceRef.Get<RuntimeAnimatorController>();
        }

        public static Sprite GetSprite(this ResourceRef resourceRef)
        {
            return resourceRef.Get<Sprite>();
        }
    }
}