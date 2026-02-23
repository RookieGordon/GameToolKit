/*
 * author       : Gordon
 * datetime     : 2025/3/26
 * description  : 动画烘焙界面
 *                选择要进行烘焙的模型和对应的动画片段进行烘焙
 */

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityToolKit.Editor.Common;
using UnityToolKit.Engine.Animation;

namespace UnityToolKit.Editor.GPUAnimation
{
    public partial class AnimationBakerWindow : EditorWindow
    {
        [MenuItem("Tools/CustomWidow/动画/GPU动画烘焙工具", false, 10)]
        public static void ShowOptimizeWindow()
        {
            var window = GetWindow<AnimationBakerWindow>();
            window.titleContent = new GUIContent("GPU动画烘焙", EditorGUIUtility.IconContent("AvatarSelector").image);
            window.UpdateWithSelections();
        }

        private GameObject _targetPrefab;
        private SerializedObject _serializedWindow;
        [SerializeField] private AnimationClip[] _targetAnimations;
        private SerializedProperty _animationProperty;
        private string _boneExposeRegex = "";
        private EGPUAnimationMode _animBakedMode = EGPUAnimationMode.ANIM_BONE;
        private int _maxAtlasSize = 4096;

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptReload()
        {
        }

        private void OnEnable()
        {
            _targetAnimations = null;
            _serializedWindow = new SerializedObject(this);
            _animationProperty = _serializedWindow.FindProperty("_targetAnimations");
            EditorApplication.update += Tick;
        }

        private void OnDisable()
        {
            _targetPrefab = null;
            _targetAnimations = null;
            _serializedWindow.Dispose();
            _animationProperty.Dispose();
            EditorApplication.update -= Tick;
        }

        private void OnGUI()
        {
            using (new EditorGUILayoutVertical())
            {
                UpdateWithSelections();
                DrawGUI();
            }
        }

        private void UpdateWithSelections()
        {
            var clip = new List<AnimationClip>();
            foreach (var obj in Selection.objects)
            {
                if ((obj as AnimationClip) != null && AssetDatabase.IsMainAsset(obj))
                {
                    clip.Add(obj as AnimationClip);
                }
                else if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(obj)) as ModelImporter != null)
                {
                    _targetPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(obj));
                }
            }

            if (clip.Count <= 0)
            {
                return;
            }

            _targetAnimations = clip.ToArray();
            _serializedWindow.Update();
        }

        private void DrawGUI()
        {
            using (new EditorGUILayoutHorizontal())
            {
                EditorGUILayout.LabelField("选择Fbx和动画片段数据");
                _targetPrefab = EditorGUILayout.ObjectField(_targetPrefab, typeof(GameObject), false)
                    as GameObject;
            }

            EditorGUILayout.PropertyField(_animationProperty, true);
            _serializedWindow.ApplyModifiedProperties();

            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(_targetPrefab)) as ModelImporter;
            if (_targetPrefab == null || importer == null
                                      || _targetAnimations == null || _targetAnimations.Length <= 0)
            {
                EditorGUILayout.LabelField("<color=#ff0000>请先选择一个Fbx模型和对应的动画片段</color>", WindowLabelStyle.ErrorLabel);
                return;
            }

            if (_targetAnimations == null || _targetAnimations.Length <= 0
                                          || _targetAnimations.Any(p => p == null))
            {
                return;
            }

            using (new EditorGUILayoutHorizontal())
            {
                _animBakedMode = (EGPUAnimationMode)EditorGUILayout.EnumPopup("烘焙类型: ", _animBakedMode);
            }

            using (new EditorGUILayoutHorizontal())
            {
                _maxAtlasSize = EditorGUILayout.IntPopup("Atlas 最大尺寸: ", _maxAtlasSize,
                    new[] { "1024", "2048", "4096", "8192" },
                    new[] { 1024, 2048, 4096, 8192 });
            }

            if (_animBakedMode == EGPUAnimationMode.ANIM_VERTEX)
            {
                if (GUILayout.Button("烘焙"))
                {
                    BakeVertexAnimation(_targetPrefab, _targetAnimations);
                }
            }
            else
            {
                using (new EditorGUILayoutHorizontal())
                {
                    EditorGUILayout.LabelField("Expose Transform Regex:");
                    _boneExposeRegex = EditorGUILayout.TextArea(_boneExposeRegex);
                }

                if (GUILayout.Button("烘焙"))
                {
                    if (importer.optimizeGameObjects)
                    {
                        EditorUtility.DisplayDialog("错误", "Fbx开启了骨骼节点优化（Optimize GameObjects），不能进行烘焙！", "确认");
                        return;
                    }

                    BakeBoneAnimation(_targetPrefab, _targetAnimations, _boneExposeRegex);
                }
            }
        }

        private void Tick()
        {
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// 创建动画纹理。
        /// 使用 NPOT（非2的幂次）尺寸以避免 NextPowerOfTwo 导致的大量空间浪费。
        /// wrapMode 统一设为 Clamp，因为：
        ///   1. U 轴：超出数据范围无意义，必须 Clamp。
        ///   2. V 轴：循环动画的帧回绕由 CPU 端 AnimationTicker 处理（帧索引取模），
        ///      传入 Shader 的帧索引始终在纹理有效范围内，不依赖 GPU 的 Repeat 采样。
        ///      使用 Clamp 可兼容 OpenGL ES 2.0 等旧 GPU 对 NPOT 纹理的限制
        ///      （旧 GPU 不支持 NPOT + Repeat 组合，会导致纹理变黑或未定义行为）。
        /// FilterMode = Point：逐像素精确采样，避免相邻骨骼/顶点数据被双线性插值混合。
        /// </summary>
        private static Texture2D CreateTexture(int width, int height)
        {
            return new Texture2D(width, height, TextureFormat.RGBAHalf, false)
            {
                filterMode = FilterMode.Point,
                wrapModeU = TextureWrapMode.Clamp,
                wrapModeV = TextureWrapMode.Clamp
            };
        }

        /// <summary>
        /// 采集所有动画片段元数据，按 Atlas 最大高度自动分组。
        /// 每个分组对应一张 Atlas 纹理，单个动画片段绝不会横跨两张纹理。
        /// </summary>
        /// <param name="clips">所有动画片段</param>
        /// <param name="maxAtlasHeight">单张 Atlas 的最大高度（像素）</param>
        /// <param name="clipParams">输出：每个片段的帧参数（含 TextureIndex 和局部 FrameBegin）</param>
        /// <param name="atlasHeights">输出：每张 Atlas 的高度</param>
        private static void GetClipParams(AnimationClip[] clips, int maxAtlasHeight,
            out AnimationTickerClip[] clipParams, out int[] atlasHeights)
        {
            clipParams = new AnimationTickerClip[clips.Length];
            var heights = new List<int>();
            int currentGroupHeight = 0;
            int currentGroupIndex = 0;

            for (int i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];
                int frameCount = Mathf.Max(1, (int)(clip.length * clip.frameRate));

                // 超出当前 Atlas 高度限制时，换新 Atlas（当前组至少需有 1 个片段）
                if (currentGroupHeight > 0 && currentGroupHeight + frameCount > maxAtlasHeight)
                {
                    heights.Add(currentGroupHeight);
                    currentGroupHeight = 0;
                    currentGroupIndex++;
                }

                var events = new AnimationTickEvent[clip.events.Length];
                for (int j = 0; j < clip.events.Length; j++)
                    events[j] = new AnimationTickEvent(clip.events[j], clip.frameRate);

                clipParams[i] = new AnimationTickerClip(clip.name, currentGroupHeight, clip.frameRate,
                    clip.length, clip.isLooping, events, currentGroupIndex);
                currentGroupHeight += frameCount;
            }

            // 添加最后一组
            heights.Add(currentGroupHeight);
            atlasHeights = heights.ToArray();
        }
    }
}