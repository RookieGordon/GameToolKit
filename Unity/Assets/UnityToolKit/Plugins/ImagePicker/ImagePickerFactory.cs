/*
 * datetime     : 2026/2/24
 * description  : 图片选择器工厂
 *                根据当前运行平台自动创建对应的 IImagePicker 实现
 */

using ToolKit.Tools.ImagePicker;
using UnityEngine;

namespace UnityToolKit.Plugins.ImagePicker
{
    /// <summary>
    /// 图片选择器工厂
    /// <para>根据运行平台自动创建对应的图片选择器实现</para>
    /// </summary>
    public static class ImagePickerFactory
    {
        private static IImagePicker _instance;

        /// <summary>
        /// 获取当前平台对应的图片选择器 (单例)
        /// </summary>
        /// <returns>图片选择器实例, 不支持的平台返回 null</returns>
        public static IImagePicker Get()
        {
            if (_instance != null) return _instance;
            _instance = Create();
            return _instance;
        }

        /// <summary>
        /// 创建当前平台对应的图片选择器
        /// </summary>
        /// <returns>图片选择器实例, 不支持的平台返回 null</returns>
        public static IImagePicker Create()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var go = new GameObject("[ImagePicker]");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var picker = go.AddComponent<AndroidImagePicker>();
            picker.Init();
            return picker;
#elif UNITY_IOS && !UNITY_EDITOR
            var go = new GameObject("[ImagePicker]");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var picker = go.AddComponent<IOSImagePicker>();
            picker.Init();
            return picker;
#elif UNITY_EDITOR
            var go = new GameObject("[ImagePicker]");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var picker = go.AddComponent<EditorImagePicker>();
            return picker;
#else
            return null;
#endif
        }
    }
}
