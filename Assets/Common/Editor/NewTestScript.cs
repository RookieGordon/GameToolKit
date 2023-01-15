using System.Collections;
using System.Collections.Generic;
using GameToolKit.Common;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
 
namespace Tests
{
    public class EditModeTests
    {
        private PackageTest mMyClass;
 
        [SetUp]
        public void SetUp()
        {
            mMyClass = new PackageTest();
        }
 
        // A Test behaves as an ordinary method
        [Test]
        public void EditModeTestsSimplePasses()
        {
            // Use the Assert class to test conditions
 
            int result = mMyClass.Add(1, 3);
            Debug.Log("Add(1,3) = " + result) ;
            Assert.AreEqual(result, 4);
        }
 
        // A UnityTest behaves like a coroutine in Play Mode. In Edit Mode you can use
        // `yield return null;` to skip a frame.
        [UnityTest]
        public IEnumerator EditModeTestsWithEnumeratorPasses()
        {
            // Use the Assert class to test conditions.
            // Use yield to skip a frame.
            yield return null;
 
            yield return mMyClass.Sub(3,1);
 
            Debug.Log("Sub(3,1) = " + mMyClass.SubResult);
 
            Assert.AreEqual(mMyClass.SubResult, 2);
        }
    }
 
    public class EditModeTests2
    {
        
 
        // A Test behaves as an ordinary method
        [Test]
        public void EditModeTestsSimplePasses()
        {
            // Use the Assert class to test conditions
            Assert.IsFalse(true);
        }
 
        // A UnityTest behaves like a coroutine in Play Mode. In Edit Mode you can use
        // `yield return null;` to skip a frame.
        [UnityTest]
        public IEnumerator EditModeTestsWithEnumeratorPasses()
        {
            // Use the Assert class to test conditions.
            // Use yield to skip a frame.
            yield return null;
 
            Assert.IsEmpty(null);
        }
    }
}