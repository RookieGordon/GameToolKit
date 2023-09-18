using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Bonsai.Designer
{
    public class BonsaiAssetEventHandler : AssetModificationProcessor
    {
        /// <summary>
        /// 资源即将被删除
        /// </summary>
        public static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions option)
        {
            if (!assetPath.Contains(".asset"))
            {
                return AssetDeleteResult.DidNotDelete;
            }

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset != null && asset is not Core.BehaviourTreeProxy)
            {
                return AssetDeleteResult.DidNotDelete;
            }

            var treeName = asset.name;
            var treeJsonPath = BonsaiPreferences.Instance.jsonPath;
            string treeJsonFullPath = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, treeJsonPath)), $"{treeName}.json");
            try
            {
                if (File.Exists(treeJsonFullPath))
                {
                    File.Delete(treeJsonFullPath);
                    Debug.Log($"{treeName}对应的Json文件删除成功！");
                }
                else
                {
                    Debug.Log($"文件{treeJsonFullPath}不存在！");
                }
            }
            catch (IOException e)
            {
                Debug.LogError("删除文件失败，原因：" + e.Message);
            }

            return AssetDeleteResult.DidNotDelete;
        }
    }
}