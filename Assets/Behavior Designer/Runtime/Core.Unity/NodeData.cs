using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace BehaviorDesigner.Runtime
{
    partial class NodeData
    {
        [SerializeField]
        private object nodeDesigner;

        [SerializeField]
        private float2 offset;

        [SerializeField]
        private string friendlyName = string.Empty;

        [SerializeField]
        private string comment = string.Empty;

        [SerializeField]
        private bool isBreakpoint;

        [SerializeField]
        private bool collapsed;

        [SerializeField]
        private int colorIndex;

        [SerializeField]
        private List<string> watchedFieldNames;

        [SerializeField]
        private Texture icon;

        public Texture Icon
        {
            get => this.icon;
            set => this.icon = value;
        }
    }
}
