// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.Tasks.UnknownTask
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections.Generic;

namespace BehaviorDesigner.Runtime.Tasks
{
    public partial class UnknownTask : Task
    {
#if !UNITY_PLATFORM
        public string JSONSerialization;

        public List<int> fieldNameHash = new List<int>();

        public List<int> startIndex = new List<int>();

        public List<int> dataPosition = new List<int>();

        public List<Object> unityObjects = new List<Object>();

        public List<byte> byteData = new List<byte>();
#endif
    }
}