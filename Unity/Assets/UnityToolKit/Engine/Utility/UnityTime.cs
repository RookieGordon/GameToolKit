/*
 * 功能描述：统一的时间工具，在编辑器模式下提供deltaTime支持
 *           借鉴自 Unity3D-ToolChain_StriteR (UTime)
 */

using UnityEngine;

namespace UnityToolKit.Runtime.Utility
{
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    public static class UnityTime
    {
#if UNITY_EDITOR
        static UnityTime() => UnityEditor.EditorApplication.update += EditorTick;

        public static float EditorDeltaTime { get; private set; } = 0f;
        public static float EditorTime { get; private set; } = 0f;

        static void EditorTick()
        {
            var last = EditorTime;
            EditorTime = (float)UnityEditor.EditorApplication.timeSinceStartup;
            EditorDeltaTime = Mathf.Max(0, EditorTime - last);
        }
#endif

        public static float DeltaTime
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    return EditorDeltaTime;
#endif
                return Time.deltaTime;
            }
        }

        public static float CurrentTime
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    return EditorTime;
#endif
                return Time.time;
            }
        }
    }
}
