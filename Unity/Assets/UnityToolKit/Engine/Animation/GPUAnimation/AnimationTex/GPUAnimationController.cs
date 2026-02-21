/*
 * author       : Gordon
 * datetime     : 2025/3/26
 * description  : GPU动画控制器，代替AnimatonController
 */

using System;
using UnityEngine;
using UnityToolKit.Engine;

namespace UnityToolKit.Engine.Animation
{
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    public class GPUAnimationController : MonoBehaviour
    {
        public GPUAnimationData GPUAnimData;
        public MeshFilter MeshFilter;
        public MeshRenderer MeshRenderer;
        public Action<string> OnAnimEvent;
        public AnimationTicker AnimTicker { get; private set; } = new AnimationTicker();


        protected void OnValidate() => Init();

        public void Init()
        {
            MeshFilter = GetComponent<MeshFilter>();
            MeshRenderer = GetComponent<MeshRenderer>();
            if (GPUAnimData == null || MeshRenderer.sharedMaterial == null)
            {
                return;
            }

            AnimTicker.Setup(GPUAnimData.AnimationClips);
            MeshFilter.sharedMesh = GPUAnimData.BakedMesh;
            GPUAnimData.ApplyMaterial(MeshRenderer.sharedMaterial);
            _InitExposeBones();
        }

        [InspectorButton]
        public GPUAnimationController SetAnimation(int _animIndex)
        {
            AnimTicker.SetAnimation(_animIndex);
            return this;
        }

        public void Tick(float deltaTime)
        {
            if (!AnimTicker.Tick(Time.deltaTime, out var output, OnAnimEvent))
            {
                return;
            }

            var block = new MaterialPropertyBlock();
            output.ApplyPropertyBlock(block);
            MeshRenderer.SetPropertyBlock(block);
            _TickExposeBones(output);
        }

        public void SetTime(float time) => AnimTicker.SetTime(time);
        public void SetTimeScale(float scale) => AnimTicker.SetNormalizedTime(scale);
        public float GetScale() => AnimTicker.GetNormalizedTime();

        #region ExposeBones

        private Transform _exposeBoneParent;
        private Transform[] _exposeBones;

        private void _InitExposeBones()
        {
            var exposeBoners = GPUAnimData.ExposeTransforms;
            if (exposeBoners == null || exposeBoners.Length <= 0)
                return;

            _exposeBoneParent = new GameObject("Bones") { hideFlags = HideFlags.DontSave }.transform;
            _exposeBoneParent.SetParent(transform);
            _exposeBoneParent.localPosition = Vector3.zero;
            _exposeBoneParent.localRotation = Quaternion.identity;
            _exposeBoneParent.localScale = Vector3.one;
            _exposeBones = new Transform[GPUAnimData.ExposeTransforms.Length];
            for (int i = 0; i < GPUAnimData.ExposeTransforms.Length; i++)
            {
                _exposeBones[i] = new GameObject(GPUAnimData.ExposeTransforms[i].Name) { hideFlags = HideFlags.DontSave }.transform;
                _exposeBones[i].SetParent(_exposeBoneParent);
            }
        }

        private void _TickExposeBones(AnimationTickOutput output)
        {
            if (GPUAnimData.ExposeTransforms == null || GPUAnimData.ExposeTransforms.Length <= 0)
                return;

            for (int i = 0; i < GPUAnimData.ExposeTransforms.Length; i++)
            {
                int boneIndex = GPUAnimData.ExposeTransforms[i].Index;
                Matrix4x4 recordMatrix = new Matrix4x4();
                recordMatrix.SetRow(0, Vector4.Lerp(
                    _ReadAnimationTexture(boneIndex, 0, output.Cur),
                    _ReadAnimationTexture(boneIndex, 0, output.Next),
                    output.Interpolate));
                recordMatrix.SetRow(1, Vector4.Lerp(
                    _ReadAnimationTexture(boneIndex, 1, output.Cur),
                    _ReadAnimationTexture(boneIndex, 1, output.Next),
                    output.Interpolate));
                recordMatrix.SetRow(2, Vector4.Lerp(
                    _ReadAnimationTexture(boneIndex, 2, output.Cur),
                    _ReadAnimationTexture(boneIndex, 2, output.Next),
                    output.Interpolate));
                recordMatrix.SetRow(3, new Vector4(0, 0, 0, 1));

                _exposeBones[i].transform.localPosition =
                    recordMatrix.MultiplyPoint(GPUAnimData.ExposeTransforms[i].Position);
                _exposeBones[i].transform.localRotation =
                    Quaternion.LookRotation(recordMatrix.MultiplyVector(GPUAnimData.ExposeTransforms[i].Direction));
            }
        }

        private Vector4 _ReadAnimationTexture(int boneIndex, int row, int frame)
        {
            var pixel = GPUAnimUtil.GetTransformPixel(boneIndex, row, frame);
            return GPUAnimData.BakeTexture.GetPixel(pixel.x, pixel.y);
        }

        #endregion
    }
}