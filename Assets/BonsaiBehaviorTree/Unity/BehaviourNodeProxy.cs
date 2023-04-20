using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Bonsai.Core;
using Bonsai.Utility;
using UnityEditor;

namespace Bonsai.Core
{
    public abstract class BehaviourNodeProxy : ScriptableObject
    {
        [SerializeReference] public BehaviourNode Node;

        public int NodeIndex = -1;

        public BehaviourNode.StatusEditor StatusEditorResult { get; set; } = BehaviourNode.StatusEditor.None;

        // Hide. The BehaviourNode Editor will handle drawing.
        [HideInInspector] public string title;

        // Hide. The BehaviourNode Editor will handle drawing.
        // Use multi-line so BehaviourNode Editor applies correct styling as a text area.
        [HideInInspector, Multiline] public string comment;

        [HideInInspector] public Vector2 bonsaiNodePosition;

        private void OnEnable()
        {
            if (NodeIndex >= 0)
            {
                return;
            }

            this.CreateNode();
        }

        private void CreateNode()
        {
            var nodeType = GetNodeType();
            Node = System.Activator.CreateInstance(nodeType) as BehaviourNode;
            Node.name = nodeType.Name;
            Node.AssetInstanceID = this.GetInstanceID();
#if UNITY_EDITOR
            Node.Proxy = this;
#endif
        }
        
        public void UpdateAssetInfo()
        {
            Node.name = Node.GetType().Name;
            Node.AssetInstanceID = this.GetInstanceID();
        }

        public virtual Type GetNodeType()
        {
            return typeof(BehaviourNode);
        }

        public void ConnectToProxy(BehaviourNode node)
        {
            Node = node;
            Node.Proxy = this;
        }
    }

    public abstract class CompositeProxy : BehaviourNodeProxy
    {
    }

    public abstract class ConditionalAbortProxy : DecoratorProxy
    {
    }

    public abstract class ConditionalTaskProxy : TaskProxy
    {
    }

    public abstract class DecoratorProxy : BehaviourNodeProxy
    {
    }

    public abstract class TaskProxy : BehaviourNodeProxy
    {
    }

    public abstract class ParallelCompositeProxy : CompositeProxy
    {
    }

    public abstract class ServiceProxy : DecoratorProxy
    {
    }
}