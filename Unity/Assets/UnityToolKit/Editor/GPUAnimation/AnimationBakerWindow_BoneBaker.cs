/*
 ****************************************************
 * 作者：Gordon
 * 创建时间：2025/06/22 17:47:09
 * 功能描述：动画烘焙，烘焙骨骼动画数据
 *           借鉴自 Unity3D-ToolChain_StriteR
 ****************************************************
 */

using System.Collections.Generic;
using System.Text.RegularExpressions;
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
        private void BakeBoneAnimation(GameObject fbxObj, AnimationClip[] clips, string exposeBones)
        {
            if (!EditorAssetUtil.SelectDirectory(fbxObj, out string savePath, out string meshName))
            {
                Debug.LogWarning("未选择有效的保存目录");
                return;
            }

            var instantiatedObj = GameObject.Instantiate(fbxObj);
            var skinnedMeshRenderer = instantiatedObj.GetComponentInChildren<SkinnedMeshRenderer>();
            try
            {
                var bindPoses = skinnedMeshRenderer.sharedMesh.bindposes;
                var bones = skinnedMeshRenderer.bones;

                // 记录暴露骨骼
                var exposeTransformParam = RecordExposeBone(exposeBones, instantiatedObj, skinnedMeshRenderer, bones);

                // 烘焙动画纹理（自动按 Atlas 尺寸上限拆分为多张纹理）
                var bakedTextures = CreateBoneTextures(skinnedMeshRenderer, clips, _maxAtlasSize, out var clipParams);
                WriteTransformData(instantiatedObj, skinnedMeshRenderer, clips, clipParams, bakedTextures);

                // 计算包围盒（使用缩放后的帧率）
                BoundsIncrement.Begin();
                for (var i = 0; i < clips.Length; i++)
                {
                    var clip = clips[i];
                    var length = clip.length;
                    var frameRate = clip.frameRate;
                    var frameCount = (int)(length * frameRate);
                    for (var j = 0; j < frameCount; j++)
                    {
                        clip.SampleAnimation(instantiatedObj, length * j / frameCount);
                        var boundsCheckMesh = new Mesh();
                        skinnedMeshRenderer.BakeMesh(boundsCheckMesh);
                        var vertices = boundsCheckMesh.vertices;
                        for (var k = 0; k < vertices.Length; k++)
                        {
                            BoundsIncrement.Iterate(vertices[k].Div(skinnedMeshRenderer.transform.localScale));
                        }
                        boundsCheckMesh.Clear();
                    }
                }
                var bounds = BoundsIncrement.End();

                // 烘焙Mesh（将骨骼权重写入UV1/UV2）
                var instanceMesh = skinnedMeshRenderer.sharedMesh.Copy();
                var transformWeights = instanceMesh.boneWeights;
                var uv1 = new Vector4[transformWeights.Length];
                var uv2 = new Vector4[transformWeights.Length];
                for (var i = 0; i < transformWeights.Length; i++)
                {
                    uv1[i] = new Vector4(transformWeights[i].boneIndex0, transformWeights[i].boneIndex1,
                        transformWeights[i].boneIndex2, transformWeights[i].boneIndex3);
                    uv2[i] = new Vector4(transformWeights[i].weight0, transformWeights[i].weight1,
                        transformWeights[i].weight2, transformWeights[i].weight3);
                }
                instanceMesh.SetUVs(1, uv1);
                instanceMesh.SetUVs(2, uv2);
                instanceMesh.boneWeights = null;
                instanceMesh.bindposes = null;
                instanceMesh.bounds = bounds;

                DestroyImmediate(instantiatedObj);

                // 保存资产
                var data = ScriptableObject.CreateInstance<GPUAnimationData>();
                data.BakedMode = EGPUAnimationMode.ANIM_BONE;
                data.AnimationClips = clipParams;
                data.ExposeTransforms = exposeTransformParam.ToArray();

                for (int i = 0; i < bakedTextures.Length; i++)
                    bakedTextures[i].name = meshName + "_AnimationAtlas_" + i;
                instanceMesh.name = meshName + "_InstanceMesh";
                var subAssets = new List<Object>(bakedTextures);
                subAssets.Add(instanceMesh);
                data = EditorAssetUtil.CreateAssetCombination(savePath + meshName + "_GPU_Bone.asset",
                    data, subAssets.ToArray());

                var texList = new List<Texture2D>();
                var assets = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(data));
                foreach (var asset in assets)
                {
                    if (asset is Texture2D texture2D)
                        texList.Add(texture2D);
                    else if (asset is Mesh mesh)
                        data.BakedMesh = mesh;
                }
                texList.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.Ordinal));
                data.BakeTextures = texList.ToArray();

                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
            }
            catch (System.Exception e)
            {
                Debug.LogError("烘焙失败: " + e.Message + "\n" + e.StackTrace);
                DestroyImmediate(instantiatedObj);
            }
        }

        private List<GPUAnimationExposeBone> RecordExposeBone(string exposeBones, GameObject instantiatedObj,
            SkinnedMeshRenderer skinnedMeshRenderer, Transform[] bones)
        {
            var result = new List<GPUAnimationExposeBone>();
            if (string.IsNullOrEmpty(exposeBones))
                return result;

            var activeTransforms = instantiatedObj.GetComponentsInChildren<Transform>();
            foreach (var activeTransform in activeTransforms)
            {
                if (!Regex.Match(activeTransform.name, exposeBones).Success)
                    continue;

                var relativeBoneIndex = -1;
                var relativeBone = activeTransform;
                while (relativeBone != null)
                {
                    relativeBoneIndex = System.Array.FindIndex(bones, p => p == relativeBone);
                    if (relativeBoneIndex != -1)
                        break;
                    relativeBone = relativeBone.parent;
                }

                if (relativeBoneIndex == -1)
                    continue;

                var rootWorldToLocal = skinnedMeshRenderer.transform.worldToLocalMatrix;

                result.Add(new GPUAnimationExposeBone()
                {
                    Index = relativeBoneIndex,
                    Name = activeTransform.name,
                    Position = rootWorldToLocal.MultiplyPoint(activeTransform.transform.position),
                    Direction = rootWorldToLocal.MultiplyVector(activeTransform.transform.forward)
                });
            }

            return result;
        }

        /// <summary>
        /// 创建骨骼矩阵纹理贴图（自动按高度拆分为多张 Atlas）
        /// </summary>
        private static Texture2D[] CreateBoneTextures(SkinnedMeshRenderer render, AnimationClip[] clips,
            int maxAtlasSize, out AnimationTickerClip[] clipParams)
        {
            var transformCount = render.sharedMesh.bindposes.Length;
            // 骨骼压缩：每根骨骼 2 像素（四元数 + 平移），替代原先的 3 像素（矩阵行）
            var totalWidth = transformCount * 2;

            if (totalWidth > maxAtlasSize)
                Debug.LogWarning($"骨骼纹理宽度 ({totalWidth}) 超出 Atlas 限制 ({maxAtlasSize})，部分设备可能不支持");

            GetClipParams(clips, maxAtlasSize, out clipParams, out int[] atlasHeights);
            var textures = new Texture2D[atlasHeights.Length];
            for (int i = 0; i < atlasHeights.Length; i++)
                textures[i] = CreateTexture(totalWidth, atlasHeights[i]);
            return textures;
        }

        /// <summary>
        /// 将骨骼矩阵数据，写入到对应的 Atlas 纹理贴图
        /// </summary>
        private void WriteTransformData(GameObject fbxObj, SkinnedMeshRenderer render, AnimationClip[] clips,
            AnimationTickerClip[] clipParams, Texture2D[] textures)
        {
            var bindPoses = render.sharedMesh.bindposes;
            var bones = render.bones;
            for (int i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];
                var texture = textures[clipParams[i].TextureIndex];

                var length = clip.length;
                var frameRate = clip.frameRate;
                var frameCount = (int)(length * frameRate);
                var startFrame = clipParams[i].FrameBegin;
                for (int j = 0; j < frameCount; j++)
                {
                    clip.SampleAnimation(fbxObj, length * j / frameCount);
                    for (int k = 0; k < bindPoses.Length; k++)
                    {
                        var frame = startFrame + j;
                        var skinMatrix = GetBoneMatrices(bindPoses[k], bones[k]);

                        // 将蒙皮矩阵分解为四元数（旋转）+ 平移
                        // 每根骨骼从 3 像素压缩到 2 像素，纹理宽度减少 33%
                        Quaternion rotation = skinMatrix.rotation;
                        Vector3 translation = new Vector3(skinMatrix.m03, skinMatrix.m13, skinMatrix.m23);

                        // 统一四元数到正 w 半球，确保帧间插值一致性
                        if (rotation.w < 0)
                        {
                            rotation.x = -rotation.x;
                            rotation.y = -rotation.y;
                            rotation.z = -rotation.z;
                            rotation.w = -rotation.w;
                        }

                        // 像素 0: 四元数 (x, y, z, w)
                        var pixel = GPUAnimUtil.GetTransformPixel(k, 0, frame);
                        texture.SetPixel(pixel.x, pixel.y,
                            new Color(rotation.x, rotation.y, rotation.z, rotation.w));
                        // 像素 1: 平移 (x, y, z, 0)
                        pixel = GPUAnimUtil.GetTransformPixel(k, 1, frame);
                        texture.SetPixel(pixel.x, pixel.y,
                            new Color(translation.x, translation.y, translation.z, 0));
                    }
                }
            }

            foreach (var tex in textures)
                tex.Apply();
        }

        private static Matrix4x4 GetBoneMatrices(Matrix4x4 bindPos, Transform bone)
        {
            var localToBoneAnimated = bone.localToWorldMatrix;
            var bindPoseToBoneAnimated = localToBoneAnimated * bindPos;
            return bindPoseToBoneAnimated;
        }
    }
}