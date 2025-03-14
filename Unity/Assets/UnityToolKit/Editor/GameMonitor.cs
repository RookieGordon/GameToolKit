using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ToolKit.Editor
{
    public interface IMonitorIndicators
    {
        public Vector2 ShowSize { get; }
        void ShowIndicators(Rect rect);
        void UpdateIndicators();
    }

    public class FPS : IMonitorIndicators
    {
        private int _fps;
        private float _fpsUpdateInterval = 1.0f;
        private float _totalFPS;
        private int _updateCount;
        private float _lastSimpleFPSTime;

        public Vector2 ShowSize => new Vector2(300, 80);

        public void ShowIndicators(Rect rect)
        {
            GUIStyle fontStyle = new GUIStyle();
            fontStyle.fontSize = 50;
            fontStyle.normal.textColor = Color.red;
            GUI.Label(rect, "FPS: " + _fps, fontStyle);
        }

        public void UpdateIndicators()
        {
            _totalFPS += 1.0f / Time.deltaTime;
            _updateCount++;
            if (Time.realtimeSinceStartup - _lastSimpleFPSTime > _fpsUpdateInterval)
            {
                _fps = (int)_totalFPS / _updateCount;
                _totalFPS = 0;
                _updateCount = 0;
                _lastSimpleFPSTime = Time.realtimeSinceStartup;
            }
        }
    }

    public class GameMonitor : MonoBehaviour
    {
        private List<IMonitorIndicators> _indicators = new List<IMonitorIndicators>();
        private Vector2 _startPos = new Vector2(30, 30);
        private float _totoalSizeY = 0;
        private float _verticalOffset = 10;

        private void Awake()
        {
            _indicators.Add(new FPS());
        }
        
        private void OnGUI()
        {
            _totoalSizeY = 0;
            for (int i = 0; i < _indicators.Count; i++)
            {
                var id = _indicators[i];
                id.ShowIndicators(new Rect(_startPos.x, _startPos.y + _totoalSizeY + _verticalOffset * i,
                    id.ShowSize.x, id.ShowSize.y));
                _totoalSizeY += id.ShowSize.y;
            }
        }

        void Update()
        {
            foreach (var val in _indicators)
            {
                val.UpdateIndicators();
            }
        }
    }
}