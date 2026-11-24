using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.TestTools;
using UnityToolKit.Runtime.ResourceSystem;
using CoreResourceSystem = ToolKit.Tools.Common.ResourceSystem;

namespace Tests.PlayMode.ResourceSystem
{
    public class UnityResourceAdapterPlayModeTests : ResourcePlayModeTestBase
    {
        private const string TextAddress = "ResourceSystemPlayMode/SampleText";

        [UnityTest]
        public IEnumerator ExecutionContextDispatchesWorkerCallsAndAsyncContinuationsToMainThread()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var context = new UnityExecutionContext();
            var threads = new int[6];
            var posted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = Task.Run(async () =>
            {
                threads[0] = Thread.CurrentThread.ManagedThreadId;
                await context.RunAsync(() => { threads[1] = Thread.CurrentThread.ManagedThreadId; });
                threads[2] = await context.RunAsync(() => Thread.CurrentThread.ManagedThreadId);
                await context.InvokeAsync(async () =>
                {
                    threads[3] = Thread.CurrentThread.ManagedThreadId;
                    await Task.Yield();
                    threads[4] = Thread.CurrentThread.ManagedThreadId;
                    return true;
                });
                context.Post(() => posted.TrySetResult(Thread.CurrentThread.ManagedThreadId));
                threads[5] = await posted.Task;
            });

            yield return Await(work);
            Assert.That(threads[0], Is.Not.EqualTo(mainThread));
            for (var i = 1; i < threads.Length; i++)
                Assert.That(threads[i], Is.EqualTo(mainThread), "入口或异步接续没有回到 Unity 主线程，索引 " + i);
            Assert.That(context.IsMainThread, Is.True);

            // 已在主线程和同步完成的异步委托也必须可等待，不依赖再过一帧。
            var inline = context.RunAsync(() => Thread.CurrentThread.ManagedThreadId);
            Assert.That(inline.IsCompleted, Is.True);
            Assert.That(inline.Result, Is.EqualTo(mainThread));
            var completed = context.InvokeAsync(() => Task.FromResult(mainThread));
            yield return Await(completed);
            Assert.That(completed.Result, Is.EqualTo(mainThread));
        }

        [UnityTest]
        public IEnumerator ExecutionContextPropagatesFaultsAndCancellationFromWorkerCalls()
        {
            var context = new UnityExecutionContext();
            var syncError = new InvalidOperationException("synchronous callback failure");
            var synchronous = Task.Run(() => context.RunAsync((Action)(() => { throw syncError; })));
            yield return Finish(synchronous);
            Assert.That(synchronous.IsFaulted, Is.True);
            Assert.That(synchronous.Exception.GetBaseException(), Is.SameAs(syncError));

            var asyncError = new InvalidOperationException("asynchronous callback failure");
            var asynchronous = Task.Run(() => context.InvokeAsync<int>(async () =>
            {
                await Task.Yield();
                throw asyncError;
            }));
            yield return Finish(asynchronous);
            Assert.That(asynchronous.IsFaulted, Is.True);
            Assert.That(asynchronous.Exception.GetBaseException(), Is.SameAs(asyncError));

            var cancelledCallback = Task.Run(() => context.RunAsync<int>(() => { throw new OperationCanceledException(); }));
            yield return Finish(cancelledCallback);
            Assert.That(cancelledCallback.IsCanceled, Is.True);

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var cancelledTask = Task.Run(() => context.InvokeAsync(() => Task.FromCanceled<int>(cancellation.Token)));
                yield return Finish(cancelledTask);
                Assert.That(cancelledTask.IsCanceled, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator ResourcesLoaderLoadsRealTextFromWorkerAndDefaultReleasePreservesSharedAsset()
        {
            var context = new UnityExecutionContext();
            var loader = new ResourcesLoader();
            var key = new ResourceKey(ResourceSource.Resources, TextAddress, typeof(TextAsset));
            var loading = Task.Run(() => context.InvokeAsync(() => loader.LoadAsync(key, null, CancellationToken.None)));
            yield return Await(loading);
            var product = loading.Result;
            var text = product.Value as TextAsset;
            Assert.That(text, Is.Not.Null);
            Assert.That(text.text, Is.EqualTo("resource-system-playmode\n"));

            product.Release();
            yield return null;
            Assert.That(text != null, Is.True, "默认 Release 不拥有 Unity 共享资产的卸载责任。");
            Assert.That(text.text, Is.EqualTo("resource-system-playmode\n"));
        }

        [UnityTest]
        public IEnumerator ResourcesLoaderCanExplicitlyUnloadTextAndLoadItAgain()
        {
            var context = new UnityExecutionContext();
            var loader = new ResourcesLoader(unloadAssetOnRelease: true);
            var key = new ResourceKey(ResourceSource.Resources, TextAddress, typeof(TextAsset));
            var loading = Task.Run(() => context.InvokeAsync(() => loader.LoadAsync(key, null, CancellationToken.None)));
            yield return Await(loading);
            var first = (TextAsset)loading.Result.Value;
            Assert.That(IsTextAssetLoaded(first), Is.True);
            loading.Result.Release();
            // Editor 中普通资产的托管包装仍可能判为非 null，访问它还会触发重新加载。
            // 这里只检查原生已加载集合；不能用 first == null 或读取 first.text 观察卸载。
            Assert.That(IsTextAssetLoaded(first), Is.False, "显式 Release 必须从内存卸载该 TextAsset。");

            var reloading = Task.Run(() => context.InvokeAsync(() => loader.LoadAsync(key, null, CancellationToken.None)));
            yield return Await(reloading);
            try
            {
                var second = (TextAsset)reloading.Result.Value;
                Assert.That(second != null, Is.True);
                Assert.That(IsTextAssetLoaded(second), Is.True);
                Assert.That(second.text, Is.EqualTo("resource-system-playmode\n"));
            }
            finally { reloading.Result.Release(); }
        }

        [UnityTest]
        public IEnumerator ResourcesLoaderReportsMissingAssetAndHonorsPreCancelledRequest()
        {
            var context = new UnityExecutionContext();
            var loader = new ResourcesLoader();
            var missingKey = new ResourceKey(ResourceSource.Resources,
                "ResourceSystemPlayMode/Missing-" + Guid.NewGuid().ToString("N"), typeof(TextAsset));
            var missing = Task.Run(() => context.InvokeAsync(() => loader.LoadAsync(missingKey, null, CancellationToken.None)));
            yield return Finish(missing);
            Assert.That(missing.IsFaulted, Is.True);
            var error = missing.Exception.GetBaseException() as ResourceException;
            Assert.That(error, Is.Not.Null);
            Assert.That(error.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.AssetNotFound));

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var key = new ResourceKey(ResourceSource.Resources, TextAddress, typeof(TextAsset));
                var cancelled = Task.Run(() => context.InvokeAsync(() => loader.LoadAsync(key, null, cancellation.Token)));
                yield return Finish(cancelled);
                Assert.That(cancelled.IsCanceled, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator TextureConversionThroughSystemProducesReadablePixelsAndUnloadDestroysAcrossFrame()
        {
            var loader = new ImageBytesLoader(CreatePng());
            var system = Track(new CoreResourceSystem(new ResourceSystemOptions
            {
                ExecutionContext = new UnityExecutionContext()
            }.AddLoader(ResourceSource.Local, loader).AddConverter<Texture2D>(new TextureConverter(readable: true))));
            yield return Await(system.InitializeAsync());

            ResourceRef<Texture2D> reference = null;
            Texture2D texture = null;
            try
            {
                var loading = Task.Run(() => system.LoadAsync<Texture2D>(ResourceSource.Local, "image.png"));
                yield return Await(loading);
                reference = loading.Result;
                texture = TrackObject(reference.Value);
                Assert.That(texture.width, Is.EqualTo(2));
                Assert.That(texture.height, Is.EqualTo(2));
                Assert.That(texture.isReadable, Is.True);
                Assert.That(texture.GetPixel(0, 0).r, Is.EqualTo(1f).Within(0.01f));
                Assert.That(texture.GetPixel(0, 0).g, Is.EqualTo(0f).Within(0.01f));
                Assert.That(loader.Released, Is.EqualTo(1), "转换完成后输入 byte[] 产物也必须释放。");

                reference.Dispose();
                Assert.That(texture != null, Is.True, "归还引用只结束持有，卸载由业务显式执行。");
                var unloading = system.UnloadAsync();
                yield return Await(unloading);
                Assert.That(unloading.Result.Released.Count, Is.EqualTo(1));
                yield return Until(() => texture == null);
            }
            finally { reference?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator TextureConverterDefaultDropsCpuCopyAndReleaseSchedulesNativeDestruction()
        {
            var key = new ResourceKey(ResourceSource.Local, "image.png", typeof(Texture2D));
            var conversion = new TextureConverter().ConvertAsync(new Product(CreatePng()), key, CancellationToken.None);
            yield return Await(conversion);
            var texture = TrackObject((Texture2D)conversion.Result.Value);
            Assert.That(texture.isReadable, Is.False);
            conversion.Result.Release();
            Assert.That(texture != null, Is.True, "Object.Destroy 应在帧末完成，而非立即销毁。");
            yield return null;
            Assert.That(texture == null, Is.True);
        }

        [UnityTest]
        public IEnumerator TextureConverterRejectsWrongRepresentationAndInvalidImageBytes()
        {
            var converter = new TextureConverter();
            var key = new ResourceKey(ResourceSource.Local, "invalid-image", typeof(Texture2D));
            var representation = Assert.Throws<ResourceException>(() =>
                converter.ConvertAsync(new Product("not bytes"), key, CancellationToken.None));
            Assert.That(representation.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.AssetUnsupportedRepresentation));

            var decoding = Assert.Throws<ResourceException>(() =>
                converter.ConvertAsync(new Product(new byte[] { 0, 1, 2, 3 }), key, CancellationToken.None));
            Assert.That(decoding.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.AssetConversionFailed));
            // 让失败分支的帧末清理结束，再进入下一项测试。
            yield return null;
        }

        private byte[] CreatePng()
        {
            var source = TrackObject(new Texture2D(2, 2, TextureFormat.RGBA32, false));
            source.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
            source.Apply();
            return ImageConversion.EncodeToPNG(source);
        }

        private static bool IsTextAssetLoaded(TextAsset expected)
            => Array.Exists(Resources.FindObjectsOfTypeAll<TextAsset>(), asset => asset == expected);

        private sealed class ImageBytesLoader : ILoader
        {
            private readonly byte[] _bytes;
            public int Released;
            public ImageBytesLoader(byte[] bytes) => _bytes = bytes;

            public Task<IResource> LoadAsync(ResourceKey key,
                IProgress<OperationProgress> progress, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                return Task.FromResult<IResource>(new Product(_bytes, () => Interlocked.Increment(ref Released)));
            }
        }
    }
}
