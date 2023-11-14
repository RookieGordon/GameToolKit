// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.NodeConnection
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using System;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
  [Serializable]
  public class NodeConnection : ScriptableObject
  {
    [SerializeField]
    private NodeDesigner originatingNodeDesigner;
    [SerializeField]
    private NodeDesigner destinationNodeDesigner;
    [SerializeField]
    private NodeConnectionType nodeConnectionType;
    [SerializeField]
    private bool selected;
    [SerializeField]
    private float horizontalHeight;
    private readonly Color selectedDisabledProColor = new Color(0.1316f, 0.3212f, 0.4803f);
    private readonly Color selectedDisabledStandardColor = new Color(0.1701f, 0.3982f, 0.5873f);
    private readonly Color selectedEnabledProColor = new Color(0.188f, 0.4588f, 0.6862f);
    private readonly Color selectedEnabledStandardColor = new Color(0.243f, 0.5686f, 0.839f);
    private readonly Color taskRunningProColor = new Color(0.0f, 0.698f, 0.4f);
    private readonly Color taskRunningStandardColor = new Color(0.0f, 1f, 0.2784f);
    private bool horizontalDirty = true;
    private Vector2 startHorizontalBreak;
    private Vector2 endHorizontalBreak;
    private Vector3[] linePoints = new Vector3[4];

    public NodeDesigner OriginatingNodeDesigner
    {
      get => this.originatingNodeDesigner;
      set => this.originatingNodeDesigner = value;
    }

    public NodeDesigner DestinationNodeDesigner
    {
      get => this.destinationNodeDesigner;
      set => this.destinationNodeDesigner = value;
    }

    public NodeConnectionType NodeConnectionType
    {
      get => this.nodeConnectionType;
      set => this.nodeConnectionType = value;
    }

    public void select() => this.selected = true;

    public void deselect() => this.selected = false;

    public float HorizontalHeight
    {
      set
      {
        this.horizontalHeight = value;
        this.horizontalDirty = true;
      }
    }

    public void OnEnable() => this.hideFlags = HideFlags.HideAndDontSave;

    public void LoadConnection(NodeDesigner nodeDesigner, NodeConnectionType nodeConnectionType)
    {
      this.originatingNodeDesigner = nodeDesigner;
      this.nodeConnectionType = nodeConnectionType;
      this.selected = false;
    }

    public void DrawConnection(Vector2 offset, bool disabled) => this.DrawConnection(this.OriginatingNodeDesigner.GetConnectionPosition(offset, NodeConnectionType.Outgoing), this.DestinationNodeDesigner.GetConnectionPosition(offset, NodeConnectionType.Incoming), disabled);

    public void DrawConnection(Vector2 source, Vector2 destination, bool disabled)
    {
      Color color = !disabled ? Color.white : new Color(0.7f, 0.7f, 0.7f);
      bool flag = (UnityEngine.Object) this.destinationNodeDesigner != (UnityEngine.Object) null && this.destinationNodeDesigner.Task != null && (double) this.destinationNodeDesigner.Task.NodeData.PushTime != -1.0 && (double) this.destinationNodeDesigner.Task.NodeData.PushTime >= (double) this.destinationNodeDesigner.Task.NodeData.PopTime;
      float num = !BehaviorDesignerPreferences.GetBool(BDPreferences.FadeNodes) ? 0.01f : 0.5f;
      if (this.selected)
        color = !disabled ? (!EditorGUIUtility.isProSkin ? this.selectedEnabledStandardColor : this.selectedEnabledProColor) : (!EditorGUIUtility.isProSkin ? this.selectedDisabledStandardColor : this.selectedDisabledProColor);
      else if (flag)
        color = !EditorGUIUtility.isProSkin ? this.taskRunningStandardColor : this.taskRunningProColor;
      else if ((double) num != 0.0 && (UnityEngine.Object) this.destinationNodeDesigner != (UnityEngine.Object) null && this.destinationNodeDesigner.Task != null && (double) this.destinationNodeDesigner.Task.NodeData.PopTime != -1.0 && (double) this.destinationNodeDesigner.Task.NodeData.PopTime <= (double) Time.realtimeSinceStartup && (double) Time.realtimeSinceStartup - (double) this.destinationNodeDesigner.Task.NodeData.PopTime < (double) num)
      {
        float t = (float) (1.0 - ((double) Time.realtimeSinceStartup - (double) this.destinationNodeDesigner.Task.NodeData.PopTime) / (double) num);
        Color white = Color.white;
        color = Color.Lerp(Color.white, !EditorGUIUtility.isProSkin ? this.taskRunningStandardColor : this.taskRunningProColor, t);
      }
      Handles.color = color;
      if (this.horizontalDirty)
      {
        this.startHorizontalBreak = new Vector2(source.x, this.horizontalHeight);
        this.endHorizontalBreak = new Vector2(destination.x, this.horizontalHeight);
        this.horizontalDirty = false;
      }
      this.linePoints[0] = this.BoundedVector3((Vector3) source);
      this.linePoints[1] = this.BoundedVector3((Vector3) this.startHorizontalBreak);
      this.linePoints[2] = this.BoundedVector3((Vector3) this.endHorizontalBreak);
      this.linePoints[3] = this.BoundedVector3((Vector3) destination);
      Handles.DrawPolyLine(this.linePoints);
      for (int index = 0; index < this.linePoints.Length; ++index)
      {
        ++this.linePoints[index].x;
        ++this.linePoints[index].y;
      }
      Handles.DrawPolyLine(this.linePoints);
    }

    private Vector3 BoundedVector3(Vector3 vector)
    {
      vector.y = Mathf.Max(0.0f, vector.y);
      return vector;
    }

    public bool Contains(Vector2 point, Vector2 offset)
    {
      Vector2 center = this.originatingNodeDesigner.OutgoingConnectionRect(offset).center;
      Vector2 vector2_1 = new Vector2(center.x, this.horizontalHeight);
      if ((double) Mathf.Abs(point.x - center.x) < 7.0 && ((double) point.y >= (double) center.y && (double) point.y <= (double) vector2_1.y || (double) point.y <= (double) center.y && (double) point.y >= (double) vector2_1.y))
        return true;
      Rect rect = this.destinationNodeDesigner.IncomingConnectionRect(offset);
      Vector2 vector2_2 = new Vector2(rect.center.x, rect.y);
      Vector2 vector2_3 = new Vector2(vector2_2.x, this.horizontalHeight);
      return (double) Mathf.Abs(point.y - this.horizontalHeight) < 7.0 && ((double) point.x <= (double) center.x && (double) point.x >= (double) vector2_3.x || (double) point.x >= (double) center.x && (double) point.x <= (double) vector2_3.x) || (double) Mathf.Abs(point.x - vector2_2.x) < 7.0 && ((double) point.y >= (double) vector2_2.y && (double) point.y <= (double) vector2_3.y || (double) point.y <= (double) vector2_2.y && (double) point.y >= (double) vector2_3.y);
    }
  }
}
