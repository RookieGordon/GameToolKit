using System;

namespace BehaviorDesigner.Runtime
{
    public static class TimeUtil
    {
        public static DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            
        public static long ConvertDateToTimestamp(this DateTime dateTime)
        {
            TimeSpan timeSpan = dateTime - UnixEpoch;
            return (long)timeSpan.TotalSeconds;
        }
    }
}