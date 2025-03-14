using ToolKit.DataStructure;
using Unity.Mathematics;
using UnityEngine;

public class MapUnit : MonoBehaviour, IBoundable, IShape
{
    public AABBBox Box;
    public string Name;
    public bool NeedFill { get; } = true;
    public ShapeType ShapeType { get; } = ShapeType.Rectangle;

    private Vector2[] _vertices;
    public Vector2[] Vertices => _vertices;

    private Collider _collider;

    public AABBBox GetBoundaryBox()
    {
        return Box;
    }

    private void Awake()
    {
        _collider = gameObject.GetComponent<Collider>();
        var center = _collider.bounds.center;
        var size = _collider.bounds.size;
        Box = new AABBBox(new float2(center.x, center.z),
            new float2(size.x, size.z), false);
        _vertices = new Vector2[2]
        {
            Box.Min, Box.Max
        };
        
        Name = gameObject.name;
    }

    public void UpdateBox()
    {
        Box.UpdatePosition(transform.position.x, transform.position.z);
        _vertices[0] = Box.Min;
        _vertices[1] = Box.Max;
    }

    private void Update()
    {
        UpdateBox();
    }
}

public class MapNode : IShape
{
    public bool NeedFill { get; } = false;
    public ShapeType ShapeType { get; } = ShapeType.Rectangle;

    public Vector2[] Vertices { get; }

    public MapNode(Vector2[] vertices)
    {
        Vertices = vertices;
    }
}