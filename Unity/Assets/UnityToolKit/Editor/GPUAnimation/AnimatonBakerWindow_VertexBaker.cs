/*
 ****************************************************
 * 作者：Gordon
 * 创建时间：2025/06/22 17:46:28
 * 功能描述：动画烘焙，烘焙顶点数据
 ****************************************************
 */

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityToolKit.Editor.Utility;
using UnityToolKit.Engine.Extension;
using UnityToolKit.Engine.Animation;
using UnityToolKit.Runtime.Utility;

namespace UnityToolKit.Editor.GPUAnimation
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
            try
            {
                var meshRenderer = instancedObj.GetComponentInChildren<SkinnedMeshRenderer>();
                if (meshRenderer == null)
                {
                    Debug.LogError("烘焙失败: Fbx模型中未找到 SkinnedMeshRenderer 组件");
                    return;
                }

                if (meshRenderer.sharedMesh == null)
                {
                    Debug.LogError("烘焙失败: SkinnedMeshRenderer 的 sharedMesh 为空");
                    return;
                }

                var bakedTextures = CreateVertexTextures(meshRenderer, clips, _maxAtlasSize,
                    out var clipParams, out int foldedWidth, out int rowsPerFrame);
                WriteVertexData(instancedObj, meshRenderer, clips, clipParams, bakedTextures,
                    foldedWidth, rowsPerFrame, out var minBounds);
                var instancedMesh = BakeMesh(meshRenderer, minBounds);
                DestroyImmediate(instancedObj);
                instancedObj = null;
                GenerateAssets(instancedMesh, bakedTextures, clipParams, rowsPerFrame, meshName, savePath);
            }
            catch (System.Exception e)
            {
                Debug.LogError("烘焙失败: " + e.Message + "\n" + e.StackTrace);
            }
            finally
            {
                if (instancedObj != null)
                    DestroyImmediate(instancedObj);
            }
        }

        /// <summary>
        /// 创建顶点数据纹理贴图（行折叠 + 自动拆分多张 Atlas）。
        /// 将每帧 vertexCount*2 像素的线性数据折叠到 foldedWidth 宽度，
        /// 避免纹理横纵比极端（如 10000×60），改善 GPU cache 命中率。
        /// </summary>
        private static Texture2D[] CreateVertexTextures(SkinnedMeshRenderer meshRenderer, AnimationClip[] clips,
            int maxAtlasSize, out AnimationTickerClip[] clipParams, out int foldedWidth, out int rowsPerFrame)
        {
            var vertexCount = meshRenderer.sharedMesh.vertexCount;
            int linearPixelsPerFrame = vertexCount * 2; // Position + Normal

            // 行折叠：将每帧的线性像素序列折叠到 maxAtlasSize 宽度以内
            foldedWidth = Mathf.Min(linearPixelsPerFrame, maxAtlasSize);
            rowsPerFrame = Mathf.CeilToInt((float)linearPixelsPerFrame / foldedWidth);

            // Atlas 拆分的高度限制 = maxAtlasSize / rowsPerFrame（以逻辑帧数计）
            int maxLogicalFrames = Mathf.Max(1, maxAtlasSize / rowsPerFrame);
            GetClipParams(clips, maxLogicalFrames, out clipParams, out int[] atlasLogicalHeights);

            var textures = new Texture2D[atlasLogicalHeights.Length];
            for (int i = 0; i < atlasLogicalHeights.Length; i++)
                textures[i] = CreateTexture(foldedWidth, atlasLogicalHeights[i] * rowsPerFrame);
            return textures;
        }

        /// <summary>
        /// 将动画顶点数据写入对应的 Atlas 贴图（使用行折叠像素坐标）
        /// </summary>
        private static void WriteVertexData(GameObject fbxObj, SkinnedMeshRenderer meshRenderer, AnimationClip[] clips,
            AnimationTickerClip[] clipParams, Texture2D[] bakedTextures, int foldedWidth, int rowsPerFrame,
            out Bounds bounds)
        {
            BoundsIncrement.Begin();
            for (int i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];
                var texture = bakedTextures[clipParams[i].TextureIndex];
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
                    var frame = startFrame + j;
                    for (int k = 0; k < meshRenderer.sharedMesh.vertexCount; k++)
                    {
                        // Position: linearIndex = k * 2
                        var pixel = GPUAnimUtil.GetVertexPixel(k * 2, frame, foldedWidth, rowsPerFrame);
                        texture.SetPixel(pixel.x, pixel.y, ColorUtil.ToColor(vertices[k]));
                        // Normal:   linearIndex = k * 2 + 1
                        pixel = GPUAnimUtil.GetVertexPixel(k * 2 + 1, frame, foldedWidth, rowsPerFrame);
                        texture.SetPixel(pixel.x, pixel.y, ColorUtil.ToColor(normals[k]));
                        BoundsIncrement.Iterate(vertices[k]);
                    }
                }

                vertexBakedMesh.Clear();
            }

            foreach (var tex in bakedTextures)
                tex.Apply();
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
        /// 保存用于运行的动画数据（多张 Atlas 纹理、Mesh 和动画数据）
        /// </summary>
        private static void GenerateAssets(Mesh bakedMesh, Texture2D[] bakedTextures, AnimationTickerClip[] clipParams,
            int rowsPerFrame, string assetName, string saveDirPath)
        {
            var gpuAniData = ScriptableObject.CreateInstance<GPUAnimationData>();
            gpuAniData.BakedMode = EGPUAnimationMode.ANIM_VERTEX;
            gpuAniData.BakedMesh = bakedMesh;
            gpuAniData.AnimationClips = clipParams;
            gpuAniData.RowsPerFrame = rowsPerFrame;

            for (int i = 0; i < bakedTextures.Length; i++)
                bakedTextures[i].name = $"{assetName}_AnimationAtlas_{i}";
            bakedMesh.name = $"{assetName}_InstanceMesh";
            var subAssets = new List<UnityEngine.Object>(bakedTextures);
            subAssets.Add(bakedMesh);
            gpuAniData = EditorAssetUtil.CreateAssetCombination($"{saveDirPath}{assetName}_GPU_Vertex.asset",
                gpuAniData, subAssets.ToArray());

            var texList = new List<Texture2D>();
            var assets = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(gpuAniData));
            foreach (var asset in assets)
            {
                if (asset is Texture2D texture2D)
                    texList.Add(texture2D);
                else if (asset is Mesh mesh)
                    gpuAniData.BakedMesh = mesh;
            }
            texList.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.Ordinal));
            gpuAniData.BakeTextures = texList.ToArray();

            EditorUtility.SetDirty(gpuAniData);
            AssetDatabase.SaveAssets();
        }
    }
}