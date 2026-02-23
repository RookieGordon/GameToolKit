/*
 * author       : Gordon
 * datetime     : 2025/3/26
 * description  : GPU动画控制器，代替Animator
 */

using System;
using UnityEngine;
using UnityToolKit.Engine.Render;

namespace UnityToolKit.Engine.Animation
{
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    public class GPUAnimationController : MonoBehaviour
    {
        /// <summary>
        /// GPU 动画数据。包含一个 Mesh、一组动画片段和一到多张 Atlas 纹理。
        /// 烘焙器会根据 Atlas 尺寸上限自动将动画分布到多张纹理，
        /// 运行时根据当前播放的动画片段自动切换对应的纹理。
        /// </summary>
        public GPUAnimationData GPUAnimData;
        public MeshFilter MeshFilter;
        public MeshRenderer MeshRenderer;
        public Action<string> OnAnimEvent;
        public AnimationTicker AnimTicker { get; private set; } = new AnimationTicker();

        /// <summary>
        /// 动画总数
        /// </summary>
        public int AnimationCount => GPUAnimData?.AnimationClips?.Length ?? 0;

        /*
         * MaterialPropertyBlock 是 Unity 提供的逐 Renderer 轻量级参数覆盖机制。它的关键优势在于：
         *  1.直接修改 renderer.material.SetXxx() 会导致 Unity 内部克隆一份 Material 实例，每个角色持有不同的 Material，GPU Instancing 合批直接失效。
         *  2.而 MaterialPropertyBlock 不会创建 Material 副本，所有实例仍共享同一份 Material，只是通过 Instancing Constant Buffer 逐实例传入不同的帧参数（_AnimFrameBegin、_AnimFrameEnd、_AnimFrameInterpolate），因此数百个角色仍可在同一个 Draw Call 中合批渲染。
         * 简而言之：它是 GPU Instancing 合批的前提条件。
         */
        private MaterialPropertyBlock _propertyBlock;

        private void Awake() => Init();

        protected void OnValidate() => Init();

        public void Init()
        {
            MeshFilter = GetComponent<MeshFilter>();
            MeshRenderer = GetComponent<MeshRenderer>();
            if (GPUAnimData == null || GPUAnimData.AnimationClips == null
                || GPUAnimData.AnimationClips.Length == 0)
                return;

            MeshFilter.sharedMesh = GPUAnimData.BakedMesh;

            if (MeshRenderer.sharedMaterial != null)
            {
                MeshRenderer.sharedMaterial.EnableKeywords(GPUAnimData.BakedMode);
                // 设置行折叠参数（骨骼模式 = 1，顶点模式 = ceil(vertexCount*2 / foldedWidth)）
                GPUAnimUtil.SetRowsPerFrame(MeshRenderer.sharedMaterial, GPUAnimData.RowsPerFrame);
            }

            AnimTicker.Setup(GPUAnimData.AnimationClips);
            InitExposeBones();
        }

        [InspectorButton]
        public GPUAnimationController SetAnimation(int animIndex)
        {
            if (GPUAnimData == null || GPUAnimData.AnimationClips == null
                || animIndex < 0 || animIndex >= GPUAnimData.AnimationClips.Length)
            {
                Debug.LogError($"Invalid Animation Index: {animIndex}");
                return this;
            }
            AnimTicker.SetAnimation(animIndex);
            return this;
        }

        public GPUAnimationController SetAnimation(string animName)
        {
            if (GPUAnimData?.AnimationClips == null) return this;
            for (int i = 0; i < GPUAnimData.AnimationClips.Length; i++)
            {
                if (GPUAnimData.AnimationClips[i].Name == animName)
                    return SetAnimation(i);
            }
            Debug.LogWarning($"Animation not found: {animName}");
            return this;
        }

        public string GetAnimationName(int animIndex)
        {
            if (GPUAnimData?.AnimationClips == null || animIndex < 0
                || animIndex >= GPUAnimData.AnimationClips.Length)
                return null;
            return GPUAnimData.AnimationClips[animIndex].Name;
        }

        /// <summary>
        /// 获取当前播放动画片段所在的 Atlas 纹理
        /// </summary>
        private Texture2D GetActiveTexture()
        {
            if (GPUAnimData?.BakeTextures == null || GPUAnimData.BakeTextures.Length == 0) return null;
            int texIdx = AnimTicker.Anim.TextureIndex;
            if (texIdx < 0 || texIdx >= GPUAnimData.BakeTextures.Length) return null;
            return GPUAnimData.BakeTextures[texIdx];
        }

        private void Update() => Tick(Time.deltaTime);

        public void Tick(float deltaTime)
        {
            if (!AnimTicker.Tick(deltaTime, out var output, OnAnimEvent))
                return;

            _propertyBlock ??= new MaterialPropertyBlock();
            output.ApplyPropertyBlock(_propertyBlock);
            // 逐实例设置当前动画片段所在的 Atlas 纹理
            var texture = GetActiveTexture();
            if (texture != null)
                GPUAnimUtil.SetAnimTexture(_propertyBlock, texture);
            MeshRenderer.SetPropertyBlock(_propertyBlock);
            TickExposeBones(output);
        }

        public void SetTime(float time) => AnimTicker.SetTime(time);
        public void SetTimeScale(float scale) => AnimTicker.SetNormalizedTime(scale);
        public float GetScale() => AnimTicker.GetNormalizedTime();

        #region ExposeBones

        private Transform _exposeBoneParent;
        private Transform[] _exposeBones;
        private GPUAnimationExposeBone[] _exposeTransformInfo;

        private void InitExposeBones()
        {
            _exposeTransformInfo = GPUAnimData?.ExposeTransforms;
            if (_exposeTransformInfo == null || _exposeTransformInfo.Length <= 0)
                return;

            _exposeBoneParent = new GameObject("Bones") { hideFlags = HideFlags.DontSave }.transform;
            _exposeBoneParent.SetParent(transform);
            _exposeBoneParent.localPosition = Vector3.zero;
            _exposeBoneParent.localRotation = Quaternion.identity;
            _exposeBoneParent.localScale = Vector3.one;
            _exposeBones = new Transform[_exposeTransformInfo.Length];
            for (int i = 0; i < _exposeTransformInfo.Length; i++)
            {
                _exposeBones[i] =
                    new GameObject(_exposeTransformInfo[i].Name) { hideFlags = HideFlags.DontSave }.transform;
                _exposeBones[i].SetParent(_exposeBoneParent);
            }
        }

        /// <summary>
        /// 从当前激活纹理中采样骨骼数据，还原暴露骨骼的 Transform。
        /// 使用四元数 + 平移的压缩格式：
        ///   像素0 = 四元数 (x,y,z,w)，像素1 = 平移 (x,y,z)
        /// 帧间插值使用 Quaternion.Lerp + Vector3.Lerp，比矩阵分量 lerp 更准确。
        /// </summary>
        private void TickExposeBones(AnimationTickOutput output)
        {
            if (_exposeTransformInfo == null || _exposeBones == null)
                return;

            var texture = GetActiveTexture();
            if (texture == null) return;

            for (int i = 0; i < _exposeTransformInfo.Length; i++)
            {
                int boneIndex = _exposeTransformInfo[i].Index;

                // 采样当前帧与下一帧的四元数
                var qCurPx = ReadPixel(texture, boneIndex, 0, output.Cur);
                var qNextPx = ReadPixel(texture, boneIndex, 0, output.Next);
                var qCur = new Quaternion(qCurPx.x, qCurPx.y, qCurPx.z, qCurPx.w);
                var qNext = new Quaternion(qNextPx.x, qNextPx.y, qNextPx.z, qNextPx.w);
                Quaternion q = Quaternion.Lerp(qCur, qNext, output.Interpolate);

                // 采样当前帧与下一帧的平移
                var tCurPx = ReadPixel(texture, boneIndex, 1, output.Cur);
                var tNextPx = ReadPixel(texture, boneIndex, 1, output.Next);
                Vector3 t = Vector3.Lerp(
                    new Vector3(tCurPx.x, tCurPx.y, tCurPx.z),
                    new Vector3(tNextPx.x, tNextPx.y, tNextPx.z),
                    output.Interpolate);

                // 从四元数 + 平移重建变换矩阵
                Matrix4x4 recordMatrix = Matrix4x4.TRS(t, q, Vector3.one);

                _exposeBones[i].transform.localPosition =
                    recordMatrix.MultiplyPoint(_exposeTransformInfo[i].Position);
                _exposeBones[i].transform.localRotation =
                    Quaternion.LookRotation(recordMatrix.MultiplyVector(_exposeTransformInfo[i].Direction));
            }
        }

        private static Vector4 ReadPixel(Texture2D texture, int boneIndex, int row, int frame)
        {
            var pixel = GPUAnimUtil.GetTransformPixel(boneIndex, row, frame);
            return texture.GetPixel(pixel.x, pixel.y);
        }

        #endregion
    }
}