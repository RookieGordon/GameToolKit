using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityToolKit.Engine.UI;
using UnityToolKit.Runtime.ResourceSystem;
using RS = ToolKit.Tools.Common.ResourceSystem;
using Object = UnityEngine.Object;

namespace Tests.PlayMode.UIResource
{
    public sealed class ImageResourceOwnershipPlayModeTests
    {
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<Ui> _images = new List<Ui>();
        private readonly List<Object> _assets = new List<Object>();
        private ControlledLoader _loader;
        private ResourceManager _manager;
        private RS _system;
        private object _previousSystem;
        private object _previousShutdown;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _loader = new ControlledLoader();
            var options = new ResourceSystemOptions { ExecutionContext = new UnityExecutionContext() };
            options.AddLoader(ResourceSource.AssetBundle, _loader);
            options.AddLoader(ResourceSource.Resources, _loader);
            _system = new RS(options);
            yield return Await(_system.InitializeAsync());
            _manager = ResourceManager.Instance;
            _previousSystem = Field("_resSystem").GetValue(_manager);
            _previousShutdown = Field("_shutdown").GetValue(_manager);
            Field("_resSystem").SetValue(_manager, _system);
            Field("_shutdown").SetValue(_manager, null);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var image in _images)
                if (image.Graphic != null) Object.Destroy(image.Graphic.gameObject);
            yield return null;
            // 释放受控后端的所有等待，失败用例也不能让系统关闭永久等待。
            foreach (var gate in _loader.Gates.Values) gate.Complete();
            yield return Await(_system.DisposeAsync().AsTask());
            yield return null;
            yield return null;
            Field("_resSystem").SetValue(_manager, _previousSystem);
            Field("_shutdown").SetValue(_manager, _previousShutdown);
            foreach (var asset in _assets) if (asset != null) Object.Destroy(asset);
            _assets.Clear();
            _images.Clear();
            yield return null;
        }

        [UnityTest] public IEnumerator SpriteCancelThenLoadBRejectsLateA() => CancelThenLoadB(false);
        [UnityTest] public IEnumerator TextureCancelThenLoadBRejectsLateA() => CancelThenLoadB(true);
        [UnityTest] public IEnumerator SpriteClearedByInheritedPropertyStillReturnsItsReference() => ClearInheritedProperty(false);
        [UnityTest] public IEnumerator TextureClearedByInheritedPropertyStillReturnsItsReference() => ClearInheritedProperty(true);
        [UnityTest] public IEnumerator CachedSpriteReplacementAndFailureKeepOnlyTheDisplayedReference() => CachedReplacement(false);
        [UnityTest] public IEnumerator CachedTextureReplacementAndFailureKeepOnlyTheDisplayedReference() => CachedReplacement(true);
        [UnityTest] public IEnumerator DestroyingImageReturnsCurrentAndLateSpriteReferences() => DestroyDuringLoad(false);
        [UnityTest] public IEnumerator DestroyingRawImageReturnsCurrentAndLateTextureReferences() => DestroyDuringLoad(true);
        [UnityTest] public IEnumerator SpriteDirtyCallbackCanStartACancellableReplacement() => DirtyCallbackReplacement(false);
        [UnityTest] public IEnumerator TextureDirtyCallbackCanStartACancellableReplacement() => DirtyCallbackReplacement(true);
        [UnityTest] public IEnumerator CancelingAQueuedSpriteFailureDoesNotInvokeItsErrorFallback() => CancelQueuedFailure(false);
        [UnityTest] public IEnumerator CancelingAQueuedTextureFailureDoesNotInvokeItsErrorFallback() => CancelQueuedFailure(true);
        [UnityTest] public IEnumerator RepeatingAnInFlightSpriteRequestKeepsTheOriginalRequest() => RepeatInFlight(false);
        [UnityTest] public IEnumerator RepeatingAnInFlightTextureRequestKeepsTheOriginalRequest() => RepeatInFlight(true);
        [UnityTest] public IEnumerator RepeatingTheDisplayedSpriteAppliesTheNewSettingsAndReleasesTheOldReference() => RepeatDisplayed(false);
        [UnityTest] public IEnumerator RepeatingTheDisplayedTextureAppliesTheNewSettingsAndReleasesTheOldReference() => RepeatDisplayed(true);
        [UnityTest] public IEnumerator SelectingTheDisplayedSpriteCancelsAPendingReplacement() => SelectDisplayedDuringReplacement(false);
        [UnityTest] public IEnumerator SelectingTheDisplayedTextureCancelsAPendingReplacement() => SelectDisplayedDuringReplacement(true);
        [UnityTest] public IEnumerator SpriteDeduplicationDistinguishesResourceSources() => DifferentSources(false);
        [UnityTest] public IEnumerator TextureDeduplicationDistinguishesResourceSources() => DifferentSources(true);
        [UnityTest] public IEnumerator SpriteCanBeRequestedAgainAfterCancelClearAndFailure() => RetryAfterCancelClearAndFailure(false);
        [UnityTest] public IEnumerator TextureCanBeRequestedAgainAfterCancelClearAndFailure() => RetryAfterCancelClearAndFailure(true);
        [UnityTest] public IEnumerator SpriteExternallyReplacedCanBeRestoredFromItsAddress() => RestoreAfterExternalReplacement(false);
        [UnityTest] public IEnumerator TextureExternallyReplacedCanBeRestoredFromItsAddress() => RestoreAfterExternalReplacement(true);

        private IEnumerator RepeatInFlight(bool raw)
        {
            var a = Prepare("a", delayed: true);
            var image = Create(raw);
            var canceled = 0;
            var duplicateNotifications = 0;
            image.Graphic.rectTransform.sizeDelta = new Vector2(19, 23);
            image.Set("a", onCancelled: () => canceled++);
            yield return WaitUntil(() => a.Started == 1);
            image.Set("a", onError: _ => duplicateNotifications++,
                onCancelled: () => duplicateNotifications++, nativeSize: true);
            a.Complete();
            yield return WaitUntil(() => image.Displayed == a.Value(raw));
            Assert.That(canceled, Is.Zero, "重复设置不能取消原来的加载。");
            Assert.That(duplicateNotifications, Is.Zero, "被拦截的调用不创建新的请求或回调。");
            Assert.That(a.Started, Is.EqualTo(1));
            Assert.That(Holds("a"), Is.EqualTo(1));
            Assert.That(image.Graphic.rectTransform.sizeDelta, Is.EqualTo(new Vector2(19, 23)),
                "重复调用的参数不能改写原请求。");
            image.Clear();
            yield return AssertFullyUnloadable();
        }

        private IEnumerator RepeatDisplayed(bool raw)
        {
            var a = Prepare("a");
            var image = Create(raw);
            image.Set("a");
            yield return WaitUntil(() => image.Displayed == a.Value(raw));
            image.Graphic.rectTransform.sizeDelta = new Vector2(19, 23);
            image.Set("a", nativeSize: true);
            yield return null;
            Assert.That(image.Graphic.rectTransform.sizeDelta, Is.EqualTo(new Vector2(4, 4)),
                "再次设置已显示的图片也应走正常替换流程，应用本次尺寸参数。");
            Assert.That(image.Displayed, Is.SameAs(a.Value(raw)));
            Assert.That(a.Started, Is.EqualTo(1), "底层资源缓存仍可复用已经加载的产物。");
            Assert.That(Holds("a"), Is.EqualTo(1), "新引用交付后必须归还旧引用。");
            image.Clear();
            Assert.That(Holds("a"), Is.Zero);
            yield return AssertFullyUnloadable();
        }

        private IEnumerator SelectDisplayedDuringReplacement(bool raw)
        {
            var a = Prepare("a");
            var b = Prepare("b", delayed: true);
            var image = Create(raw);
            image.Set("a");
            yield return WaitUntil(() => image.Displayed == a.Value(raw));
            image.Graphic.rectTransform.sizeDelta = new Vector2(19, 23);
            var canceled = 0;
            image.Set("b", onCancelled: () => canceled++);
            yield return WaitUntil(() => b.Started == 1);
            image.Set("a", nativeSize: true);
            yield return WaitUntil(() => canceled == 1);
            b.Complete();
            yield return WaitUntil(() => b.Released == 1);
            Assert.That(image.Displayed, Is.SameAs(a.Value(raw)), "重新选择当前 A 后，迟到的 B 不能替换它。");
            Assert.That(image.Graphic.rectTransform.sizeDelta, Is.EqualTo(new Vector2(4, 4)),
                "最后一次 A 请求应正常应用本次参数，不能只取消 B 后提前返回。");
            Assert.That(a.Started, Is.EqualTo(1));
            Assert.That(Holds("a"), Is.EqualTo(1));
            Assert.That(Holds("b"), Is.Zero);
            image.Clear();
            Assert.That(Holds("a"), Is.Zero);
            yield return AssertFullyUnloadable();
        }

        private IEnumerator DifferentSources(bool raw)
        {
            var bundle = Prepare("same", delayed: true);
            var resources = Prepare("same", source: ResourceSource.Resources);
            var image = Create(raw);
            var canceled = 0;
            image.Set("same", onCancelled: () => canceled++);
            yield return WaitUntil(() => bundle.Started == 1);
            image.Set("same", source: ResourceSource.Resources);
            yield return WaitUntil(() => image.Displayed == resources.Value(raw));
            yield return WaitUntil(() => canceled == 1);
            bundle.Complete();
            yield return WaitUntil(() => bundle.Released == 1);
            image.Set("same");
            yield return WaitUntil(() => image.Displayed == bundle.Value(raw));
            Assert.That(bundle.Started, Is.EqualTo(2), "相同地址的不同来源必须允许重新请求。");
            Assert.That(resources.Started, Is.EqualTo(1));
            Assert.That(Holds("same"), Is.EqualTo(1));
            image.Clear();
            yield return AssertFullyUnloadable();
        }

        private IEnumerator RetryAfterCancelClearAndFailure(bool raw)
        {
            var a = Prepare("a", delayed: true);
            var image = Create(raw);
            var canceled = 0;
            image.Set("a", onCancelled: () => canceled++);
            yield return WaitUntil(() => a.Started == 1);
            image.Cancel();
            yield return WaitUntil(() => canceled == 1);
            a.Complete();
            yield return WaitUntil(() => a.Released == 1);
            image.Set("a");
            yield return WaitUntil(() => image.Displayed == a.Value(raw));
            image.Clear();
            Assert.That(Holds("a"), Is.Zero);
            image.Set("a");
            yield return WaitUntil(() => image.Displayed == a.Value(raw));
            Assert.That(Holds("a"), Is.EqualTo(1), "Clear 之后相同地址仍能恢复显示并重新持有。");

            Prepare("failure", fail: true);
            var errors = 0;
            image.Set("failure", onError: _ => errors++);
            yield return WaitUntil(() => errors == 1);
            var recovered = Prepare("failure");
            image.Set("failure", onError: _ => errors++);
            yield return WaitUntil(() => image.Displayed == recovered.Value(raw));
            Assert.That(errors, Is.EqualTo(1));
            Assert.That(Holds("a"), Is.Zero);
            Assert.That(Holds("failure"), Is.EqualTo(1));
            image.Clear();
            yield return AssertFullyUnloadable();
        }

        private IEnumerator RestoreAfterExternalReplacement(bool raw)
        {
            var a = Prepare("a");
            var external = Prepare("external");
            var image = Create(raw);
            image.Set("a");
            yield return WaitUntil(() => image.Displayed == a.Value(raw));
            foreach (var replacement in new[] { external.Value(raw), null })
            {
                image.SetInheritedProperty(replacement);
                image.Set("a");
                yield return WaitUntil(() => image.Displayed == a.Value(raw));
                Assert.That(Holds("a"), Is.EqualTo(1), "持有资源与显示资源不一致时必须恢复显示，且不增加持有数。");
            }
            image.Clear();
            yield return AssertFullyUnloadable();
        }

        [UnityTest]
        public IEnumerator CancelCallbackCanStartANewRequestWithoutItsStateBeingCleared()
        {
            var a = Prepare("a", delayed: true);
            var b = Prepare("b");
            var image = Create(raw: false);
            var canceled = 0;
            image.Set("a", onCancelled: () => { canceled++; image.Set("b"); });
            yield return WaitUntil(() => a.Started > 0);
            image.Cancel();
            yield return WaitUntil(() => image.Displayed == b.Sprite);
            Assert.That(canceled, Is.EqualTo(1));
            a.Complete();
            yield return WaitUntil(() => a.Released == 1);
            Assert.That(image.Displayed, Is.SameAs(b.Sprite));
            image.Clear();
            Assert.That(Holds("b"), Is.Zero);
        }

        [UnityTest]
        public IEnumerator ErrorCallbackCanStartANewTextureRequest()
        {
            Prepare("failure", fail: true);
            var b = Prepare("b");
            var image = Create(raw: true);
            var errors = 0;
            image.Set("failure", onError: _ => { errors++; image.Set("b"); });
            yield return WaitUntil(() => image.Displayed == b.Texture);
            Assert.That(errors, Is.EqualTo(1));
            Assert.That(Holds("b"), Is.EqualTo(1));
            image.Clear();
            Assert.That(Holds("b"), Is.Zero);
        }

        private IEnumerator CancelThenLoadB(bool raw)
        {
            var a = Prepare("a", delayed: true);
            var b = Prepare("b");
            var image = Create(raw);
            var canceled = 0;
            image.Set("a", onCancelled: () => canceled++);
            yield return WaitUntil(() => a.Started > 0);
            image.Cancel();
            image.Set("b");
            yield return WaitUntil(() => image.Displayed == b.Value(raw));
            yield return WaitUntil(() => canceled == 1);
            a.Complete();
            yield return WaitUntil(() => a.Released == 1);
            Assert.That(image.Displayed, Is.SameAs(b.Value(raw)), "迟到的 A 不能覆盖 B。");
            Assert.That(Holds("a"), Is.Zero);
            Assert.That(Holds("b"), Is.EqualTo(1));
            image.Clear();
            yield return AssertFullyUnloadable();
        }

        private IEnumerator CancelQueuedFailure(bool raw)
        {
            var failure = Prepare("failure", delayed: true, fail: true);
            var b = Prepare("b");
            Prepare("fallback");
            yield return Warm("b", raw);
            var image = Create(raw);
            var errors = 0;
            var canceled = 0;
            var queued = new QueuedSynchronizationContext();
            var unityContext = SynchronizationContext.Current;
            try
            {
                // 只让本次加载捕获可控队列，测试协程和资源上下文仍使用 Unity 主线程。
                SynchronizationContext.SetSynchronizationContext(queued);
                image.Set("failure", onError: _ => { errors++; image.Set("fallback"); },
                    onCancelled: () => canceled++);
            }
            finally { SynchronizationContext.SetSynchronizationContext(unityContext); }

            try
            {
                yield return WaitUntil(() => failure.Started > 0);
                failure.Complete();
                yield return WaitUntil(() => queued.Count > 0);
                // 先交付后端故障给 ResourceManager；UI 的 await 续体仍留在队列里。
                queued.RunOne();
                Assert.That(queued.Count, Is.GreaterThan(0), "需要把取消安排在 Task 已故障、UI 尚未收到结果的窗口。");
                Assert.That(errors + canceled, Is.Zero);

                image.Cancel();
                image.Set("b");
                queued.Drain();
                Assert.That(errors, Is.Zero, "取消后的旧失败不能再触发错误兜底并覆盖新请求。");
                Assert.That(canceled, Is.EqualTo(1));
                Assert.That(image.Displayed, Is.SameAs(b.Value(raw)));
                Assert.That(Holds("b"), Is.EqualTo(1));
                image.Clear();
                yield return AssertFullyUnloadable();
            }
            finally { queued.Drain(); }
        }

        private IEnumerator ClearInheritedProperty(bool raw)
        {
            var asset = Prepare("a");
            var image = Create(raw);
            image.Set("a");
            yield return WaitUntil(() => image.Displayed == asset.Value(raw));
            Assert.That(Holds("a"), Is.EqualTo(1));
            image.SetInheritedProperty(null);
            image.Clear();
            image.Clear();
            Assert.That(Holds("a"), Is.Zero, "图片属性为空不代表 ResourceRef 已经归还。");
            yield return AssertFullyUnloadable();
        }

        private IEnumerator CachedReplacement(bool raw)
        {
            Prepare("a");
            var b = Prepare("b");
            Prepare("failure", fail: true);
            yield return Warm("a", raw);
            yield return Warm("b", raw);
            var image = Create(raw);
            image.Set("a");
            image.Set("b");
            yield return WaitUntil(() => image.Displayed == b.Value(raw));
            Assert.That(Holds("a"), Is.Zero, "同帧替换不能丢弃缓存命中已取得的引用。");
            Assert.That(Holds("b"), Is.EqualTo(1));
            var errors = 0;
            image.Set("failure", onError: _ => errors++);
            yield return WaitUntil(() => errors == 1);
            Assert.That(image.Displayed, Is.SameAs(b.Value(raw)), "失败应保留当前图像。");
            image.Cancel();
            image.Clear();
            yield return AssertFullyUnloadable();
        }

        private IEnumerator DestroyDuringLoad(bool raw)
        {
            var current = Prepare("current");
            var late = Prepare("late", delayed: true);
            var image = Create(raw);
            image.Set("current");
            yield return WaitUntil(() => image.Displayed == current.Value(raw));
            image.Set("late");
            yield return WaitUntil(() => late.Started > 0);
            Object.Destroy(image.Graphic.gameObject);
            yield return null;
            Assert.That(Holds("current"), Is.Zero);
            late.Complete();
            yield return WaitUntil(() => late.Released == 1);
            yield return AssertFullyUnloadable();
        }

        private IEnumerator DirtyCallbackReplacement(bool raw)
        {
            var original = Prepare("original");
            var a = Prepare("a");
            var b = Prepare("b", delayed: true);
            yield return Warm("a", raw);
            var image = Create(raw);
            image.Set("original");
            yield return WaitUntil(() => image.Displayed == original.Value(raw));
            var triggered = false;
            var canceled = 0;
            UnityAction callback = () =>
            {
                if (triggered) return;
                triggered = true;
                image.Set("b", onCancelled: () => canceled++);
            };
            image.Graphic.RegisterDirtyVerticesCallback(callback);
            image.Set("a");
            yield return WaitUntil(() => b.Started > 0);
            image.Graphic.UnregisterDirtyVerticesCallback(callback);
            image.Cancel();
            yield return WaitUntil(() => canceled == 1);
            b.Complete();
            yield return WaitUntil(() => b.Released == 1);
            Assert.That(image.Displayed, Is.SameAs(a.Value(raw)));
            Assert.That(Holds("original"), Is.Zero);
            Assert.That(Holds("a"), Is.EqualTo(1));
            Assert.That(Holds("b"), Is.Zero);
            image.Clear();
            yield return AssertFullyUnloadable();
        }

        private Gate Prepare(string address, bool delayed = false, bool fail = false,
            ResourceSource source = ResourceSource.AssetBundle)
        {
            var texture = new Texture2D(4, 4) { name = address + " Texture" };
            var sprite = Sprite.Create(texture, new Rect(0, 0, 4, 4), Vector2.zero);
            sprite.name = address + " Sprite";
            _assets.Add(sprite);
            _assets.Add(texture);
            var gate = new Gate(texture, sprite, fail);
            _loader.Gates[(source, address)] = gate;
            if (!delayed) gate.Complete();
            return gate;
        }

        private Ui Create(bool raw)
        {
            var obj = new GameObject("UI ownership fixture", typeof(RectTransform), typeof(CanvasRenderer));
            var image = raw ? new Ui(obj.AddComponent<RawImageExt>()) : new Ui(obj.AddComponent<ImageExt>());
            _images.Add(image);
            return image;
        }

        private IEnumerator Warm(string address, bool raw)
        {
            if (raw)
            {
                var task = _system.LoadAsync<Texture2D>(ResourceSource.AssetBundle, address);
                yield return Await(task);
                task.Result.Dispose();
            }
            else
            {
                var task = _system.LoadAsync<Sprite>(ResourceSource.AssetBundle, address);
                yield return Await(task);
                task.Result.Dispose();
            }
        }

        private int Holds(string address) => _system.Snapshot().Entries
            .Where(entry => entry.Key.Address == address).Sum(entry => entry.Holds);

        private IEnumerator AssertFullyUnloadable()
        {
            yield return null;
            var task = _system.UnloadAsync();
            yield return Await(task);
            Assert.That(task.Result.SkippedInUse, Is.Empty);
            Assert.That(task.Result.Failed, Is.Empty);
            Assert.That(_system.Snapshot().Entries, Is.Empty);
        }

        private static FieldInfo Field(string name) => typeof(ResourceManager).GetField(name, Private);

        private static IEnumerator Await(Task task)
        {
            yield return WaitUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            var deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (!condition())
            {
                Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(deadline), "等待图片操作超时。");
                yield return null;
            }
        }

        private sealed class Ui
        {
            private readonly ImageExt _image;
            private readonly RawImageExt _raw;
            public Ui(ImageExt image) { _image = image; Graphic = image; }
            public Ui(RawImageExt raw) { _raw = raw; Graphic = raw; }
            public Graphic Graphic { get; }
            public Object Displayed => _image != null ? (Object)_image.sprite : _raw.texture;
            public void Set(string address, Action<Exception> onError = null, Action onCancelled = null,
                ResourceSource source = ResourceSource.AssetBundle, bool nativeSize = false)
            {
                if (_image != null) _image.SetSprite(address, source, nativeSize, onError, onCancelled);
                else _raw.SetTexture(address, source, nativeSize, onError, onCancelled);
            }
            public void Clear() { if (_image != null) _image.ClearSprite(); else _raw.ClearTexture(); }
            public void Cancel() { if (_image != null) _image.CancelSet(); else _raw.CancelSet(); }
            public void SetInheritedProperty(Object value)
            {
                if (_image != null) _image.sprite = (Sprite)value;
                else _raw.texture = (Texture)value;
            }
        }

        private sealed class Gate
        {
            public readonly Texture2D Texture;
            public readonly Sprite Sprite;
            public readonly bool Fail;
            public readonly TaskCompletionSource<bool> Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Started;
            public int Released;
            public Gate(Texture2D texture, Sprite sprite, bool fail) { Texture = texture; Sprite = sprite; Fail = fail; }
            public Object Value(bool raw) => raw ? (Object)Texture : Sprite;
            public void Complete() => Ready.TrySetResult(true);
        }

        private sealed class QueuedSynchronizationContext : SynchronizationContext
        {
            private readonly ConcurrentQueue<Action> _callbacks = new ConcurrentQueue<Action>();
            public int Count => _callbacks.Count;
            public override void Post(SendOrPostCallback callback, object state)
                => _callbacks.Enqueue(() => callback(state));
            public void RunOne()
            {
                Assert.That(_callbacks.TryDequeue(out var callback), Is.True);
                // 保持调用者的 Unity SynchronizationContext，下一层 await 才会重新排队。
                callback();
            }
            public void Drain()
            {
                while (_callbacks.TryDequeue(out var callback)) callback();
            }
        }

        private sealed class ControlledLoader : ILoader
        {
            public readonly ConcurrentDictionary<(ResourceSource, string), Gate> Gates = new ConcurrentDictionary<(ResourceSource, string), Gate>();
            public async Task<IResource> LoadAsync(ResourceKey key, IExecutionContext context,
                IProgress<OperationProgress> progress, CancellationToken cancellation)
            {
                var gate = Gates[(key.Source, key.Address)];
                Interlocked.Increment(ref gate.Started);
                // 故意允许后端迟到，验证取消后的产物仍有人收尾。
                await gate.Ready.Task.ConfigureAwait(false);
                if (gate.Fail) throw new InvalidOperationException("fixture load failed");
                return new Product(key.RequestedType == typeof(Sprite) ? (object)gate.Sprite : gate.Texture,
                    () => Interlocked.Increment(ref gate.Released));
            }
        }
    }
}
