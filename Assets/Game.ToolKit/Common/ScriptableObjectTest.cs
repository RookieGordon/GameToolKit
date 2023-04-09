using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "BehaviourTree", menuName = "Bonsai/ Test")]
public class ScriptableObjectTest : ScriptableObject
{
    public int testint = 0;

    private void OnEnable()
    {
        Debug.LogError("OnEnable");
    }
}
