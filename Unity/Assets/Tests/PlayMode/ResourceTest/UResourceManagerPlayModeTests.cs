using System;
using System.Collections;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.TestTools;
using UnityToolKit.Runtime.Resource;
using Object = UnityEngine.Object;

namespace Tests.ResourceTest
{
    public sealed class UResourceManagerPlayModeTests
    {
        private const string TextAddress = "GResourceManagerTest/TestText";
        private const string MissingAddress = "GResourceManagerTest/MissingText";

        [SetUp]
        public void SetUp()
        {
            DestroyResourceRuntimeObjects();
        }

        [TearDown]
        public void TearDown()
        {
            DestroyResourceRuntimeObjects();
        }
        
        /* 
         * 测试用例：
         * 1、ResourcesLoader只能加载Resources目录下的资源
         * 2、ResourcesLoader能成功加载资源
         * 3、ResourcesLoader加载不存在的资源，返回NotFound
         * 4、GResourceManager能成功加载资源，并且得到的ResourceRef是有效的
         * 5、GResourceManager加载失败，失败回调正常触发
         * 6、GResourceManager应用资源成功
         * 7、GameObjectInstanceProvider实例化、回收、销毁
         */

        [Test]
        // 测试点：ResourcesLoader 只接受 Unity Resources 目录下的相对路径。
        public void ResourcesLoader_CanLoad_OnlyAcceptsResourcesStyleAddresses()
        {
            var loader = new ResourcesLoader();

            Assert.IsTrue(loader.CanLoad(TextAddress));
            Assert.IsFalse(loader.CanLoad(string.Empty));
            Assert.IsFalse(loader.CanLoad("http://example.com/asset"));
            Assert.IsFalse(loader.CanLoad(Application.dataPath));
        }

        [UnityTest]
        // 测试点：ResourcesLoader 能从 Resources 目录加载真实的 TextAsset。
        public IEnumerator ResourcesLoader_LoadAsync_LoadsTextAsset()
        {
            var loader = new ResourcesLoader();
            var task = loader.LoadAsync(TextAddress);

            yield return WaitForTask(task);

            var handle = task.Result;
            Assert.IsTrue(handle.IsSuccess);
            var text = handle.GetAsset<TextAsset>();
            Assert.IsNotNull(text);
            StringAssert.Contains("GResourceManager test payload", text.text);

            (handle as AssetHandle)?.Unload();
        }

        [UnityTest]
        // 测试点：Resources 地址不存在时返回结构化的 NotFound 失败句柄。
        public IEnumerator ResourcesLoader_LoadAsync_MissingAddress_ReturnsNotFoundHandle()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"\[ResourceSystem\].*NotFound"));

            var loader = new ResourcesLoader();
            var task = loader.LoadAsync(MissingAddress);

            yield return WaitForTask(task);

            var handle = task.Result;
            Assert.IsFalse(handle.IsSuccess);
            Assert.AreEqual(ELoadError.NotFound, handle.Error.Code);
        }

        [UnityTest]
        // 测试点：GResourceManager 协程加载接口成功时返回有效 ResourceRef。
        public IEnumerator GResourceManager_LoadResourceAsync_LoadsResourceRef()
        {
            ResourceRef loadedRef = null;
            LoadError? error = null;
            var completed = false;

            GResourceManager.Instance.LoadResourceAsync(
                TextAddress,
                resourceRef =>
                {
                    loadedRef = resourceRef;
                    completed = true;
                },
                loadError =>
                {
                    error = loadError;
                    completed = true;
                });

            yield return WaitUntilOrFail(() => completed);

            Assert.IsNull(error);
            Assert.IsNotNull(loadedRef);
            Assert.IsTrue(loadedRef.IsValid);
            StringAssert.Contains("GResourceManager test payload", loadedRef.GetTextAsset().text);

            loadedRef.Dispose();
        }

        [UnityTest]
        // 测试点：GResourceManager 协程加载接口失败时触发错误回调。
        public IEnumerator GResourceManager_LoadResourceAsync_MissingAddress_CallsErrorCallback()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"\[ResourceSystem\].*NotFound"));

            ResourceRef loadedRef = null;
            LoadError? error = null;
            var completed = false;

            GResourceManager.Instance.LoadResourceAsync(
                MissingAddress,
                resourceRef =>
                {
                    loadedRef = resourceRef;
                    completed = true;
                },
                loadError =>
                {
                    error = loadError;
                    completed = true;
                });

            yield return WaitUntilOrFail(() => completed);

            Assert.IsNull(loadedRef);
            Assert.IsTrue(error.HasValue);
            Assert.AreEqual(ELoadError.NotFound, error.Value.Code);
        }

        [UnityTest]
        // 测试点：ResourceBinder 集成能应用 Resources 资源，并能回滚绑定状态。
        public IEnumerator GResourceManager_ApplyResourceAsync_AppliesAndRevertsTextAsset()
        {
            var target = ScriptableObject.CreateInstance<ResourceApplyTarget>();
            var applicable = new TextAssetApplicator();
            var completed = false;

            GResourceManager.Instance.ApplyResourceAsync<ResourceApplyTarget, TextAsset>(
                target,
                TextAddress,
                applicable,
                () => completed = true,
                default,
                "playmode");

            yield return WaitUntilOrFail(() => completed);

            Assert.AreEqual(1, applicable.ApplyCount);
            StringAssert.Contains("GResourceManager test payload", target.Content);
            Assert.AreEqual("playmode", target.LastTag);

            GResourceManager.Instance.RevertAsset<ResourceApplyTarget>(target, applicable);

            Assert.AreEqual(1, applicable.RevertCount);
            Assert.IsNull(target.Content);
            Assert.IsNull(target.LastTag);

            Object.DestroyImmediate(target);
        }

        [UnityTest]
        // 测试点：GameObject 实例提供器能激活、入池、再激活并销毁实例。
        public IEnumerator GameObjectInstanceProvider_ReturnsAndDestroysGameObjects()
        {
            var poolRoot = new GameObject("TestPoolRoot").transform;
            poolRoot.gameObject.SetActive(false);
            var prefab = new GameObject("ResourceProviderPrefab");
            var handle = new AssetHandle("ResourceProviderPrefab");
            handle.SetSucceed(prefab, null);
            var provider = new GameObjectInstanceProvider(poolRoot);

            Assert.IsTrue(provider.CanInstantiate(handle));

            var instance = provider.Create(handle) as GameObject;
            Assert.IsNotNull(instance);
            Assert.IsTrue(instance.activeSelf);

            provider.OnReturn(instance);
            Assert.IsFalse(instance.activeSelf);
            Assert.AreSame(poolRoot, instance.transform.parent);

            provider.OnGet(instance);
            Assert.IsTrue(instance.activeSelf);

            provider.OnDestroy(instance);
            yield return null;

            Assert.IsTrue(instance == null);

            Object.DestroyImmediate(prefab);
            Object.DestroyImmediate(poolRoot.gameObject);
        }

        private static IEnumerator WaitForTask(Task task, float timeoutSeconds = 3f)
        {
            yield return WaitUntilOrFail(() => task.IsCompleted, timeoutSeconds);

            if (task.IsFaulted)
            {
                Assert.Fail(task.Exception != null ? task.Exception.ToString() : "Task failed.");
            }

            if (task.IsCanceled)
            {
                Assert.Fail("Task was canceled.");
            }
        }

        private static IEnumerator WaitUntilOrFail(Func<bool> predicate, float timeoutSeconds = 3f)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    Assert.Fail("Timed out waiting for resource test condition.");
                }

                yield return null;
            }
        }

        private static void DestroyResourceRuntimeObjects()
        {
            var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            foreach (var go in allObjects)
            {
                if (go == null)
                {
                    continue;
                }

                if (go.name == "SingletonRoot" ||
                    go.name == "GResourceManager" ||
                    go.name == "[ResInstancePoolRoot]" ||
                    go.name == "TestPoolRoot" ||
                    go.name.StartsWith("ResourceProviderPrefab", StringComparison.Ordinal))
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        private sealed class ResourceApplyTarget : ScriptableObject
        {
            public string Content;
            public string LastTag;
        }

        private sealed class TextAssetApplicator : IApplicable
        {
            public int ApplyCount;
            public int RevertCount;

            public void Apply<T, R>(T target, R resource, params object[] applayArgs)
                where T : class
                where R : class
            {
                if (target is not ResourceApplyTarget applyTarget || resource is not TextAsset textAsset)
                {
                    return;
                }

                ApplyCount++;
                applyTarget.Content = textAsset.text;
                applyTarget.LastTag = applayArgs != null && applayArgs.Length > 0 ? applayArgs[0] as string : null;
            }

            public void Revert<T>(T target) where T : class
            {
                if (target is not ResourceApplyTarget applyTarget)
                {
                    return;
                }

                RevertCount++;
                applyTarget.Content = null;
                applyTarget.LastTag = null;
            }
        }
    }
}
