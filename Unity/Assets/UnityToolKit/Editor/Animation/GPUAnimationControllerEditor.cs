/* 
****************************************************
* 作者：Gordon
* 创建时间：2025/06/28 18:35:26
* 功能描述：GPU动画Inspector窗口
****************************************************
*/

using System;
using UnityEditor;
using UnityEngine;
using UnityToolKit.Editor.Common;
using UnityToolKit.Engine.Animation;
using UnityToolKit.Runtime.Utility;

namespace UnityToolKit.Editor.Animation
{
    [CustomEditor(typeof(GPUAnimationController))]
    public class GPUAnimationControllerEditor : CustomInspector
    {
        private PreviewRenderUtility _preview;
        private GPUAnimationController _previewTarget;
        private MeshRenderer _previewMeshRenderer;
        private GameObject _boundsViewer;
        private MaterialPropertyBlock _targetBlock;
        private float _previewTickSpeed = 1f;
        private int _previewAnimIndex = 0;
        private bool _previewReplay = true;
        private Vector2 _rotateDelta;
        private Vector3 _cameraDirection;
        private float _cameraDistance = 8f;

        public override bool HasPreviewGUI()
        {
            if (!(target as GPUAnimationController).GPUAnimData)
            {
                return false;
            }

            return true;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (!HasPreviewGUI())
            {
                return;
            }

            _cameraDirection = Vector3.Normalize(new Vector3(0f, 3f, 15f));
            _preview = new PreviewRenderUtility();
            _preview.camera.fieldOfView = 30.0f;
            _preview.camera.nearClipPlane = 0.3f;
            _preview.camera.farClipPlane = 1000;
            _preview.camera.transform.position = _cameraDirection * _cameraDistance;

            _previewTarget = GameObject.Instantiate(((Component)target).gameObject)
                .GetComponent<GPUAnimationController>();
            _preview.AddSingleGO(_previewTarget.gameObject);
            _previewMeshRenderer = _previewTarget.GetComponent<MeshRenderer>();
            _targetBlock = new MaterialPropertyBlock();
            _previewTarget.transform.position = Vector3.zero;
            _previewTarget.Init();
            if (_previewTarget.GPUAnimData != null && _previewTarget.GPUAnimData.AnimationClips != null
                && _previewTarget.GPUAnimData.AnimationClips.Length > 0)
                _previewTarget.SetAnimation(0);

            Material transparentMaterial = new Material(Shader.Find("Unlit/Transparent"));
            transparentMaterial.SetColor("_Color", new Color(1, 1, 1, .3f));

            _boundsViewer = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _boundsViewer.GetComponent<MeshRenderer>().material = transparentMaterial;

            _preview.AddSingleGO(_boundsViewer.gameObject);
            _boundsViewer.transform.SetParent(_previewTarget.transform);
            _boundsViewer.transform.localRotation = Quaternion.identity;
            var previewMesh = _previewTarget.MeshFilter.sharedMesh;
            if (previewMesh != null)
            {
                _boundsViewer.transform.localScale = previewMesh.bounds.size;
                _boundsViewer.transform.localPosition = previewMesh.bounds.center;
            }
            _boundsViewer.SetActive(false);

            EditorApplication.update += Update;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Update;
            if (_preview == null)
                return;

            _preview.Cleanup();
            _preview = null;
            _previewTarget = null;
            _previewMeshRenderer = null;
            _targetBlock = null;
        }

        public override void OnPreviewSettings()
        {
            base.OnPreviewSettings();
            if (_previewTarget == null || _previewTarget.AnimTicker == null
                || _previewTarget.GPUAnimData == null || _previewTarget.GPUAnimData.AnimationClips == null
                || _previewTarget.GPUAnimData.AnimationClips.Length <= 0)
                return;
            AnimationTickerClip param = _previewTarget.AnimTicker.Anim;
            GUILayout.Label(string.Format("{0},Loop:{1}", param.Name, param.Loop ? 1 : 0));
        }

        public override void OnPreviewGUI(Rect r, GUIStyle background)
        {
            if (_preview == null)
                return;
            InputCheck();
            PreviewGUI();
            
            // 限制预览渲染尺寸，避免 RenderTexture.Create 失败
            float maxSize = Mathf.Min(SystemInfo.maxTextureSize, 2048);
            Rect clampedRect = r;
            clampedRect.width = Mathf.Min(r.width, maxSize);
            clampedRect.height = Mathf.Min(r.height, maxSize);
            if (clampedRect.width <= 0 || clampedRect.height <= 0)
                return;
            _preview.BeginPreview(clampedRect, background);
            _preview.camera.Render();
            _preview.EndAndDrawPreview(clampedRect);
        }

        void InputCheck()
        {
            if (Event.current == null)
                return;
            if (Event.current.type == EventType.MouseDrag)
                _rotateDelta += Event.current.delta;

            if (Event.current.type == EventType.ScrollWheel)
                _cameraDistance = Mathf.Clamp(_cameraDistance + Event.current.delta.y * .2f, 0, 20f);
        }

        void PreviewGUI()
        {
            var gpuAnimData = _previewTarget.GPUAnimData;
            string[] anims = new string[gpuAnimData.AnimationClips.Length];
            for (int i = 0; i < anims.Length; i++)
                anims[i] = gpuAnimData.AnimationClips[i].Name
                    .Substring(gpuAnimData.AnimationClips[i].Name.LastIndexOf("_") + 1);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Play:");
            _previewAnimIndex = GUILayout.SelectionGrid(_previewAnimIndex, anims,
                gpuAnimData.AnimationClips.Length > 5 ? 5 : gpuAnimData.AnimationClips.Length);
            if (_previewTarget.AnimTicker.AnimIndex != _previewAnimIndex)
                _previewTarget.SetAnimation(_previewAnimIndex);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Speed:");
            _previewTickSpeed = GUILayout.HorizontalSlider(_previewTickSpeed, 0f, 3f);
            GUILayout.Label("Replay:");
            _previewReplay = GUILayout.Toggle(_previewReplay, "");
            GUILayout.Label("Bounds:");
            _boundsViewer.SetActive(GUILayout.Toggle(_boundsViewer.activeSelf, ""));
            GUILayout.EndHorizontal();
        }

        void Update()
        {
            if (_preview == null || _previewTarget == null || target == null)
                return;

            if (_previewReplay && _previewTarget.GetScale() >= 1)
                _previewTarget.SetTime(0f);

            var controller = target as GPUAnimationController;
            if (controller != null)
                controller.Tick(UnityTime.DeltaTime * _previewTickSpeed);
            _previewTarget.Tick(UnityTime.DeltaTime * _previewTickSpeed);

            _preview.camera.transform.position = _cameraDirection * _cameraDistance;
            _preview.camera.transform.LookAt(_previewTarget.transform);
            _previewTarget.transform.rotation = Quaternion.Euler(_rotateDelta.y, _rotateDelta.x, 0f);
            Repaint();
        }
    }
}