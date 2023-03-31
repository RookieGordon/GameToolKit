using UnityEngine;

namespace Bonsai.Core
{
    public abstract partial class BehaviourNode: ScriptableObject
    {
        /// <summary>
        /// The order of the node in preorder traversal.
        /// </summary>
        [SerializeField, HideInInspector] internal int preOrderIndex = 0;
        
        /// <summary>
        /// The game object associated with the tree of this node.
        /// </summary>
        protected GameObject Actor => this.treeOwner.actor;
        
        #region Node Editor Meta Data

#if UNITY_EDITOR

        /// <summary>
        /// Statuses used by the editor to know how to visually represent the node.
        /// It is the same as the Status enum but has extra enums useful to the editor.
        /// </summary>
        public enum StatusEditor
        {
            Success,
            Failure,
            Running,
            None,
            Aborted,
            Interruption
        };
        
        public StatusEditor StatusEditorResult { get; set; } = StatusEditor.None;

        // Hide. The BehaviourNode Editor will handle drawing.
        [HideInInspector] public string title;

        // Hide. The BehaviourNode Editor will handle drawing.
        // Use multi-line so BehaviourNode Editor applies correct styling as a text area.
        [HideInInspector, Multiline] public string comment;

        [HideInInspector] public Vector2 bonsaiNodePosition;
#endif

        #endregion
    }
}