using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityToolKit.Runtime.Resource;
using Object = UnityEngine.Object;

namespace Tests.ResourceTest
{
    public sealed class UResourceManagerPlayModeTests
    {
        private const string TextAddress = "TestText";
        private const string MissingAddress = "MissingText";
        private const string GameObjectAddress = "TestGameObject";
        private const string SpriteAddress = "TestSprite";
        private const string TextureAddress = "TestTexture";

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
         * 8、GResourceManager加载GameObject实例，释放后进入对象池并可复用
         * 9、业务方直接销毁GameObject实例后，释放凭证不会回收到对象池
         * 10、Sprite/Texture可以被多个目标同时应用，并能独立回滚
         * 11、Sprite/Texture目标被意外销毁后，回滚不会抛出异常
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

        [UnityTest]
        // 测试点：GResourceManager 加载 GameObject 后，释放凭证会回收到对象池，再次加载复用同一实例。
        public IEnumerator GResourceManager_LoadResourceGameObjAsync_RecyclesAndReusesInstance()
        {
            ResourceRef firstRef = null;
            yield return LoadGameObjectRef(resourceRef => firstRef = resourceRef);

            var firstInstance = firstRef.GetGameObject();
            Assert.IsNotNull(firstInstance);
            Assert.IsTrue(firstInstance.activeSelf);
            Assert.IsTrue(firstRef.IsValid);

            firstRef.Dispose();
            yield return null;

            Assert.IsFalse(firstInstance.activeSelf);
            Assert.IsNotNull(firstInstance.transform.parent);
            Assert.AreEqual("[ResInstancePoolRoot]", firstInstance.transform.parent.name);

            ResourceRef secondRef = null;
            yield return LoadGameObjectRef(resourceRef => secondRef = resourceRef);

            var secondInstance = secondRef.GetGameObject();
            Assert.IsNotNull(secondInstance);
            Assert.AreSame(firstInstance, secondInstance);
            Assert.IsTrue(secondInstance.activeSelf);

            secondRef.Dispose();
        }

        [UnityTest]
        // 测试点：业务方直接 Destroy GameObject 实例后，凭证会自动释放，且不会把已销毁对象放回池中。
        public IEnumerator GResourceManager_LoadResourceGameObjAsync_DestroyedByConsumer_DoesNotRecycleDeadInstance()
        {
            ResourceRef firstRef = null;
            yield return LoadGameObjectRef(resourceRef => firstRef = resourceRef);

            var firstInstance = firstRef.GetGameObject();
            Assert.IsNotNull(firstInstance);

            Object.Destroy(firstInstance);
            yield return WaitUntilOrFail(() => firstRef.Token == 0);

            Assert.AreEqual(0, firstRef.Token);

            ResourceRef secondRef = null;
            yield return LoadGameObjectRef(resourceRef => secondRef = resourceRef);

            var secondInstance = secondRef.GetGameObject();
            Assert.IsNotNull(secondInstance);
            Assert.AreNotSame(firstInstance, secondInstance);

            secondRef.Dispose();
        }

        [UnityTest]
        // 测试点：ImageSpriteApplicator 可以把同一个 Sprite 应用到多个 Image，单个目标回滚不影响其它目标，目标被销毁后回滚不抛异常。
        public IEnumerator ImageSpriteApplicator_MultipleTargetsShareAndRevertIndependently()
        {
            var applicable = new ImageSpriteApplicator();
            var imageA = CreateImageTarget("SpriteApplyTargetA");
            var imageB = CreateImageTarget("SpriteApplyTargetB");
            var sprite = Resources.Load<Sprite>(SpriteAddress);

            Assert.IsNotNull(sprite);

            applicable.Apply<Image, Sprite>(imageA, sprite, false);
            applicable.Apply<Image, Sprite>(imageB, sprite, false);

            Assert.AreSame(sprite, imageA.sprite);
            Assert.AreSame(sprite, imageB.sprite);

            applicable.Revert<Image>(imageA);
            Assert.IsNull(imageA.sprite);
            Assert.AreSame(sprite, imageB.sprite);

            Object.DestroyImmediate(imageB.gameObject);
            Assert.DoesNotThrow(() => applicable.Revert<Image>(imageB));

            yield return null;
        }

        [UnityTest]
        // 测试点：同一个 Texture 资源可以应用到多个 RawImage，目标组件意外销毁后会自动回滚。
        public IEnumerator GResourceManager_ApplyResourceTexture_MultipleTargetsShareAndDestroyedTargetCanRevert()
        {
            var applicable = new RawImageTextureApplicator();
            var imageA = CreateRawImageTarget("TextureApplyTargetA");
            var imageB = CreateRawImageTarget("TextureApplyTargetB");
            var completedA = false;
            var completedB = false;

            GResourceManager.Instance.ApplyResourceAsync<RawImage, Texture>(
                imageA,
                TextureAddress,
                applicable,
                () => completedA = true,
                default,
                false);

            GResourceManager.Instance.ApplyResourceAsync<RawImage, Texture>(
                imageB,
                TextureAddress,
                applicable,
                () => completedB = true,
                default,
                false);

            yield return WaitUntilOrFail(() => completedA && completedB);

            Assert.IsNotNull(imageA.texture);
            Assert.IsNotNull(imageB.texture);
            Assert.AreSame(imageA.texture, imageB.texture);

            Object.Destroy(imageA);
            yield return WaitUntilOrFail(() => imageA == null);
            yield return null;

            Assert.IsNotNull(imageB.texture);
            GResourceManager.Instance.RevertAsset<RawImage>(imageB, applicable);
            Assert.IsNull(imageB.texture);
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

                if (!go.scene.IsValid())
                {
                    continue;
                }

                if (go.name == "SingletonRoot" ||
                    go.name == "GResourceManager" ||
                    go.name == "[ResInstancePoolRoot]" ||
                    go.name == "TestPoolRoot" ||
                    go.name.StartsWith("ResourceProviderPrefab", StringComparison.Ordinal) ||
                    go.name.StartsWith("TestGameObject", StringComparison.Ordinal) ||
                    go.name.StartsWith("SpriteApplyTarget", StringComparison.Ordinal) ||
                    go.name.StartsWith("TextureApplyTarget", StringComparison.Ordinal))
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        private static IEnumerator LoadGameObjectRef(Action<ResourceRef> onLoaded)
        {
            ResourceRef loadedRef = null;
            LoadError? error = null;
            var completed = false;

            GResourceManager.Instance.LoadResourceGameObjAsync(
                GameObjectAddress,
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
            onLoaded?.Invoke(loadedRef);
        }

        private static Image CreateImageTarget(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            return go.AddComponent<Image>();
        }

        private static RawImage CreateRawImageTarget(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            return go.AddComponent<RawImage>();
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
