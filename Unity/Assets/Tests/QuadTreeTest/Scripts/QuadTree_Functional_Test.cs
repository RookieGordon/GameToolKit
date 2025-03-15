using System.Collections.Generic;
using UnityEngine;
using ToolKit.DataStructure;
using ToolKit.Tools;
using Unity.Mathematics;
using UnityEditor;

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

    private Dictionary<int, MapNode> _nodeMap = new Dictionary<int, MapNode>();

    private int _unitIdGenerator = 0;

    private Transform _cubeParent;

    private void Awake()
    {
        _shapeGUI = new GameObject("ShapeGUI").AddComponent<ShapesGUI>();
        _mapBox = new AABBBox(new float2(), new float2(20, 20), false);
        _quadTree = new QuadTree<MapUnit>(_mapBox);
        _cubeParent = new GameObject("MapCubeRoot").transform;
    }

    private void OnGUI()
    {
        GUIStyle fontStyle = new GUIStyle("Button");
        fontStyle.fontSize = 40;
        if (GUI.Button(new Rect(0, 210, 300, 200), "Add Unit", fontStyle))
        {
            var obj = GameObject.Instantiate(MapCubeObj, _cubeParent);
            _unitIdGenerator++;
            obj.name = "Unit_" + _unitIdGenerator;
            obj.transform.position = new Vector3(_random.NextFloat(-9, 9), 0, _random.NextFloat(-9, 9));
            var mapUnit = obj.GetComponent<MapUnit>();
            mapUnit.UpdateBox();
            _quadTree.Add(mapUnit);
            _Refresh();
        }

        if (GUI.Button(new Rect(0, 420, 300, 200), "Remove Unit", fontStyle))
        {
            var obj = _cubeParent.GetChild(_cubeParent.childCount - 1).gameObject;
            _quadTree.Remove(obj.GetComponent<MapUnit>());
            GameObject.DestroyImmediate(obj);
            _Refresh();
        }

        if (GUI.Button(new Rect(0, 630, 300, 200), "Query", fontStyle))
        {
            var obj = Selection.activeObject;
            if (obj == null)
            {
                return;
            }

            var mapUnit = (obj as GameObject)?.GetComponent<MapUnit>();
            var l = _quadTree.Query(mapUnit.Box * 3);
            var s = string.Empty;
            foreach (var unit in l)
            {
                s += $"{unit.name}->";
            }

            Log.Error($"与{mapUnit.name}相邻的Unit有：{s}");
        }
        
        if (GUI.Button(new Rect(0, 840, 300, 200), "Find", fontStyle))
        {
            var obj = Selection.activeObject;
            if (obj == null)
            {
                return;
            }

            var mapUnit = (obj as GameObject)?.GetComponent<MapUnit>();
            var n = _quadTree.Find(mapUnit);
            if (n == null)
            {
                Log.Error("MapUnit不存在于四叉树中！");
            }
            else
            {
                Log.Error($"{mapUnit.name}所在节点的层：{n.Depth}, 节点Id：{n.Id}");
            }
        }
        
        if (GUI.Button(new Rect(0, 1050, 300, 200), "Update", fontStyle))
        {
            var obj = Selection.activeObject;
            if (obj == null)
            {
                return;
            }

            var mapUnit = (obj as GameObject)?.GetComponent<MapUnit>();
            _quadTree.UpdateTree(mapUnit);
            _Refresh();
        }
        
        if (GUI.Button(new Rect(0, 1260, 300, 200), "Find Intersections", fontStyle))
        {
            foreach (var kvpair in _quadTree.FindAllIntersections())
            {
                Log.Error($"{kvpair.Key.name}和{kvpair.Value.name}相交！");
            }
        }
    }

    private void _Refresh()
    {
        _shapeGUI.ClearShapes();
        var l = _quadTree.LevelOrderTravel();
        for (int i = 0; i < l.Count; i++)
        {
            var node = l[i] as QuadTree<MapUnit>.TreeNode;
            if (!_nodeMap.TryGetValue(node.Id, out var mapNode))
            {
                mapNode = new MapNode(new Vector2[]
                {
                    node.NodeBox.Min, node.NodeBox.Max
                });
                _nodeMap.Add(node.Id, mapNode);
            }

            _shapeGUI.AddShape(mapNode, _colors[node.Depth]);

            foreach (var unit in node.Values)
            {
                _shapeGUI.AddShape(unit, _colors[node.Depth]);
            }
        }
    }
}