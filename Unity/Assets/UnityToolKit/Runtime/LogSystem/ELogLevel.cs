using System;

namespace UnityToolKit.Runtime.LogSystem
{
    [Flags]
    public enum ELogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
        All = Debug | Info | Warning | Error,
    }
}