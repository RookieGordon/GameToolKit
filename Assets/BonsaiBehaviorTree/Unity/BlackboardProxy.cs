using System;
using UnityEngine;
using Bonsai.Utility;

namespace Bonsai.Core
{
    public class BlackboardProxy : ScriptableObject
    {
        public bool IsEmpty = true;

        [SerializeReference] public Blackboard Blackboard;

        private void OnEnable()
        {
            if (!IsEmpty)
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
            IsEmpty = false;
        }
        
        public void UpdateAssetInfo()
        {
            Blackboard.name = this.name;
            Blackboard.AssetInstanceID = this.GetInstanceID();
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