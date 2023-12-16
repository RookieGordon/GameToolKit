using System;
using System.Diagnostics;

namespace BehaviorDesigner.Runtime
{
    public static class TimeUtils
    {
        public static float deltaTime = 1 / 60;

        public static Stopwatch watch = new Stopwatch();

        static TimeUtils()
        {
            watch.Start();
        }

        public static double realtimeSinceStartup
        {
            get
            {
                return (double)watch.ElapsedMilliseconds / (double)1000;
            }
        }
    }
}