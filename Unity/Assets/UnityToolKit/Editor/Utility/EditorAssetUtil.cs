/*
 * 功能描述：编辑器资源操作工具
 *           借鉴自 Unity3D-ToolChain_StriteR (UEAsset)
 */

using UnityEditor;
using UnityEngine;

namespace UnityToolKit.Editor.Utility
{
    public static class EditorAssetUtil
    {
        /// <summary>
        /// 创建带子资产的 ScriptableObject
        /// </summary>
        public static T CreateAssetCombination<T>(string path, T mainAsset, Object[] subAssets) where T : Object
        {
            AssetDatabase.CreateAsset(mainAsset, path);
            if (subAssets != null)
            {
                foreach (var subAsset in subAssets)
                {
                    if (subAsset != null)
                    {
                        AssetDatabase.AddObjectToAsset(subAsset, path);
                    }
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        /// <summary>
        /// 弹出文件夹选择对话框
        /// </summary>
        public static bool SelectDirectory(Object referenceAsset, out string savePath, out string assetName)
        {
            savePath = "";
            assetName = "";

            if (referenceAsset == null)
                return false;

            var assetPath = AssetDatabase.GetAssetPath(referenceAsset);
            assetName = System.IO.Path.GetFileNameWithoutExtension(assetPath);

            var folder = EditorUtility.SaveFolderPanel("选择保存目录", "Assets", "");
            if (string.IsNullOrEmpty(folder))
                return false;

            // 将绝对路径转为相对路径
            if (folder.StartsWith(Application.dataPath))
            {
                savePath = "Assets" + folder.Substring(Application.dataPath.Length) + "/";
            }
            else
            {
                Debug.LogError("保存目录必须在 Assets 目录下");
                return false;
            }

            return true;
        }
    }
}
