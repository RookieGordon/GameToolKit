using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ToolKit.DataStructure;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine.UI;

namespace Tests.QuadTreeTest
{
    class TestElement : MonoBehaviour, IBoundable
    {
        private void Update()
        {
            
        }

        public AABBBox GetBoundaryBox()
        {
            var pos = transform.position;
            // initByDiagonal: false → 以(pos.x, pos.z)为中心，(1,1)为尺寸
            return new AABBBox(new float2(pos.x, pos.z), new float2(1, 1), false);
        }
    }
    
    public class QuadTreeDebugger : MonoBehaviour
    {
        public Button AddNodeBtn;
        public Button UpdateNodeBtn;

        private QuadTree<TestElement> _quadTree;
        private TestElement _element;

        private void Awake()
        {
            AddNodeBtn.onClick.AddListener(OnClickAddNode);
            UpdateNodeBtn.onClick.AddListener(OnClickUpdateNode);
            
            _quadTree = new QuadTree<TestElement>(new AABBBox(0, 0, 100, 100), 4, 4);
            gameObject.GetComponent<QuadTreeGizmosDebugTarget>().SetTarget(_quadTree);
        }

        private void Update()
        {
            if (Selection.gameObjects.Length >= 1)
            {
                var obj = Selection.gameObjects[0];
                if (obj.TryGetComponent<TestElement>(out var element))
                {
                    _element = element;
                }
            }
        }

        void OnClickAddNode()
        {
            // var element = new GameObject("Element");
            // element.AddComponent<Gizmos>()
            var element = new GameObject("Element").AddComponent<TestElement>();
            element.gameObject.transform.position = new Vector3(UnityEngine.Random.Range(0, 100), 0, UnityEngine.Random.Range(0, 100));
            _quadTree.Add(element);
        }

        void OnClickUpdateNode()
        {
            if (_element != null)
            {
                _quadTree.UpdateValue(_element);
                _element = null;
            }
        }
    }
}