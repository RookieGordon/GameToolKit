/*
 ****************************************************
 * 作者：Gordon
 * 创建时间：2026/02/22
 * 功能描述：GPU动画 Instancing 批量渲染示例
 *           使用 Graphics.DrawMeshInstanced 一次绘制大量动画角色实例
 *           每个实例独立播放动画，通过 MaterialPropertyBlock 传递帧参数
 *           借鉴自 Unity3D-ToolChain_StriteR
 ****************************************************
 */

using UnityEngine;
using UnityEngine.Rendering;
using UnityToolKit.Engine.Animation;

namespace Tests.GPUAnimationTest
{
    [ExecuteInEditMode]
    public class GPUAnimInstanceSample : MonoBehaviour
    {
        [Header("网格布局")]
        public int CountX = 32;
        public int CountZ = 32;
        [Tooltip("实例之间的间距")]
        public float Spacing = 10f;

        [Header("GPU动画数据")]
        public GPUAnimationData AnimData;
        public Material InstanceMaterial;

        private AnimationTicker[] _tickers;
        private Matrix4x4[] _matrices;
        private MaterialPropertyBlock _propertyBlock;
        private float[] _curFrames;
        private float[] _nextFrames;
        private float[] _interpolates;

        private void Awake()
        {
            if (AnimData == null || InstanceMaterial == null)
                return;

            int totalCount = CountX * CountZ;
            _matrices = new Matrix4x4[totalCount];
            _tickers = new AnimationTicker[totalCount];
            _curFrames = new float[totalCount];
            _nextFrames = new float[totalCount];
            _interpolates = new float[totalCount];
            _propertyBlock = new MaterialPropertyBlock();

            for (int i = 0; i < CountX; i++)
            {
                for (int j = 0; j < CountZ; j++)
                {
                    int index = i * CountZ + j;
                    float scale = 0.8f + Random.value * 0.2f;
                    _matrices[index] = Matrix4x4.TRS(
                        transform.position + new Vector3(i * Spacing, 0, j * Spacing),
                        transform.rotation,
                        Vector3.one * scale);

                    _tickers[index] = new AnimationTicker();
                    _tickers[index].Setup(AnimData.AnimationClips);
                    _tickers[index].SetAnimation(Random.Range(0, AnimData.AnimationClips.Length));
                    _tickers[index].SetNormalizedTime(Random.value);
                }
            }

            AnimData.ApplyMaterial(InstanceMaterial);
        }

        private void Update()
        {
            if (_tickers == null || AnimData == null || InstanceMaterial == null)
                return;

            float deltaTime = Time.deltaTime;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                deltaTime = UnityToolKit.Runtime.Utility.UnityTime.DeltaTime;
#endif

            for (int i = 0; i < CountX; i++)
            {
                for (int j = 0; j < CountZ; j++)
                {
                    int index = i * CountZ + j;
                    _tickers[index].Tick(deltaTime, out var output);

                    // 非循环动画播放结束后重置
                    if (!_tickers[index].Anim.Loop &&
                        _tickers[index].TimeElapsed >= _tickers[index].Anim.Length)
                    {
                        _tickers[index].SetNormalizedTime(0f);
                    }

                    _curFrames[index] = output.Cur;
                    _nextFrames[index] = output.Next;
                    _interpolates[index] = output.Interpolate;
                }
            }

            _propertyBlock.SetFloatArray("_AnimFrameBegin", _curFrames);
            _propertyBlock.SetFloatArray("_AnimFrameEnd", _nextFrames);
            _propertyBlock.SetFloatArray("_AnimFrameInterpolate", _interpolates);

            Graphics.DrawMeshInstanced(
                AnimData.BakedMesh, 0, InstanceMaterial,
                _matrices, _matrices.Length, _propertyBlock,
                ShadowCastingMode.On, true);
        }
    }
}
