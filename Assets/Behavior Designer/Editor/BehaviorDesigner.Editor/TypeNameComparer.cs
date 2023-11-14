// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.TypeNameComparer
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime.Tasks;
using System;
using System.Collections.Generic;

namespace BehaviorDesigner.Editor
{
  public class TypeNameComparer : IComparer<Type>
  {
    public int Compare(Type x, Type y)
    {
      string name1 = x.Name;
      string name2 = y.Name;
      int length1 = name1.Length;
      int length2 = name2.Length;
      int index1 = 0;
      for (int index2 = 0; index1 < length1 && index2 < length2; ++index2)
      {
        int num;
        if (char.IsDigit(name1[index1]) && char.IsDigit(name1[index2]))
        {
          string empty1 = string.Empty;
          for (; index1 < length1 && char.IsDigit(name1[index1]); ++index1)
            empty1 += (string) (object) name1[index1];
          string empty2 = string.Empty;
          for (; index2 < length2 && char.IsDigit(name2[index2]); ++index2)
            empty2 += (string) (object) name2[index2];
          int result1 = 0;
          int.TryParse(empty1, out result1);
          int result2 = 0;
          int.TryParse(empty2, out result2);
          num = result1.CompareTo(result2);
        }
        else
          num = name1[index1].CompareTo(name2[index2]);
        if (num != 0)
          return num;
        ++index1;
      }
      return length1 - length2;
    }

    private string TypePrefix(Type t)
    {
      if (t.IsSubclassOf(typeof (Runtime.Tasks.Action)))
        return "Action";
      if (t.IsSubclassOf(typeof (Composite)))
        return "Composite";
      return t.IsSubclassOf(typeof (Conditional)) ? "Conditional" : "Decorator";
    }
  }
}
