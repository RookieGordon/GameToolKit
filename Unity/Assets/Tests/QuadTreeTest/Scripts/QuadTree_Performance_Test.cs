using System.Collections;
using System.Collections.Generic;
using ToolKit.DataStructure;
using Unity.Mathematics;
using UnityEngine;

namespace Test
{
    public class QuadTree_Performance_Test : MonoBehaviour
    {
        private AABBBox _mapBox;
        private QuadTree<MapUnit> _quadTree;
        private int _unitIdGenerator = 0;
        private List<GameObject> _unitList = new List<GameObject>();
        private List<MapUnit> _movedUnitList = new List<MapUnit>();

        public GameObject MapCapsule;
        private Transform _capsuleParent;

        private void Awake()
        {
            _mapBox = new AABBBox(new float2(), new float2(100, 100), false);
            _quadTree = new QuadTree<MapUnit>(_mapBox);
            _capsuleParent = new GameObject("CapsuleParent").transform;
        }


        private void OnGUI()
        {
            GUIStyle fontStyle = new GUIStyle("Button");
            fontStyle.fontSize = 40;
            if (GUI.Button(new Rect(0, 210, 300, 200), "Start", fontStyle))
            {
                for (var i = 0; i < 20; i++)
                {
                    _CreateUnit();
                }
            }
        }

        private void _CreateUnit()
        {
            var obj = GameObject.Instantiate(MapCapsule, _capsuleParent);
            _unitIdGenerator++;
            obj.name = "Unit_" + _unitIdGenerator;
            _unitList.Add(obj);
            var min = _mapBox.Min + new float2(1);
            var max = _mapBox.Max - new float2(1);
            obj.transform.position = new Vector3(UnityEngine.Random.Range(min.x, max.x), 0,
                UnityEngine.Random.Range(min.y, max.y));

            obj.GetComponent<RandomMove>().InitMap(min, max);

            var mapUnit = obj.GetComponent<MapUnit>();
            mapUnit.UpdateBox();

            _quadTree.Add(mapUnit);
        }

        void Update()
        {
            // _movedUnitList.Clear();
            foreach (var go in _unitList)
            {
                var randomMove = go.GetComponent<RandomMove>();
                randomMove.UpdateMove();
                var mapUnit = go.GetComponent<MapUnit>();
                if (randomMove.Moved)
                {
                    // _movedUnitList.Add(mapUnit);
                }
                mapUnit.UpdateBox();
            }

            _quadTree.UpdateTree(_movedUnitList);
        }
    }
}