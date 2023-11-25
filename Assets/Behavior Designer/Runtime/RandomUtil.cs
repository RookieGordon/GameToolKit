using System;

namespace BehaviorDesigner.Runtime
{
    public static class RandomUtil
    {
        /// <summary>
        /// [minValue, maxValue]
        /// </summary>
        public static double RandomRange(this Random random, double minValue, double maxValue)
        {
            double result = minValue + random.NextDouble() * (maxValue - minValue);
            return result > maxValue ? maxValue : result;
        }

        public static double RandomRange(this Random ran, double minValue, double maxValue, int decimalPlace)
        {
            double randNum = ran.NextDouble() * (maxValue - minValue) + minValue;
            return Convert.ToDouble(randNum.ToString("f" + decimalPlace));
        }
    }
}