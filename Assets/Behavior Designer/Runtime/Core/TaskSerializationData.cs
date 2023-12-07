// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.TaskSerializationData
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections.Generic;
using SerializeField = Newtonsoft.Json.JsonPropertyAttribute;

namespace BehaviorDesigner.Runtime
{
    [Serializable]
    public partial class TaskSerializationData
    {
#if !UNITY_PLATFORM
        [SerializeField]
        public List<string> types = new List<string>();

        [SerializeField]
        public List<int> parentIndex = new List<int>();

        [SerializeField]
        public List<int> startIndex = new List<int>();

        [SerializeField]
        public List<int> variableStartIndex = new List<int>();

        [SerializeField]
        public string JSONSerialization;

        [SerializeField]
        public FieldSerializationData fieldSerializationData = new FieldSerializationData();

        [SerializeField]
        public string Version;
#endif
    }
}