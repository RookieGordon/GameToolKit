using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

public enum ShapeType
{
    Point,
    Line,
    Triangle,
    Rectangle,
    Circle,
}

public interface IShape
{
    public bool NeedFill { get; }
    public ShapeType ShapeType { get; }
    public Vector2[] Vertices { get; }
}

public class ShapesGUI : MonoBehaviour
{
    private static int _idGenerator = 0;

    public struct ShapeInfo
    {
        public int Id;
        public IShape Shape;
        public Color ShapeColor;
    }

    private List<ShapeInfo> _shapeList = new List<ShapeInfo>();
    private Dictionary<IShape, int> _shapeMap = new Dictionary<IShape, int>();

    public int AddShape(IShape shape, Color shapeColor)
    {
        if (_shapeMap.TryGetValue(shape, out var id))
        {
            return id;
        }

        id = _idGenerator++;
        _shapeList.Add(new ShapeInfo()
        {
            Id = id,
            Shape = shape,
            ShapeColor = shapeColor,
        });
        _shapeMap[shape] = id;
        var render = gameObject.GetComponent<MeshRenderer>();
        if (render != null)
        {
            render.materials[0].color = shapeColor;
        }

        return id;
    }

    public void RemoveShape(int id)
    {
        for (int i = _shapeList.Count - 1; i >= 0; i--)
        {
            if (_shapeList[i].Id == id)
            {
                _shapeMap.Remove(_shapeList[i].Shape);
                _shapeList.RemoveAt(i);
                return;
            }
        }
    }
    
    public void RemoveShape(IShape shape)
    {
        for (int i = _shapeList.Count - 1; i >= 0; i--)
        {
            if (_shapeList[i].Shape == shape)
            {
                _shapeMap.Remove(shape);
                _shapeList.RemoveAt(i);
                return;
            }
        }
    }
    
    public void ClearShapes()
    {
        _shapeList.Clear();
        _shapeMap.Clear();
    }

    private void OnDrawGizmos()
    {
        if (_shapeList.Count == 0)
        {
            return;
        }

        foreach (var info in _shapeList)
        {
            var shapeType = info.Shape.ShapeType;
            switch (shapeType)
            {
                case ShapeType.Circle:
                    DrawCircle(info.Shape.Vertices[0], math.length(info.Shape.Vertices[1] - info.Shape.Vertices[0]),
                        info.ShapeColor, info.Shape.NeedFill);
                    break;
                case ShapeType.Triangle:
                    DrawTriangle(info.Shape.Vertices[0], info.Shape.Vertices[1], info.Shape.Vertices[2],
                        info.ShapeColor, info.Shape.NeedFill);
                    break;
                case ShapeType.Rectangle:
                    DrawRectangle(info.Shape.Vertices[0], info.Shape.Vertices[1], info.ShapeColor, info.Shape.NeedFill);
                    break;
            }
        }
    }

    private void DrawCircle(Vector2 center, float radius, Color color, bool needFill = false)
    {
        Handles.color = color;
        if (needFill)
        {
            Handles.DrawSolidArc(new Vector3(center.x, 0, center.y), Vector3.up, Vector3.forward, 360, radius);
        }
        else
        {
            Handles.DrawWireArc(new Vector3(center.x, 0, center.y), Vector3.up, Vector3.forward, 360, radius);
        }
    }

    private void DrawTriangle(Vector2 a, Vector2 b, Vector2 c, Color color, bool needFill = false)
    {
        Handles.color = color;
        if (needFill)
        {
            Handles.DrawAAConvexPolygon(new Vector3[]
            {
                new Vector3(a.x, 0, a.y),
                new Vector3(b.x, 0, b.y),
                new Vector3(c.x, 0, c.y),
                new Vector3(a.x, 0, a.y),
            });
        }
        else
        {
            Handles.DrawPolyLine(new Vector3[]
            {
                new Vector3(a.x, 0, a.y),
                new Vector3(b.x, 0, b.y),
                new Vector3(c.x, 0, c.y),
                new Vector3(a.x, 0, a.y),
            });
        }
    }

    private void DrawRectangle(Vector2 min, Vector2 max, Color color, bool needFill = false)
    {
        Handles.color = color;
        if (needFill)
        {
            Handles.DrawAAConvexPolygon(new Vector3[]
            {
                new Vector3(min.x, 0, min.y),
                new Vector3(min.x, 0, max.y),
                new Vector3(max.x, 0, max.y),
                new Vector3(max.x, 0, min.y),
                new Vector3(min.x, 0, min.y),
            });
        }
        else
        {
            Handles.DrawPolyLine(new Vector3[]
            {
                new Vector3(min.x, 0, min.y),
                new Vector3(min.x, 0, max.y),
                new Vector3(max.x, 0, max.y),
                new Vector3(max.x, 0, min.y),
                new Vector3(min.x, 0, min.y),
            });
        }
        
    }
}