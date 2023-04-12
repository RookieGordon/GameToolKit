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
            Tree.Proxy = this;
        }

        private void LoadBehaviourTree()
        {
            var jsonStr = FileHelper.ReadFile(JsonPath);
            Tree = SerializeHelper.DeSerializeObject<BehaviourTree>(jsonStr);
            Tree.Proxy = this;
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
            var subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
            foreach (var subAsset in subAssets)
            {
                if (subAsset is BehaviourNodeProxy nodeProxy)
                {
                    nodeProxy.ConnectToProxy(Tree.Nodes[nodeProxy.NodeIndex]);
                }
            }
        }

        [ContextMenu("Add Blackboard")]
        public void AddBlackboardAsset()
        {
            if (Tree.Blackboard == null && !EditorApplication.isPlaying)
            {
                var blackboardProxy = CreateInstance<BlackboardProxy>();
                blackboardProxy.AttachToBehaviourTree(this);
                // blackboardProxy.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(blackboardProxy, this);
            }
        }

        public void SetName()
        {
            Tree.name = this.name;
        }
    }
}