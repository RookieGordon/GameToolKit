// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.EditorZoomArea
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using UnityEngine;

namespace BehaviorDesigner.Editor
{
  public class EditorZoomArea
  {
    private static Matrix4x4 _prevGuiMatrix;
    private static Rect groupRect = new Rect();

    public static Rect Begin(Rect screenCoordsArea, float zoomScale)
    {
      GUI.EndGroup();
      Rect rect = screenCoordsArea.ScaleSizeBy(1f / zoomScale, screenCoordsArea.TopLeft());
      rect.y += 21f;
      GUI.BeginGroup(rect);
      EditorZoomArea._prevGuiMatrix = GUI.matrix;
      Matrix4x4 matrix4x4_1 = Matrix4x4.TRS((Vector3) rect.TopLeft(), Quaternion.identity, Vector3.one);
      Vector3 one = Vector3.one;
      one.x = one.y = zoomScale;
      Matrix4x4 matrix4x4_2 = Matrix4x4.Scale(one);
      GUI.matrix = matrix4x4_1 * matrix4x4_2 * matrix4x4_1.inverse * GUI.matrix;
      return rect;
    }

    public static void End()
    {
      GUI.matrix = EditorZoomArea._prevGuiMatrix;
      GUI.EndGroup();
      EditorZoomArea.groupRect.y = 21f;
      EditorZoomArea.groupRect.width = (float) Screen.width;
      EditorZoomArea.groupRect.height = (float) Screen.height;
      GUI.BeginGroup(EditorZoomArea.groupRect);
    }
  }
}
