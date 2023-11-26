// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.VariableSerializationData
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections.Generic;
#if !UNITY_PLATFORM
using SerializeField = Newtonsoft.Json.JsonPropertyAttribute;
#else
using UnityEngine;
#endif

namespace BehaviorDesigner.Runtime
{
    [Serializable]
    public class VariableSerializationData
    {
        [SerializeField]
        public List<int> variableStartIndex = new List<int>();

        [SerializeField]
        public string JSONSerialization = string.Empty;

        [SerializeField]
        public FieldSerializationData fieldSerializationData = new FieldSerializationData();
    }
}
