using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityToolKit.Runtime.Utility
{
    public class TaskToCoroutineUtil
    {
        public static IEnumerator WaitForTask(Task task, float timeoutSeconds = 5f, Action onTimeout = null)
        {
            yield return WaitUntilOrFail(() => task.IsCompleted, timeoutSeconds, onTimeout);
        }

        private static IEnumerator WaitUntilOrFail(Func<bool> predicate, float timeoutSeconds = 5f, Action onTimeout = null)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    onTimeout?.Invoke();
                    yield break;
                }

                yield return null;
            }
        }
    }
}