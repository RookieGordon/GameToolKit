using System;

namespace ToolKit.Tools.Common
{
    /// <summary>一次进度快照。Fraction 为 null 表示总量未知，只有阶段文字。</summary>
    public sealed class OperationProgress
    {
        public OperationProgress(double? fraction, string message = null)
        {
            if (fraction.HasValue && (double.IsNaN(fraction.Value) || double.IsInfinity(fraction.Value)
                || fraction.Value < 0 || fraction.Value > 1))
            {
                throw new ArgumentOutOfRangeException(nameof(fraction), "进度必须为 0 到 1 之间的有限数值，未知总量时使用 null。");
            }

            Fraction = fraction;
            Message = message;
        }

        public double? Fraction { get; }
        public string Message { get; }
    }
}
