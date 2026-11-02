using System;

namespace ToolKit.Tools.Common
{
    public enum OperationStatus { Succeeded, Failed, Canceled }

    /// <summary>回调形式（Subscribe）的结果信封；await 形式直接用 Task 的成功/异常/取消。</summary>
    public sealed class OperationResult<TResult>
    {
        private readonly TResult _value;

        private OperationResult(OperationStatus status, TResult value, Exception error)
        {
            Status = status;
            _value = value;
            Error = error;
        }

        public OperationStatus Status { get; }
        public Exception Error { get; }
        public bool IsSuccess { get { return Status == OperationStatus.Succeeded; } }

        public TResult Value
        {
            get
            {
                if (!IsSuccess) throw new InvalidOperationException("只有成功结果可以读取 Value。", Error);
                return _value;
            }
        }

        internal static OperationResult<TResult> Success(TResult value)
        {
            return new OperationResult<TResult>(OperationStatus.Succeeded, value, null);
        }

        internal static OperationResult<TResult> Failure(Exception error)
        {
            return new OperationResult<TResult>(OperationStatus.Failed, default(TResult), error);
        }

        internal static OperationResult<TResult> Canceled(OperationCanceledException error)
        {
            return new OperationResult<TResult>(OperationStatus.Canceled, default(TResult), error);
        }
    }
}
