/*
 * 功能描述：Bounds增量计算工具
 */

using UnityEngine;

namespace UnityToolKit.Runtime.Utility
{
    public static class BoundsIncrement
    {
        private static Vector3 _min;
        private static Vector3 _max;
        private static bool _started;

        public static void Begin()
        {
            _min = Vector3.positiveInfinity;
            _max = Vector3.negativeInfinity;
            _started = true;
        }

        public static void Iterate(Vector3 point)
        {
            if (!_started)
            {
                Debug.LogError("BoundsIncrement.Begin() must be called before Iterate()");
                return;
            }

            _min = Vector3.Min(_min, point);
            _max = Vector3.Max(_max, point);
        }

        public static Bounds End()
        {
            _started = false;
            var center = (_min + _max) * 0.5f;
            var size = _max - _min;
            return new Bounds(center, size);
        }
    }
}
