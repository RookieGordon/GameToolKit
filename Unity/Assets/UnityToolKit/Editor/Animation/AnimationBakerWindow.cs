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

namespace UnityToolKit.Editor.Animation
{
    public partial class AnimationBakerWindow : EditorWindow
    {
        [MenuItem("Tools/CustomWidow/Optimize/GPU Animation Baker", false, 10)]
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
        private EGPUAnimationMode _animBakedMode = EGPUAnimationMode._ANIM_BONE;

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

            if (_animBakedMode == EGPUAnimationMode._ANIM_VERTEX)
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

        private static Texture2D CreateTexture(int width, int height)
        {
            return new Texture2D(width, height,
                TextureFormat.RGBAHalf, false)
            {
                filterMode = FilterMode.Point,
                wrapModeU = TextureWrapMode.Clamp,
                wrapModeV = TextureWrapMode.Repeat
            };
        }

        private static int GetClipParams(AnimationClip[] clips, out AnimationTickerClip[] clipParams)
        {
            int totalHeight = 0;
            clipParams = new AnimationTickerClip[clips.Length];
            for (int i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];

                var instanceEvents = new AnimationTickEvent[clip.events.Length];
                for (int j = 0; j < clip.events.Length; j++)
                {
                    instanceEvents[j] = new AnimationTickEvent(clip.events[j], clip.frameRate);
                }

                clipParams[i] = new AnimationTickerClip(clip.name, totalHeight, clip.frameRate, clip.length,
                    clip.isLooping, instanceEvents);
                var frameCount = (int)(clip.length * clip.frameRate);
                totalHeight += frameCount;
            }

            return totalHeight;
        }
    }
}