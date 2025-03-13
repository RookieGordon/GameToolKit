using System;
using ToolKit.Tools;
using UnityEngine;

public class UnityDebugger: MonoBehaviour
{
    private void Awake()
    {
        Log.SetLog(new UnityLogger());
    }
}