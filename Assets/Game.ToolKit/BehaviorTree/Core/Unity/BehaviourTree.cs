using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Bonsai.Core
{
    [CreateAssetMenu(fileName = "BehaviourTree", menuName = "Bonsai/Behaviour Tree")]
    public partial class BehaviourTree : ScriptableObject
    {
        /// <summary>
        /// allNodes must always be kept in pre-order.
        /// </summary>
        [SerializeField, HideInInspector]
#pragma warning disable IDE0044 // Add readonly modifier
        private BehaviourNode[] allNodes = { };
#pragma warning restore IDE0044 // Add readonly modifier
        
        [SerializeField, HideInInspector] public Blackboard Blackboard;
        
        /// <summary>
        /// <para>The game object actor associated with the tree.</para>
        /// <para>Field is optional. The tree core can run without the actor.</para>
        /// </summary>
        public GameObject actor;
        
#if UNITY_EDITOR
        [ContextMenu("Add Blackboard")]
        void AddBlackboardAsset()
        {
            if (Blackboard == null && !EditorApplication.isPlaying)
            {
                Blackboard = CreateInstance<Blackboard>();
                Blackboard.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(Blackboard, this);
            }
        }

        [HideInInspector] public Vector2 panPosition = Vector2.zero;

        [HideInInspector] public Vector2 zoomPosition = Vector2.one;

        /// <summary>
        /// Unused nodes are nodes that are not part of the root.
        /// These are ignored when tree executes and excluded when cloning.
        /// </summary>
        [SerializeField, HideInInspector] public List<BehaviourNode> unusedNodes = new List<BehaviourNode>();

#endif
    }
}