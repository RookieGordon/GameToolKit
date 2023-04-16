using Bonsai.Utility;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Bonsai.Core
{
    public class BonsaiTreeComponent : MonoBehaviour
    {
        /// <summary>
        /// The tree blueprint asset used.
        /// </summary>
        public string JsonPath;

        // Tree instance of the blueprint. This is a clone of the tree blueprint asset.
        // The tree instance is what runs in game.
        private BehaviourTree _treeInstance;

        public Button StepOverBtn;

        public Button ResumeBtn;

        void Awake()
        {
            if (JsonPath != null)
            {
                var str = FileHelper.ReadFile(JsonPath);
                var tree = SerializeHelper.DeSerializeObject<BehaviourTree>(str);
                _treeInstance = BehaviourTree.Clone(tree);
                _treeInstance.actor = gameObject;
                _treeInstance.AssetInstanceID = tree.AssetInstanceID;
                BehaviourTree.AfterInit?.Invoke(_treeInstance);
            }
            else
            {
                Debug.LogError("The behaviour tree is not set for " + gameObject);
            }

            // StepOverBtn.onClick.AddListener(() => _treeInstance.Debugger.StepOver = true);
            // ResumeBtn.onClick.AddListener(() => _treeInstance.Debugger.Running = false);
            
            
        }

        void Start()
        {
            _treeInstance.Start();
            _treeInstance.BeginTraversal();
        }

        void Update()
        {
            _treeInstance.UpdateTree(Time.deltaTime);
        }

        void OnDestroy()
        {
            // Destroy(_treeInstance);
        }

        /// <summary>
        /// The tree instance running in game.
        /// </summary>
        public BehaviourTree Tree
        {
            get { return _treeInstance; }
        }
    }
}