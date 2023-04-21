using System;
using System.Collections.Generic;
using UnityEngine;
using Bonsai.Utility;
using UnityEditor;

namespace Bonsai.Core
{
    [CreateAssetMenu(fileName = "BehaviourTree", menuName = "Bonsai/Behaviour Tree")]
    public class BehaviourTreeProxy : ScriptableObject
    {
        [SerializeReference] public BehaviourTree Tree;

        public string JsonPath;

        [HideInInspector] public Vector2 panPosition = Vector2.zero;

        [HideInInspector] public Vector2 zoomPosition = Vector2.one;

        /// <summary>
        /// Unused nodes are nodes that are not part of the root.
        /// These are ignored when tree executes and excluded when cloning.
        /// </summary>
        [SerializeField, HideInInspector] public List<BehaviourNode> UnusedNodes = new List<BehaviourNode>();

        private void CreateTree()
        {
            Tree = System.Activator.CreateInstance<BehaviourTree>();
            Tree.name = "BehaviourTree";
            Tree.AssetInstanceID = this.GetInstanceID();
#if UNITY_EDITOR
            Tree.Proxy = this;
#endif
        }

        private void LoadBehaviourTree()
        {
            var jsonStr = FileHelper.ReadFile(JsonPath);
            Tree = TreeSerializeHelper.DeSerializeObject<BehaviourTree>(jsonStr);
#if UNITY_EDITOR
            Tree.Proxy = this;
#endif
        }

        public void InitBehaviourTree()
        {
            if (!string.IsNullOrEmpty(JsonPath))
            {
                this.LoadBehaviourTree();
            }
            else
            {
                this.CreateTree();
            }
        }

        public void ReConnectDataToNodeProxy()
        {
            var path = AssetDatabase.GetAssetPath(this);
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            foreach (var asset in assets)
            {
                if (asset is BehaviourNodeProxy nodeProxy && nodeProxy.NodeIndex >= 0)
                {
                    nodeProxy.ConnectToProxy(Tree.Nodes[nodeProxy.NodeIndex]);
                }
            }
        }

        public void BindTreeWithProxy(BehaviourTree tree)
        {
            Tree = tree;
#if UNITY_EDITOR
            Tree.Proxy = this;
#endif
            this.ReConnectDataToNodeProxy();
        }

        [ContextMenu("Add Blackboard")]
        public void AddBlackboardAsset()
        {
            if (Tree.Blackboard == null && !EditorApplication.isPlaying)
            {
                var blackboardProxy = CreateInstance<BlackboardProxy>();
                blackboardProxy.AttachToBehaviourTree(this);
                blackboardProxy.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(blackboardProxy, this);
            }
        }

        public void UpdateAssetInfo()
        {
            Tree.name = this.name;
            Tree.AssetInstanceID = this.GetInstanceID();
        }
    }
}