// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.FieldSerializationData
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections.Generic;
using SerializeField = Newtonsoft.Json.JsonPropertyAttribute;

namespace BehaviorDesigner.Runtime
{
    [Serializable]
    public partial class FieldSerializationData
    {
      
#if !UNITY_PLATFORM
        [SerializeField]
        public List<string> typeName = new List<string>();

        [SerializeField]
        public List<int> fieldNameHash = new List<int>();

        [SerializeField]
        public List<int> startIndex = new List<int>();

        [SerializeField]
        public List<int> dataPosition = new List<int>();

        [SerializeField]
        public List<System.Object> unityObjects = new List<System.Object>();

        [SerializeField]
        public List<byte> byteData = new List<byte>();
#endif

        public byte[] byteDataArray;
    }
}
