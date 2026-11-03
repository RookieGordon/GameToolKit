using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using CoreResourceSystem = ToolKit.Tools.Common.ResourceSystem;
using Object = UnityEngine.Object;

namespace Tests.PlayMode.ResourceSystem
{
    /// <summary>使用帧循环等待任务；失败也清理各测试拥有的系统、对象及临时目录。</summary>
    public abstract class ResourcePlayModeTestBase
    {
        private readonly List<CoreResourceSystem> _systems = new List<CoreResourceSystem>();
        private readonly List<Object> _objects = new List<Object>();
        private readonly List<string> _directories = new List<string>();

        protected CoreResourceSystem Track(CoreResourceSystem system)
        {
            _systems.Add(system);
            return system;
        }

        protected T TrackObject<T>(T value) where T : Object
        {
            _objects.Add(value);
            return value;
        }

        protected string CreateTempDirectory()
        {
            // Editor PlayMode 中固定落在工程的 Temp 内，不使用系统默认临时目录。
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../Temp/ResourceSystemPlayMode", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            _directories.Add(directory);
            return directory;
        }

        protected static IEnumerator Until(Func<bool> condition, float timeoutSeconds = 10)
        {
            var deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            while (!condition())
            {
                Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(deadline),
                    "等待条件超时；测试不得阻塞 Unity 主线程或无限等待。");
                yield return null;
            }
        }

        protected static IEnumerator Finish(Task task, float timeoutSeconds = 10)
        {
            yield return Until(() => task.IsCompleted, timeoutSeconds);
        }

        protected static IEnumerator Await(Task task, float timeoutSeconds = 10)
        {
            yield return Finish(task, timeoutSeconds);
            task.GetAwaiter().GetResult();
        }

        [UnityTearDown]
        public IEnumerator CleanUp()
        {
            var tasks = new List<Task>();
            foreach (var system in _systems) tasks.Add(system.DisposeAsync().AsTask());
            var disposal = Task.WhenAll(tasks);
            try
            {
                yield return Finish(disposal);
                disposal.GetAwaiter().GetResult();
            }
            finally
            {
                foreach (var value in _objects)
                    if (value != null) Object.Destroy(value);
                foreach (var directory in _directories)
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                _systems.Clear();
                _objects.Clear();
                _directories.Clear();
            }
            // Destroy 在帧末生效，不能把活对象留给下一项测试。
            yield return null;
        }
    }
}
