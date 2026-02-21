/*
 * author       : QLR-3-50
 * datetime     : 2/12/2025 4:47:21 PM
 * description  : 只支持 pivot = (0.5, 0.5) 的Image，使用这个工具之前需要手动将所有 pivot 改为 (0.5, 0.5) !!!!!!
 *                目前还不支持九宫格slice
 * */
#if TEST
using InstancedVAT;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System.IO;
using UnityEngine.UI;
using System.Text;
using TMPro;

public class UiBaker
{
    const string INSTANCED_UI_MESH_PATH = "Assets/Arts/CommonDeps/InstancedUI.mesh";
    const string CLASH_ROYALE_NUMBER_GLYPH_INFO_PATH = "Assets/Arts/UI/Fonts/ClashRoyale_NumberGlyphInfo.asset";
    const string NODES_DIR = "Assets/Arts/UI/NodesForInstance";
    const string MATERIALS_DIR = "Assets/Arts/UI/MaterialsForInstance";

    private static int s_renderOrder = 0;

    [MenuItem("Assets/Instanced Prefab/转换选中的UI prefab")]
    public static void ConvertSelectedPrefab()
    {
        var srcPrefab = Selection.activeObject as GameObject;
        if (srcPrefab == null)
            return;
        ConvertPrefab(srcPrefab);
    }

    public static void ConvertPrefab(GameObject srcPrefab)
    {
        string srcPrefabPath = AssetDatabase.GetAssetPath(srcPrefab);
        string outputDir = NODES_DIR + '/' + srcPrefab.name;
        if (AssetDatabase.IsValidFolder(outputDir) == false)
        {
            Directory.CreateDirectory(outputDir);
        }
        var compositeInstancePrefab = ScriptableObject.CreateInstance<CompositeInstancePrefab>();
        s_renderOrder = 0;

        GameObject instance = PrefabUtility.InstantiatePrefab(srcPrefab) as GameObject;
        RectTransform rootNode = instance.GetComponent<RectTransform>();

        ConvertNode(rootNode, rootNode, "", outputDir, compositeInstancePrefab);

        string compositeInstancePrefabPath = srcPrefabPath.Substring(0, srcPrefabPath.LastIndexOf('.')) + "_instance.asset";
        AssetDatabase.CreateAsset(compositeInstancePrefab, compositeInstancePrefabPath);

        UnityEngine.Object.DestroyImmediate(instance);
    }

    private static void ConvertNode(RectTransform node, RectTransform rootNode, string parentHierarchy, string outputDir, CompositeInstancePrefab outputPrefab)
    {
        string hierarchy = string.IsNullOrEmpty(parentHierarchy) ? node.name : parentHierarchy + '/' + node.name;
        var pivot = node.pivot;
        if (pivot.x != 0.5f || pivot.y != 0.5f)
        {
            Debug.LogError($"{hierarchy} 的 pivot 必须设置为 (0.5, 0.5) !!!");
        }
        else
        {
            //尝试获取Image
            Image image = node.GetComponent<Image>();
            if (image != null)
            {

                Material srcMaterial = image.material;
                Material outputMaterial = null;
                InstanceUiPrefab prefabForThisNode = ScriptableObject.CreateInstance<InstanceUiPrefab>();
                prefabForThisNode.rootRotation = rootNode.localRotation;
                prefabForThisNode.rootScale = rootNode.localScale;
                prefabForThisNode.mesh = AssetDatabase.LoadAssetAtPath<Mesh>(INSTANCED_UI_MESH_PATH);
                prefabForThisNode.layer = node.gameObject.layer;
                prefabForThisNode.renderOrder = s_renderOrder++;
                //UI模型的本地空间必须设置为ModelNode（即使Image位于rootNode），原因是
                //①共享同一个mesh的情况下，也能够实现灵活多变的sizeDelta
                //②需要微调Z坐标，利用深度测试来模拟UI遮挡
                Vector3 localPosition = node.localPosition;
                localPosition.z = 0f;   //确保导出前的Z坐标为0
                node.localPosition = localPosition;
                prefabForThisNode.objectSpace = CoordinateSpace.ModelNode;
                Vector2 sizeDelta = node.sizeDelta;
                Vector3 s = new Vector3(sizeDelta.x, sizeDelta.y, 1f);
                Vector3 t = new Vector3(0f, 0f, -1f * prefabForThisNode.renderOrder);
                //modelNode > worldSpace > rootNode
                prefabForThisNode.modelToRoot = rootNode.worldToLocalMatrix * node.localToWorldMatrix * Matrix4x4.TRS(t, Quaternion.identity, s);

                prefabForThisNode.hierarchy = hierarchy;
                prefabForThisNode.activeInHierarchy = node.gameObject.activeInHierarchy;
                prefabForThisNode.sprite = image.sprite;
                prefabForThisNode.color = image.color.linear;//per instance data不会自动转换颜色空间，因此需要手动转换为linear颜色空间
                prefabForThisNode.imageType = image.type;
                if (image.type == Image.Type.Simple)
                {
                    Shader shader = null;
                    if (node.gameObject.tag == "UI3dTransparent")
                    {
                        shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Arts/Shaders/UI/UI_3D_Simple_Transparent_Instanced.shader");
                    }
                    else
                    {
                        shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Arts/Shaders/UI/UI_3D_Simple_Instanced.shader");
                    }
                    outputMaterial = new Material(shader);
                }
                else if (image.type == Image.Type.Filled)
                {
                    prefabForThisNode.fillMethod = image.fillMethod;

                    if (image.fillMethod == Image.FillMethod.Radial360)
                    {
                        //StartDegree
                        if (image.fillOrigin == (int)Image.Origin360.Top)
                            prefabForThisNode.startDegree = 270f;
                        else if (image.fillOrigin == (int)Image.Origin360.Right)
                            prefabForThisNode.startDegree = 0f;
                        else if (image.fillOrigin == (int)Image.Origin360.Bottom)
                            prefabForThisNode.startDegree = 90f;
                        else if (image.fillOrigin == (int)Image.Origin360.Left)
                            prefabForThisNode.startDegree = 180f;

                        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Arts/Shaders/UI/UI_3D_Filled_360_Instanced.shader");
                        outputMaterial = new Material(shader);
                    }
                    else if (image.fillMethod == Image.FillMethod.Horizontal)
                    {
                        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Arts/Shaders/UI/UI_3D_Filled_Horizontal_Instanced.shader");
                        outputMaterial = new Material(shader);
                    }
                }
                if (outputMaterial != null)
                {
                    //outputMaterial.CopyMatchingPropertiesFromMaterial(srcMaterial);
                    //_MainTex
                    string texturePath = "";
                    if (image.sprite != null)
                    {
                        outputMaterial.SetTexture("_MainTex", image.sprite.texture);
                        texturePath = AssetDatabase.GetAssetPath(image.sprite.texture);
                    }
                    //目前使用的是TexturePacker的图集，因此texturePath == 图集路径。如果使用了unity内置的图集，则应该改为shader.name + SpriteAtlas path
                    string outputMaterialName = Hash128.Compute(outputMaterial.shader.name + texturePath).ToString() + ".mat";
                    string outputMaterialPath = MATERIALS_DIR + '/' + outputMaterialName;
                    AssetDatabase.CreateAsset(outputMaterial, outputMaterialPath);
                    prefabForThisNode.material = outputMaterial;
                }
                string prefabNameForThisNode = hierarchy.Replace('/', '_') + "_instance.asset";
                string prefabPathForThisNode = outputDir + '/' + prefabNameForThisNode;
                AssetDatabase.CreateAsset(prefabForThisNode, prefabPathForThisNode);
                if (outputPrefab.nodes == null)
                {
                    outputPrefab.nodes = new List<InstancePrefabV2>();
                }
                outputPrefab.nodes.Add(prefabForThisNode);
            }
            else
            {
                //尝试获取TextMesh
                var textMesh = node.GetComponent<TextMeshProUGUI>();
                if (textMesh != null)
                {
                    string text = textMesh.text;
                    if (int.TryParse(text, out int number))
                    {
                        InstanceNumberText prefabForThisNode = ScriptableObject.CreateInstance<InstanceNumberText>();
                        prefabForThisNode.rootRotation = rootNode.localRotation;
                        prefabForThisNode.rootScale = rootNode.localScale;
                        prefabForThisNode.mesh = AssetDatabase.LoadAssetAtPath<Mesh>(INSTANCED_UI_MESH_PATH);
                        prefabForThisNode.layer = node.gameObject.layer;
                        prefabForThisNode.renderOrder = s_renderOrder++;
                        Vector3 localPosition = node.localPosition;
                        localPosition.z = 0f;   //确保导出前的Z坐标为0
                        node.localPosition = localPosition;
                        prefabForThisNode.objectSpace = CoordinateSpace.ModelNode;
                        //modelNode > worldSpace > rootNode
                        prefabForThisNode.modelToRoot = rootNode.worldToLocalMatrix * node.localToWorldMatrix;

                        prefabForThisNode.hierarchy = hierarchy;
                        prefabForThisNode.activeInHierarchy = node.gameObject.activeInHierarchy;
                        prefabForThisNode.color = textMesh.color.linear;//per instance data不会自动转换颜色空间，因此需要手动转换为linear颜色空间

                        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Arts/Shaders/UI/UI_3D_Simple_Instanced.shader");
                        Material outputMaterial = new Material(shader);
                        if (textMesh.font.name == "ClashRoyale SDF")
                        {
                            prefabForThisNode.glyphInfo = AssetDatabase.LoadAssetAtPath<NumberGlyphInfo>(CLASH_ROYALE_NUMBER_GLYPH_INFO_PATH);
                            Texture2D mainTex = prefabForThisNode.glyphInfo.sprites[0].texture;
                            outputMaterial.SetTexture("_MainTex", mainTex);
                            string texturePath = AssetDatabase.GetAssetPath(mainTex);
                            //目前使用的是TexturePacker的图集，因此texturePath == 图集路径。如果使用了unity内置的图集，则应该改为shader.name + SpriteAtlas path
                            string outputMaterialName = Hash128.Compute(outputMaterial.shader.name + texturePath).ToString() + ".mat";
                            string outputMaterialPath = MATERIALS_DIR + '/' + outputMaterialName;
                            AssetDatabase.CreateAsset(outputMaterial, outputMaterialPath);
                        }
                        else 
                        {
                            Debug.LogError("目前还不支持这种字体: " + textMesh.font.name);
                        }
                        prefabForThisNode.material = outputMaterial;
                        prefabForThisNode.Number = number;
                        prefabForThisNode.AutoSize = textMesh.enableAutoSizing;
                        prefabForThisNode.FontSizeMax = prefabForThisNode.AutoSize ? textMesh.fontSizeMax : textMesh.fontSize;
                        prefabForThisNode.SizeDeltaX = node.sizeDelta.x;
                        prefabForThisNode.Alignment = textMesh.horizontalAlignment;

                        string prefabNameForThisNode = hierarchy.Replace('/', '_') + "_instance.asset";
                        string prefabPathForThisNode = outputDir + '/' + prefabNameForThisNode;
                        AssetDatabase.CreateAsset(prefabForThisNode, prefabPathForThisNode);
                        if (outputPrefab.nodes == null)
                        {
                            outputPrefab.nodes = new List<InstancePrefabV2>();
                        }
                        outputPrefab.nodes.Add(prefabForThisNode);
                    }
                }
            }
        }
        //递归处理各个子节点（深度优先遍历，UI的渲染顺序也是深度优先遍历）
        for(int i = 0; i < node.childCount; i++)
        {
            var t_pNode = node.GetChild(i) as RectTransform;
            ConvertNode(t_pNode, rootNode, hierarchy, outputDir, outputPrefab);
        }
    }

    /* 1-----2
     * |   / |
     * |  /  |
     * | /   |
     * |/    |
     * 0-----3 */
    [MenuItem("Assets/Instanced Prefab/生成UI公共mesh")]
    public static void GenerateInstancedUIMesh()
    {
        Mesh mesh = new Mesh();
        mesh.vertices = new Vector3[] 
        { 
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
        };
        mesh.uv = new Vector2[]
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f),
        };
        mesh.triangles = new int[6] { 0, 1, 2, 0, 2, 3 };
        AssetDatabase.CreateAsset(mesh, INSTANCED_UI_MESH_PATH);
    }

    [MenuItem("Assets/Instanced Prefab/测试")]
    public static void Test()
    {
        StringBuilder sb = new StringBuilder();
        GameObject go = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Arts/UI/Prefabs/BattleView/MonsterInfo.prefab");
        Transform rootNode = go.transform;
        Transform bossIconNode = rootNode.Find("goBossMonster/BossIcon");
        sb.Append("rootNode\nposition: ").Append(rootNode.position).Append("\nscale: ").Append(rootNode.lossyScale).Append("\n\n")
            .Append("BossIconNode\nposition: ").Append(bossIconNode.position).Append("\nscale: ").Append(bossIconNode.lossyScale);
        Debug.Log(sb.ToString());
    }
}

#endif