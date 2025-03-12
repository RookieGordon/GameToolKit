using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using ToolKit.DataStructure;
using Unity.Mathematics;
using Debug = System.Diagnostics.Debug;

public class QuadTreeTest : MonoBehaviour
{
    private AABBBox _mapBox;
    private QuadTree<MapUnit> _quadTree;
    private ShapesGUI _shapeGUI;
    private Unity.Mathematics.Random _random = new Unity.Mathematics.Random(1337);

    private Color[] _colors = new Color[8]
    {
        Color.green, Color.black, Color.blue, Color.cyan, Color.magenta, Color.red, Color.white, Color.yellow
    };

    public GameObject MapCubeObj;

    private Dictionary<QuadTree<MapUnit>.TreeNode, MapNode> _nodeMap = new Dictionary<QuadTree<MapUnit>.TreeNode, MapNode>();

    private int _unitIdGenerator = 0;

    private void Awake()
    {
        _shapeGUI = new GameObject().AddComponent<ShapesGUI>();
        _mapBox = new AABBBox(new float2(), new float2(20, 20), false);
        _quadTree = new QuadTree<MapUnit>(_mapBox);
    }

    private void OnGUI()
    {
        GUIStyle fontStyle = new GUIStyle("Button");
        fontStyle.fontSize = 40;
        if (GUI.Button(new Rect(0, 0, 300, 200), "Refresh", fontStyle))
        {
            _Refresh();
        }

        if (GUI.Button(new Rect(0, 210, 300, 200), "Add Unit", fontStyle))
        {
            var obj = GameObject.Instantiate(MapCubeObj);
            _unitIdGenerator++;
            obj.name = "Unit_" + _unitIdGenerator;
            obj.transform.position = new Vector3(_random.NextFloat(-10, 10), 0, _random.NextFloat(-10, 10));
            obj.GetComponent<MapUnit>().UpdateBox();
            _quadTree.Add(obj.GetComponent<MapUnit>());
        }
        
        // if (GUI.Button(new Rect(0, 210, 300, 200), "Remove Unit", fontStyle))
        // {
        // }
    }

    private void _Refresh()
    {
        var l = _quadTree.SequenceTraversal();
        for (int i = 0; i < l.Count; i++)
        {
            var node = l[i];
            if (!_nodeMap.TryGetValue(node, out var mapNode))
            {
                mapNode = new MapNode(new Vector2[]
                {
                    node.NodeBox.Min, node.NodeBox.Max
                });
                _shapeGUI.AddShape(mapNode, _colors[node.Depth]);
                _nodeMap.Add(node, mapNode);
            }
            foreach (var unit in node.Values) 
            {
                _shapeGUI.AddShape(unit, _colors[node.Depth]);
            }
        }
    }
}