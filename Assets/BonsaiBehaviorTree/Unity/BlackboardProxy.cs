using System;
using UnityEngine;
using Bonsai.Utility;

namespace Bonsai.Core
{
    public class BlackboardProxy : ScriptableObject
    {
        public bool HasTree = false;

        [SerializeReference] public Blackboard Blackboard;

        private void OnEnable()
        {
            if (HasTree)
            {
                return;
            }

            this.CreateBlackboard();
        }

        private void CreateBlackboard()
        {
            Blackboard = System.Activator.CreateInstance<Blackboard>();
            Blackboard.name = "Blackboard";
            Blackboard.AssetInstanceID = GetInstanceID();
            Blackboard.Proxy = this;
            HasTree = true;
            //Log.LogInfo($"create new blackboard");
        }

        public void AttachToBehaviourTree(BehaviourTreeProxy treeProxy)
        {
            if (treeProxy.Tree != null && treeProxy.Tree.Blackboard == null)
            {
                treeProxy.Tree.Blackboard = Blackboard;
            }
        }
    }
}