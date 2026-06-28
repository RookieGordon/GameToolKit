/*
 ****************************************************
 * 作者：Gordon
 * 创建时间：2026/02/21
 * 功能描述：GPU蒙皮网格渲染器
 *           使用ComputeShader在GPU端实时计算蒙皮变换，
 *           替代Unity内置的SkinnedMeshRenderer，
 *           降低CPU端蒙皮计算的开销。
 *           
 *           适用场景：需要保留完整骨骼动画系统（Animator/Animation），
 *           但希望将蒙皮计算从CPU移到GPU的情况。
 *           与AnimationTexture方案互补：
 *           - AnimationTexture: 完全脱离Animator，适合大量同模型实例
 *           - GPUSkinnedMesh:   保留Animator控制，适合需要动画混合/IK的角色
 ****************************************************
 */

using System.Collections.Generic;
using UnityEngine;

namespace UnityToolKit.Engine.Animation
{
    /// <summary>
    /// GPU蒙皮网格渲染器，替代SkinnedMeshRenderer进行GPU端蒙皮计算。
    /// 将骨骼矩阵通过ComputeBuffer传递给ComputeShader，
    /// 在GPU端完成顶点蒙皮变换，结果写入输出Mesh的顶点缓冲区。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class GPUSkinnedMeshRenderer : MonoBehaviour
    {
        [Header("数据源")]
        [Tooltip("原始的SkinnedMeshRenderer，用于获取骨骼和绑定姿势数据")]
        public SkinnedMeshRenderer SourceSkin;

        [Header("计算着色器")]
        [Tooltip("用于GPU蒙皮计算的ComputeShader")]
        public ComputeShader SkinningShader;

        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;

        // GPU 缓冲区
        private ComputeBuffer _vertexBuffer;        // 输入：原始顶点数据
        private ComputeBuffer _boneWeightBuffer;     // 输入：骨骼权重
        private ComputeBuffer _boneMatrixBuffer;     // 输入：当前帧骨骼矩阵
        private ComputeBuffer _outputVertexBuffer;   // 输出：蒙皮后顶点位置
        private ComputeBuffer _outputNormalBuffer;   // 输出：蒙皮后法线

        private int _kernelIndex;
        private int _vertexCount;
        private int _threadGroupCount;
        private Matrix4x4[] _boneMatrices;
        private Mesh _outputMesh;

        // 缓存的绑定姿势逆矩阵
        private Matrix4x4[] _bindPoses;
        private Transform[] _bones;

        private bool _initialized;

        private struct VertexData
        {
            public Vector3 Position;
            public Vector3 Normal;
        }

        private struct BoneWeightData
        {
            public Vector4 Indices;  // 4个骨骼索引
            public Vector4 Weights;  // 4个骨骼权重
        }

        private void Awake()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
        }

        private void OnEnable()
        {
            Initialize();
        }

        private void OnDisable()
        {
            ReleaseBuffers();
        }

        private void OnDestroy()
        {
            ReleaseBuffers();
            if (_outputMesh != null)
            {
                Destroy(_outputMesh);
            }
        }

        /// <summary>
        /// 初始化GPU蒙皮所需的缓冲区和数据
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            if (SourceSkin == null || SkinningShader == null) return;

            var sharedMesh = SourceSkin.sharedMesh;
            if (sharedMesh == null) return;

            _vertexCount = sharedMesh.vertexCount;
            _bindPoses = sharedMesh.bindposes;
            _bones = SourceSkin.bones;
            _boneMatrices = new Matrix4x4[_bones.Length];

            // 查找 ComputeShader 内核
            _kernelIndex = SkinningShader.FindKernel("CSSkinning");

            // 计算线程组数量（每组64线程）
            _threadGroupCount = Mathf.CeilToInt(_vertexCount / 64f);

            // 准备顶点数据
            var vertices = sharedMesh.vertices;
            var normals = sharedMesh.normals;
            var vertexData = new VertexData[_vertexCount];
            for (int i = 0; i < _vertexCount; i++)
            {
                vertexData[i] = new VertexData
                {
                    Position = vertices[i],
                    Normal = normals != null && i < normals.Length ? normals[i] : Vector3.up
                };
            }

            // 准备骨骼权重数据
            var boneWeights = sharedMesh.boneWeights;
            var boneWeightData = new BoneWeightData[_vertexCount];
            for (int i = 0; i < _vertexCount; i++)
            {
                if (i < boneWeights.Length)
                {
                    boneWeightData[i] = new BoneWeightData
                    {
                        Indices = new Vector4(
                            boneWeights[i].boneIndex0, boneWeights[i].boneIndex1,
                            boneWeights[i].boneIndex2, boneWeights[i].boneIndex3),
                        Weights = new Vector4(
                            boneWeights[i].weight0, boneWeights[i].weight1,
                            boneWeights[i].weight2, boneWeights[i].weight3)
                    };
                }
            }

            // 创建 GPU 缓冲区
            int vertexStride = sizeof(float) * 6; // Position(3) + Normal(3)
            int boneWeightStride = sizeof(float) * 8; // Indices(4) + Weights(4)

            _vertexBuffer = new ComputeBuffer(_vertexCount, vertexStride);
            _vertexBuffer.SetData(vertexData);

            _boneWeightBuffer = new ComputeBuffer(_vertexCount, boneWeightStride);
            _boneWeightBuffer.SetData(boneWeightData);

            _boneMatrixBuffer = new ComputeBuffer(_bones.Length, sizeof(float) * 16);

            _outputVertexBuffer = new ComputeBuffer(_vertexCount, sizeof(float) * 3);
            _outputNormalBuffer = new ComputeBuffer(_vertexCount, sizeof(float) * 3);

            // 绑定缓冲区到 ComputeShader
            SkinningShader.SetBuffer(_kernelIndex, "_VertexBuffer", _vertexBuffer);
            SkinningShader.SetBuffer(_kernelIndex, "_BoneWeightBuffer", _boneWeightBuffer);
            SkinningShader.SetBuffer(_kernelIndex, "_BoneMatrixBuffer", _boneMatrixBuffer);
            SkinningShader.SetBuffer(_kernelIndex, "_OutputVertexBuffer", _outputVertexBuffer);
            SkinningShader.SetBuffer(_kernelIndex, "_OutputNormalBuffer", _outputNormalBuffer);
            SkinningShader.SetInt("_VertexCount", _vertexCount);

            // 创建输出Mesh
            _outputMesh = Instantiate(sharedMesh);
            _outputMesh.name = sharedMesh.name + "_GPUSkinned";
            // 移除不需要的蒙皮数据
            _outputMesh.boneWeights = null;
            _outputMesh.bindposes = null;
            _meshFilter.sharedMesh = _outputMesh;

            _initialized = true;
        }

        private void LateUpdate()
        {
            if (!_initialized) return;

            UpdateBoneMatrices();
            DispatchSkinning();
            ReadbackResults();
        }

        /// <summary>
        /// 更新骨骼矩阵 = 当前骨骼世界矩阵 × 绑定姿势逆矩阵 × 根节点世界到本地矩阵
        /// </summary>
        private void UpdateBoneMatrices()
        {
            var rootWorldToLocal = transform.worldToLocalMatrix;
            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] != null)
                {
                    _boneMatrices[i] = rootWorldToLocal * _bones[i].localToWorldMatrix * _bindPoses[i];
                }
            }

            _boneMatrixBuffer.SetData(_boneMatrices);
        }

        /// <summary>
        /// 分发 ComputeShader 执行蒙皮计算
        /// </summary>
        private void DispatchSkinning()
        {
            SkinningShader.Dispatch(_kernelIndex, _threadGroupCount, 1, 1);
        }

        /// <summary>
        /// 将GPU计算结果回读到Mesh
        /// </summary>
        private void ReadbackResults()
        {
            var positions = new Vector3[_vertexCount];
            var normals = new Vector3[_vertexCount];

            _outputVertexBuffer.GetData(positions);
            _outputNormalBuffer.GetData(normals);

            _outputMesh.vertices = positions;
            _outputMesh.normals = normals;
            _outputMesh.RecalculateBounds();
        }

        private void ReleaseBuffers()
        {
            _vertexBuffer?.Release();
            _boneWeightBuffer?.Release();
            _boneMatrixBuffer?.Release();
            _outputVertexBuffer?.Release();
            _outputNormalBuffer?.Release();

            _vertexBuffer = null;
            _boneWeightBuffer = null;
            _boneMatrixBuffer = null;
            _outputVertexBuffer = null;
            _outputNormalBuffer = null;

            _initialized = false;
        }
    }
}
