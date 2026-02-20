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
using UnityToolKit.Engine.Optimize;
using UnityToolKit.Runtime.Utility;

namespace UnityToolKit.Editor.Engine.Optimize
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

                // 烘焙动画纹理
                var bakedTexture = CreateBoneTexture(skinnedMeshRenderer, clips, out var clipParams);
                WriteTransformData(instantiatedObj, skinnedMeshRenderer, clips, clipParams, bakedTexture);

                // 计算包围盒
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
                data.BakedMode = EGPUAnimationMode._ANIM_BONE;
                data.AnimationClips = clipParams;
                data.ExposeTransforms = exposeTransformParam.ToArray();

                bakedTexture.name = meshName + "_AnimationAtlas";
                instanceMesh.name = meshName + "_InstanceMesh";
                data = EditorAssetUtil.CreateAssetCombination(savePath + meshName + "_GPU_Bone.asset",
                    data, new Object[] { bakedTexture, instanceMesh });

                var assets = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(data));
                foreach (var asset in assets)
                {
                    if (asset is Texture2D texture2D)
                        data.BakeTexture = texture2D;
                    else if (asset is Mesh mesh)
                        data.BakedMesh = mesh;
                }

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
        /// 创建骨骼矩阵纹理贴图
        /// </summary>
        private static Texture2D CreateBoneTexture(SkinnedMeshRenderer render, AnimationClip[] clips,
            out AnimationTickerClip[] clipParams)
        {
            var transformCount = render.sharedMesh.bindposes.Length;
            var totalWidth = transformCount * 3;
            var totalFrame = GetClipParams(clips, out clipParams);
            return CreateTexture(Mathf.NextPowerOfTwo(totalWidth), Mathf.NextPowerOfTwo(totalFrame));
        }

        /// <summary>
        /// 将骨骼矩阵数据，写入到纹理贴图
        /// </summary>
        private void WriteTransformData(GameObject fbxObj, SkinnedMeshRenderer render, AnimationClip[] clips,
            AnimationTickerClip[] clipParams, Texture2D texture)
        {
            var bindPoses = render.sharedMesh.bindposes;
            var bones = render.bones;
            for (int i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];

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
                        var bindPoseToBoneAnimated = GetBoneMatrices(bindPoses[k], bones[k]);
                        var pixel = GPUAnimUtil.GetTransformPixel(k, 0, frame);
                        texture.SetPixel(pixel.x, pixel.y, bindPoseToBoneAnimated.GetRow(0).ToColor());
                        pixel = GPUAnimUtil.GetTransformPixel(k, 1, frame);
                        texture.SetPixel(pixel.x, pixel.y, bindPoseToBoneAnimated.GetRow(1).ToColor());
                        pixel = GPUAnimUtil.GetTransformPixel(k, 2, frame);
                        texture.SetPixel(pixel.x, pixel.y, bindPoseToBoneAnimated.GetRow(2).ToColor());
                    }
                }
            }

            texture.Apply();
        }

        private static Matrix4x4 GetBoneMatrices(Matrix4x4 bindPos, Transform bone)
        {
            var localToBoneAnimated = bone.localToWorldMatrix;
            var bindPoseToBoneAnimated = localToBoneAnimated * bindPos;
            return bindPoseToBoneAnimated;
        }
    }
}