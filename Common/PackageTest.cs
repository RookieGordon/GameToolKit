using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GameToolKit.Common
{
    public class PackageTest
    {
        public int Add(int a, int b) {
            return (a) + (b);
        }
 
        public int SubResult;
        public IEnumerator Sub(int a,int b) {
            yield return null;
            SubResult = (a) - (b);
        }
    }
}

