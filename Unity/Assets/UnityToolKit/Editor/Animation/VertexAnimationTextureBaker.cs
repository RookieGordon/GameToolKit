#if TEST
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using System.IO;
using Hotfixs.GameCfg.StaticData;
using PG.Hotfixs.HotfixsMain.Battle;
using System.Text.RegularExpressions;

namespace InstancedVAT
{
    public class VertexAnimationTextureBaker
    {
        private const int TEXELS_PER_BONE = 3;         //每个bone matrix占用3个texel
        private const int MAX_BONES_PER_VERTEX = 4;    //每个vertex最多受到4个bone的影响

        //带有GPU instancing、VAT的shader
        private static Dictionary<string, string> ToInstancedVAT = new Dictionary<string, string>()
        {
            { "Assets/Arts/Shaders/3D/Characters/ST_Unlit_Tex.shader", "Assets/Arts/Shaders/3D/Characters/ST_Unlit_Tex_Instanced_VAT.shader" },
            { "Assets/Arts/Shaders/3D/MapObj/ST_Map_Obj.shader", "Assets/Arts/Shaders/3D/MapObj/ST_Map_Obj_Instanced_VAT.shader"},
        };

        //带有GPU instancing，但是没有VAT的shader
        private static Dictionary<string, string> ToInstanced = new Dictionary<string, string>()
        {
            { "Assets/Arts/Shaders/3D/Characters/ST_Unlit_Tex.shader", "Assets/Arts/Shaders/3D/Characters/ST_Unlit_Tex_Instanced.shader" },
            { "Assets/Arts/Shaders/3D/Characters/ST_Role_Shadow_Quad.shader", "Assets/Arts/Shaders/3D/Characters/ST_Role_Shadow_Quad_Instanced.shader" },
            { "Assets/Arts/Shaders/3D/MapObj/ST_Map_Obj.shader", "Assets/Arts/Shaders/3D/MapObj/ST_Map_Obj_Instanced.shader"},
            { "Assets/Arts/Shaders/3D/MapObj/SC_Asset_Obj.shader", "Assets/Arts/Shaders/3D/MapObj/SC_Asset_Obj_Instanced.shader"},
        };

        private static HashSet<string> _excludedPrefabs = new HashSet<string>()
        {
            "Assets/Arts/Maps/World0/Zawu/Prefabs/Common_Muxiang_02.prefab",
        };

        [MenuItem("Assets/Instanced Prefab/转换选中的战斗prefab")]
        public static void ConvertSelectedSkinnedPrefab()
        {
            var prefab = Selection.activeObject as GameObject;
            if (prefab == null)
                return;
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            if (prefabPath.EndsWith(".prefab") == false)
                return;
            ConvertPrefab(prefabPath);
        }

        [MenuItem("Assets/Instanced Prefab/转换所有战斗prefab")]
        public static void ConvertAllBattlePrefab()
        {
            HashSet<string> convertedPrefabs = new HashSet<string>();
            var heroSkinCfgs = HeroSkinCfgCreater.GetData();
            foreach (var heroSkinCfg in heroSkinCfgs.Values)
            {
                var prefabPathArray = new string[]{heroSkinCfg.ModelStar1, heroSkinCfg.ModelStar2, heroSkinCfg.ModelStar3, heroSkinCfg.ModelStar4 };
                for (int i = 0, n = prefabPathArray.Length; i < n; i++)
                {
                    string srcPrefabPath = prefabPathArray[i];
                    if (_excludedPrefabs.Contains(srcPrefabPath))
                        continue;
                    var t_strModelName = Path.GetFileNameWithoutExtension(srcPrefabPath);
                    if (string.IsNullOrEmpty(t_strModelName))
                        continue;
                    if (!t_strModelName.EndsWith("_low"))
                    {
                        string lowPrefabPath = srcPrefabPath.Replace(t_strModelName, $"{t_strModelName}_low");
                        if (File.Exists(lowPrefabPath))
                        {
                            srcPrefabPath = lowPrefabPath;
                        }
                    }

                    if (convertedPrefabs.Add(srcPrefabPath))
                    {
                        ConvertPrefab(srcPrefabPath);
                    }
                }
            }
            var battleItemCfgs = BattleItemCfgCreater.GetData();
            foreach(var battleItemCfg in battleItemCfgs.Values)
            {
                string srcPrefabPath = battleItemCfg.AssetPath;
                if (_excludedPrefabs.Contains(srcPrefabPath))
                    continue;
                if (convertedPrefabs.Add(srcPrefabPath))
                {
                    ConvertPrefab(srcPrefabPath);
                }
            }
            var battleChestCfgs = BattleChestCfgCreater.GetData();
            foreach(var battleChestCfg in battleChestCfgs.Values)
            {
                string srcPrefabPath = battleChestCfg.ChestAssetPath;
                if (_excludedPrefabs.Contains(srcPrefabPath))
                    continue;
                if (convertedPrefabs.Add(srcPrefabPath))
                {
                    ConvertPrefab(srcPrefabPath);
                }
            }
        }

        public static void ConvertPrefab(string prefabPath)
        {
            Material outputMaterial = null;
            InstancePrefabV2 outputPrefab = null;
            string sourceMatPath = null;

            if (prefabPath.EndsWith(".prefab") == false)
                prefabPath += ".prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogError("加载prefab失败: " + prefabPath);
                return;
            }
            GameObject instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            Transform rootNode = instance.transform;

            SkinnedMeshRenderer[] skinnedMeshRenderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>();
            //SkinnedMeshRenderer skinnedMeshRenderer = instance.GetComponentInChildren<SkinnedMeshRenderer>();
            MeshRenderer meshRenderer = instance.GetComponentInChildren<MeshRenderer>();
            if (skinnedMeshRenderers != null && skinnedMeshRenderers.Length > 0)
            {/****************************************** 带骨骼动画 *********************************************/
                var clipPath_clipIndex = new Dictionary<string, int>();   //key: clipPath, value: clipIndex

                SkinnedMeshRenderer skinnedMeshRenderer = null;
                foreach (var element in skinnedMeshRenderers)
                {
                    if (skinnedMeshRenderer == null || element.sharedMesh.vertexCount > skinnedMeshRenderer.sharedMesh.vertexCount)
                        skinnedMeshRenderer = element;
                }

                Animator animator = instance.GetComponentInChildren<Animator>(true);
                var controller = animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
                if (controller == null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                    return;
                }
                string controllerPath = AssetDatabase.GetAssetPath(controller);
                string animatorDir = Path.GetDirectoryName(controllerPath);

                InstanceVATPrefabV2 _outputPrefab = ScriptableObject.CreateInstance<InstanceVATPrefabV2>();
                outputPrefab = _outputPrefab;
                _outputPrefab.states = new List<StateInfoV2>();
                _outputPrefab.clips = new List<ClipInfoV2>();
                _outputPrefab.boneCount = skinnedMeshRenderer.bones.Length;

                ClipInfoV2 last;
                var baseLayer = controller.layers[0];
                var states = baseLayer.stateMachine.states;
                foreach (var state in states)
                {
                    int stateNameHash = Animator.StringToHash(state.state.name);
                    if (state.state == baseLayer.stateMachine.defaultState)
                    {
                        _outputPrefab.defaultStateNameHash = stateNameHash;
                    }

                    if (state.state.motion is UnityEditor.Animations.BlendTree blendTree)
                    {
                        //To do: 未支持BlendTree
                    }
                    else if (state.state.motion is AnimationClip clip)
                    {
                        string clipPath = AssetDatabase.GetAssetPath(clip);
                        if (clipPath_clipIndex.TryGetValue(clipPath, out int clipIndex) == false)
                        {//如果未处理过这个AnimationClip
                            clipIndex = clipPath_clipIndex.Count;
                            clipPath_clipIndex.Add(clipPath, clipIndex);

                            ClipInfoV2 current = new ClipInfoV2
                            {
                                offsetRows = 0,
                                durationTime = clip.length,
                                isLooping = clip.isLooping,
                                fps = 15,    //暂时采用统一的fps，以后再支持个性化
                            };
                            if (_outputPrefab.clips.Count > 0)
                            {//如果不是第1个clip，则需要跳过前面的clips（计算offset rows）
                                last = _outputPrefab.clips[_outputPrefab.clips.Count - 1];
                                current.offsetRows = last.offsetRows + Mathf.CeilToInt(last.fps * last.durationTime) + 1;
                            }
                            _outputPrefab.clips.Add(current);
                        }
                        //将state和animationClip关联起来
                        _outputPrefab.states.Add(new StateInfoV2
                        {
                            stateNameHash = stateNameHash,
                            stateName = state.state.name,
                            clipIndex = clipIndex,
                        });
                    }
                    else if (state.state.motion == null)
                    {//如果这个state没有动画，则导出bindpose(只有2帧)
                        string clipPath = "";
                        if (clipPath_clipIndex.TryGetValue(clipPath, out int clipIndex) == false)
                        {//如果未处理过这个AnimationClip
                            clipIndex = clipPath_clipIndex.Count;
                            clipPath_clipIndex.Add(clipPath, clipIndex);

                            ClipInfoV2 current = new ClipInfoV2
                            {
                                offsetRows = 0,
                                isLooping = true,
                                fps = 15,
                                durationTime = 1.0f / 15.001f,
                            };
                            if (_outputPrefab.clips.Count > 0)
                            {//如果不是第1个clip，则需要跳过前面的clips（计算offset rows）
                                last = _outputPrefab.clips[_outputPrefab.clips.Count - 1];
                                current.offsetRows = last.offsetRows + Mathf.CeilToInt(last.fps * last.durationTime) + 1;
                            }
                            _outputPrefab.clips.Add(current);
                        }
                        //将state和animationClip关联起来
                        _outputPrefab.states.Add(new StateInfoV2
                        {
                            stateNameHash = stateNameHash,
                            stateName = state.state.name,
                            clipIndex = clipIndex,
                        });
                    }
                }

                //将收集到的所有AnimationClip烘培到一个Texture里面
                last = _outputPrefab.clips[_outputPrefab.clips.Count - 1];
                int rowCount = last.offsetRows + Mathf.CeilToInt(last.fps * last.durationTime) + 1;
                int columnCount = _outputPrefab.boneCount * TEXELS_PER_BONE;
                NativeArray<half4> textureBuffer = new NativeArray<half4>(rowCount * columnCount, Allocator.Temp);
                for (int clipIndex = 0; clipIndex < _outputPrefab.clips.Count; clipIndex++)
                {
                    ClipInfoV2 clipInfo = _outputPrefab.clips[clipIndex];
                    StateInfoV2 stateInfo = _outputPrefab.states.Find(element => element.clipIndex == clipIndex);

                    _BakeAnimationClipIntoTexture(clipInfo, animator, stateInfo.stateNameHash, skinnedMeshRenderer, rootNode, textureBuffer);
                }
                Texture2D outputTex = new Texture2D(columnCount, rowCount, TextureFormat.RGBAHalf, false);
                outputTex.SetPixelData(textureBuffer, 0);
                outputTex.Apply();
                textureBuffer.Dispose();
                string outputTexPath = Path.Combine(animatorDir, Path.GetFileNameWithoutExtension(controllerPath) + "_animTex.asset");
                AssetDatabase.CreateAsset(outputTex, outputTexPath);

                //将bone weights烘焙到uv1、uv2，生成一个新的mesh，代替原来的.fbx模型
                Mesh outputMesh = null;
                if (skinnedMeshRenderer.sharedMesh != null)
                {
                    outputMesh = _BakeBoneWeightsIntoMesh(skinnedMeshRenderer.sharedMesh);
                    string sourceMeshPath = AssetDatabase.GetAssetPath(skinnedMeshRenderer.sharedMesh);
                    string outputMeshPath = sourceMeshPath.Substring(0, sourceMeshPath.LastIndexOf('.')) + "_withBoneWeight.mesh";
                    AssetDatabase.CreateAsset(outputMesh, outputMeshPath);
                }
                outputPrefab.mesh = outputMesh;
                outputPrefab.objectSpace = CoordinateSpace.RootNode;    //骨骼动画instance参考的是RootNode，这是烘培anim texture的时候决定的
                outputPrefab.modelToRoot = Matrix4x4.identity;

                if (skinnedMeshRenderer.sharedMaterial != null)
                {
                    sourceMatPath = AssetDatabase.GetAssetPath(skinnedMeshRenderer.sharedMaterial);
                    outputMaterial = _ConvertMaterial(skinnedMeshRenderer.sharedMaterial, outputTex);
                }
            }
            else if (meshRenderer != null)
            {/****************************************** 不带骨骼动画 *********************************************/
                outputPrefab = ScriptableObject.CreateInstance<InstancePrefabV2>();
                outputPrefab.mesh = meshRenderer.GetComponent<MeshFilter>().sharedMesh;
                if (meshRenderer.transform == rootNode)
                {
                    outputPrefab.objectSpace = CoordinateSpace.RootNode;
                    outputPrefab.modelToRoot = Matrix4x4.identity;
                }
                else
                {
                    outputPrefab.objectSpace = CoordinateSpace.ModelNode;
                    //modelNode -> worldSpace -> rootNode
                    outputPrefab.modelToRoot = rootNode.worldToLocalMatrix * meshRenderer.transform.localToWorldMatrix;
                }

                if (meshRenderer.sharedMaterial != null)
                {
                    sourceMatPath = AssetDatabase.GetAssetPath(meshRenderer.sharedMaterial);
                    outputMaterial = _ConvertMaterial(meshRenderer.sharedMaterial);
                }
            }
            else
            {
                Debug.LogError("在prefab上面找不到SkinnedMeshRenderer或者MeshRenderer: " + prefabPath);
                UnityEngine.Object.DestroyImmediate(instance);
                return;
            }

            if (outputMaterial != null)
            {
                Regex re = new Regex(@"_instance_(\d+)\.mat");
                string outputMatPath = sourceMatPath.Substring(0, sourceMatPath.LastIndexOf(".")) + "_instance.mat";
                while (File.Exists(outputMatPath))
                {
                    Material existMaterial = AssetDatabase.LoadAssetAtPath<Material>(outputMatPath);
                    if (existMaterial.shader == outputMaterial.shader)
                    {//如果这个material已经存在，并且使用了同一个shader
                        //UnityEngine.Object.DestroyImmediate(outputMaterial);
                        //outputMaterial = existMaterial;
                        //outputMatPath = null;
                        break;
                    }
                    var match = re.Match(outputMatPath);
                    if (match.Success)
                    {
                        int number = int.Parse(match.Groups[1].Value) + 1;
                        outputMatPath = outputMatPath.Replace(match.Groups[0].Value, $"_instance_{number}.mat");
                    }
                    else
                    {
                        outputMatPath = outputMatPath.Replace("_instance.mat", "_instance_1.mat");
                    }
                }
                
                outputPrefab.material = outputMaterial;
                if (outputMatPath != null)
                {
                    AssetDatabase.CreateAsset(outputMaterial, outputMatPath);
                }
            }

            outputPrefab.layer = instance.layer;
            outputPrefab.rootRotation = rootNode.localRotation;
            outputPrefab.rootScale = rootNode.localScale;
            string outputPrefabPath = prefabPath.Substring(0, prefabPath.LastIndexOf('.')) + "_instance.asset";
            AssetDatabase.CreateAsset(outputPrefab, outputPrefabPath);

            UnityEngine.Object.DestroyImmediate(instance);
        }

        private static void _BakeAnimationClipIntoTexture(ClipInfoV2 clipInfo, Animator animator, int stateNameHash, SkinnedMeshRenderer renderer, Transform rootNode, 
            NativeArray<half4> output)
        {
            animator.applyRootMotion = true;
            animator.Play(stateNameHash, -1);
            animator.Update(0.0f);

            float4x4 worldToRootMatrix = rootNode.worldToLocalMatrix;
            int frameCount = Mathf.CeilToInt(clipInfo.fps * clipInfo.durationTime) + 1;
            float frameInterval = clipInfo.durationTime / (frameCount - 1);
            Transform[] bones = renderer.bones;
            int boneCount = bones.Length;
            int textureWidth = TEXELS_PER_BONE * boneCount;
            Matrix4x4[] bindposes = renderer.sharedMesh.bindposes;
            //每个关键帧 占用 1 row
            //每个 bone 占用 3 column 
            for (int rowIndex = clipInfo.offsetRows, rowEnd = clipInfo.offsetRows + frameCount;
                rowIndex < rowEnd;
                rowIndex++)
            {
                for (int boneIndex = 0; boneIndex < boneCount; boneIndex++)
                {
                    Transform boneTransform = bones[boneIndex];
                    if (boneTransform == null)
                        continue;
                    //modelSpace -> boneSpace -> worldSpace -> rootNode
                    float4x4 matrix = math.mul(math.mul(worldToRootMatrix, boneTransform.localToWorldMatrix), bindposes[boneIndex]);

                    int texelIndex = textureWidth * rowIndex + TEXELS_PER_BONE * boneIndex;
                    for (int i = 0; i < TEXELS_PER_BONE; i++)
                    {
                        //每个texel代表矩阵的一行
                        output[texelIndex + i] = new half4((half)matrix[0][i], (half)matrix[1][i], (half)matrix[2][i], (half)matrix[3][i]);
                    }
                }
                animator.Update(frameInterval);
            }
        }

        private static Mesh _BakeBoneWeightsIntoMesh(Mesh sourceMesh)
        {
            Mesh mesh = GameObject.Instantiate(sourceMesh);
            NativeArray<byte> bonesPerVertex = mesh.GetBonesPerVertex();    //记录各个顶点所关联的bone数量
            NativeArray<BoneWeight1> boneWeightsSource = mesh.GetAllBoneWeights();

            int vertexCount = mesh.vertices.Length;
            NativeArray<Vector4> uvs1 = new NativeArray<Vector4>(vertexCount, Allocator.Temp);
            NativeArray<Vector4> uvs2 = new NativeArray<Vector4>(vertexCount, Allocator.Temp);

            int startBoneWeightIndex = 0;

            for (int vertIndex = 0; vertIndex < vertexCount; vertIndex++)
            {
                float totalWeight = 0f;
                float totalWeightCapped = 0f;
                int bonesForThisVertex = bonesPerVertex[vertIndex];

                for (int i = 0; i < bonesForThisVertex; i++)
                {
                    BoneWeight1 currentBoneWeight = boneWeightsSource[startBoneWeightIndex + i];
                    totalWeight += currentBoneWeight.weight;
                    if (i < MAX_BONES_PER_VERTEX) 
                        totalWeightCapped += currentBoneWeight.weight;
                }

                float weightMultiplier = totalWeight / totalWeightCapped;
                int bonesToBake = math.min(MAX_BONES_PER_VERTEX, bonesForThisVertex);
                totalWeight = 0f;
                float4 uv1 = float4.zero;
                float4 uv2 = float4.zero;
                for (int i = 0; i < bonesToBake; i++)
                {
                    BoneWeight1 currentBoneWeight = boneWeightsSource[startBoneWeightIndex + i];
                    float adjustedWeight = currentBoneWeight.weight * weightMultiplier;
                    totalWeight += adjustedWeight;
                    if (i == 0) 
                        uv1 = new float4(currentBoneWeight.boneIndex, adjustedWeight, uv1.z, uv1.w);
                    else if (i == 1) 
                        uv1 = new float4(uv1.x, uv1.y, currentBoneWeight.boneIndex, adjustedWeight);
                    else if (i == 2) 
                        uv2 = new float4(currentBoneWeight.boneIndex, adjustedWeight, uv2.z, uv2.w);
                    else if (i == 3) 
                        uv2 = new float4(uv2.x, uv2.y, currentBoneWeight.boneIndex, adjustedWeight);
                }
                uvs1[vertIndex] = uv1;
                uvs2[vertIndex] = uv2;

                startBoneWeightIndex += bonesForThisVertex;
            }
            mesh.SetUVs(1, uvs1);
            mesh.SetUVs(2, uvs2);
            uvs1.Dispose();
            uvs2.Dispose();
            bonesPerVertex.Dispose();
            boneWeightsSource.Dispose();
            return mesh;
        }

        private static Material _ConvertMaterial(Material sourceMaterial, Texture2D animTexture)
        {
            Material outputMaterial = null;
            string sourceShaderPath = AssetDatabase.GetAssetPath(sourceMaterial.shader);
            if (ToInstancedVAT.TryGetValue(sourceShaderPath, out string replacedShaderPath))//带有GPU instancing、VAT
            {
                Shader replacedShader = AssetDatabase.LoadAssetAtPath<Shader>(replacedShaderPath);
                outputMaterial = new Material(replacedShader);
                outputMaterial.CopyMatchingPropertiesFromMaterial(sourceMaterial);
                outputMaterial.SetTexture("_AnimTexture", animTexture);
            }
            else
            {
                Debug.LogError($"这种shader不支持GPU instancing: {sourceShaderPath}, materialPath: {AssetDatabase.GetAssetPath(sourceMaterial)}");
            }
            return outputMaterial;
        }

        private static Material _ConvertMaterial(Material sourceMaterial)
        {
            Material outputMaterial = null;
            string sourceShaderPath = AssetDatabase.GetAssetPath(sourceMaterial.shader);
            if (ToInstanced.TryGetValue(sourceShaderPath, out string replacedShaderPath))//带有GPU instancing，没有VAT
            {
                Shader replacedShader = AssetDatabase.LoadAssetAtPath<Shader>(replacedShaderPath);
                outputMaterial = new Material(replacedShader);
                outputMaterial.CopyMatchingPropertiesFromMaterial(sourceMaterial);
            }
            else
            {
                Debug.LogError($"这种shader不支持GPU instancing: {sourceShaderPath}");
            }
            return outputMaterial;
        }
    }
}
#endif