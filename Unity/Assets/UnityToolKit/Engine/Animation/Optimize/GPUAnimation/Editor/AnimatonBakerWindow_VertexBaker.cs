/*
 ****************************************************
 * 作者：Gordon
 * 创建时间：2025/06/22 17:46:28
 * 功能描述：动画烘焙，烘焙顶点数据
 ****************************************************
 */

using UnityEditor;
using UnityEngine;
using UnityToolKit.Editor.Utility;
using UnityToolKit.Engine.Extension;
using UnityToolKit.Engine.Optimize;
using UnityToolKit.Runtime.Utility;

namespace UnityToolKit.Editor.Engine.Optimize
{
    public partial class AnimationBakerWindow
    {
        private void BakeVertexAnimation(GameObject fbxObj, AnimationClip[] clips)
        {
            if (!EditorAssetUtil.SelectDirectory(fbxObj, out string savePath, out string meshName))
            {
                Debug.LogWarning("未选择有效的保存目录");
                return;
            }

            var instancedObj = GameObject.Instantiate(fbxObj);
            var meshRenderer = instancedObj.GetComponentInChildren<SkinnedMeshRenderer>();
            var bakedTexture = CreateVertexTexture(meshRenderer, clips, out var clipParams);
            WriteVertexData(instancedObj, meshRenderer, clips, clipParams, bakedTexture, out var minBounds);
            var instancedMesh = BakeMesh(meshRenderer, minBounds);
            DestroyImmediate(instancedObj);
            GenerateAssets(instancedMesh, bakedTexture, clipParams, meshName, savePath);
        }

        /// <summary>
        /// 创建顶点数据纹理贴图
        /// </summary>
        private static Texture2D CreateVertexTexture(SkinnedMeshRenderer meshRenderer, AnimationClip[] clips,
            out AnimationTickerClip[] clipParams)
        {
            var vertexCount = meshRenderer.sharedMesh.vertexCount;
            var totalVertexRecord = vertexCount * 2;
            var totalFrame = GetClipParams(clips, out clipParams);
            // 这里纹理的宽度，为什么是顶点数乘以2呢？因为需要存储顶点位置和顶点法向量，一共六个值，因此最少需要两个像素才行
            // U方向就是宽度方向，记录的是顶点序号，因此wrapMode需要设为Clamp（没有多余的数据可以读取）。而V方向是帧率方向，Repeat模式可以重复读取。
            return CreateTexture(Mathf.NextPowerOfTwo(totalVertexRecord), Mathf.NextPowerOfTwo(totalFrame));
        }

        /// <summary>
        /// 将动画顶点数据写入贴图
        /// </summary>
        private static void WriteVertexData(GameObject fbxObj, SkinnedMeshRenderer meshRenderer, AnimationClip[] clips,
            AnimationTickerClip[] clipParams, Texture2D bakedTexture, out Bounds bounds)
        {
            BoundsIncrement.Begin();
            for (int i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];
                var vertexBakedMesh = new Mesh();
                var length = clip.length;
                var frameRate = clip.frameRate;
                var frameCount = (int)(length * frameRate);
                var startFrame = clipParams[i].FrameBegin;
                for (int j = 0; j < frameCount; j++)
                {
                    clip.SampleAnimation(fbxObj, length * j / frameCount);
                    meshRenderer.BakeMesh(vertexBakedMesh);
                    var vertices = vertexBakedMesh.vertices;
                    var normals = vertexBakedMesh.normals;
                    for (int k = 0; k < meshRenderer.sharedMesh.vertexCount; k++)
                    {
                        var frame = startFrame + j;
                        var pixel = GPUAnimUtil.GetVertexPositionPixel(k, frame);
                        bakedTexture.SetPixel(pixel.x, pixel.y, ColorUtil.ToColor(vertices[k]));
                        pixel = GPUAnimUtil.GetVertexNormalPixel(k, frame);
                        bakedTexture.SetPixel(pixel.x, pixel.y, ColorUtil.ToColor(normals[k]));
                        BoundsIncrement.Iterate(vertices[k]);
                    }
                }

                vertexBakedMesh.Clear();
            }

            // 应用纹理
            bakedTexture.Apply();
            bounds = BoundsIncrement.End();
        }
        
        private static Mesh BakeMesh(SkinnedMeshRenderer meshRenderer, Bounds bounds)
        {
            var instancedMesh = meshRenderer.sharedMesh.Copy();
            instancedMesh.normals = null;
            instancedMesh.tangents = null;
            instancedMesh.boneWeights = null;
            instancedMesh.bindposes = null;
            instancedMesh.bounds = bounds;
            return instancedMesh;
        }

        /// <summary>
        /// 保存用于运行的动画数据（贴图，纹理和动画数据）
        /// </summary>
        private static void GenerateAssets(Mesh bakedMesh, Texture2D bakedTexture, AnimationTickerClip[] clipParams,
            string assetName, string saveDirPath)
        {
            var gpuAniData = ScriptableObject.CreateInstance<GPUAnimationData>();
            gpuAniData.BakedMode = EGPUAnimationMode._ANIM_VERTEX;
            gpuAniData.BakedMesh = bakedMesh;
            gpuAniData.BakeTexture = bakedTexture;
            gpuAniData.AnimationClips = clipParams;

            bakedTexture.name = $"{assetName}_AnimationAtlas";
            bakedMesh.name = $"{assetName}_InstanceMesh";
            gpuAniData = EditorAssetUtil.CreateAssetCombination($"{saveDirPath}{assetName}_GPU_Vertex.asset",
                gpuAniData, new UnityEngine.Object[]
                {
                    bakedMesh, bakedTexture
                });
            var assets = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(gpuAniData));
            foreach (var asset in assets)
            {
                if (asset is Texture2D texture2D)
                {
                    gpuAniData.BakeTexture = texture2D;
                }
                else if (asset is Mesh mesh)
                {
                    gpuAniData.BakedMesh = mesh;
                }
            }

            EditorUtility.SetDirty(gpuAniData);
            AssetDatabase.SaveAssets();
        }
    }
}