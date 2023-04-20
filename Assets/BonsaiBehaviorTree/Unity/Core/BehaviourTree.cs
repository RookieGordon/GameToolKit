using System;
using UnityEngine;

namespace Bonsai.Core
{
#if UNITY_EDITOR
    public partial class BehaviourTree
    {
        [Newtonsoft.Json.JsonIgnore] public BehaviourTreeProxy Proxy;

        /// <summary>
        /// <para>The game object actor associated with the tree.</para>
        /// <para>Field is optional. The tree core can run without the actor.</para>
        /// </summary>
        [Newtonsoft.Json.JsonIgnore] public GameObject actor;

        private void SetNodeProxyIndex(BehaviourNode node)
        {
            if (node.Proxy != null)
            {
                node.Proxy.NodeIndex = node.preOrderIndex;
            }
        }
    }
#endif
}