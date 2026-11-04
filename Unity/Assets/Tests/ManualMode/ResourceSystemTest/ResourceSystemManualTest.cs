using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.UI;
using UnityToolKit.Runtime.ResourceSystem;
using RS = ToolKit.Tools.Common.ResourceSystem;

namespace Tests.ResourceSystemManual
{
    /// <summary>独立的人工测试入口：加载、持有、归还和卸载均通过真实 ResourceSystem。</summary>
    public sealed class ResourceSystemManualTest : MonoBehaviour
    {
        [Header("场景专用配置与 Resources 地址输入")]
        public ResourceSystemConfig Config;
        public InputField TextureAddressInput;
        public InputField SpriteAddressInput;
        public InputField PrefabAddressInput;
        public InputField LoadCountInput;
        public InputField ReturnCountInput;

        [Header("预览")]
        public RawImage TexturePreview;
        public Image SpritePreview;
        public Transform InstanceRoot;

        [Header("操作")]
        public Button LoadTextureButton;
        public Button ReleaseTextureButton;
        public Button LoadSpriteButton;
        public Button ReleaseSpriteButton;
        public Button LoadGameObjectButton;
        public Button ReturnInstancesButton;
        public Button CancelLoadButton;
        public Button ClearPoolButton;
        public Button UnloadIdleButton;
        public Button ReleaseAllButton;
        public Button UnloadUnityUnusedButton;

        [Header("状态")]
        public Text StatusText;
        public Text CacheText;
        public Text LogText;
        public Text InstanceCountText;

        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly Queue<string> _log = new Queue<string>();
        private RS _system;
        private ResourceRef<Texture2D> _texture;
        private ResourceRef<Sprite> _sprite;
        private readonly List<InstanceLease<GameObject>> _instances = new List<InstanceLease<GameObject>>();
        private CancellationTokenSource _loadCancellation;
        private Font _font;
        private Task _operation = Task.CompletedTask;
        private Task _shutdown;
        private bool _closing;

        public bool IsBusy { get; private set; }
        public bool IsReady => !_closing && _system != null && _system.IsInitialized && !_system.IsDisposed;
        public int ActiveInstanceCount => _instances.Count;

        private void Awake()
        {
            // 字体按运行机选择，场景不携带系统字体文件。
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial" }, 18);
            if (_font != null)
                foreach (var label in GetComponentsInChildren<Text>(true)) label.font = _font;

            LoadTextureButton.onClick.AddListener(() => _ = LoadTextureAsync());
            ReleaseTextureButton.onClick.AddListener(() => _ = ExecuteAsync("归还纹理引用", _ =>
            {
                ReleaseTexture();
                return Task.CompletedTask;
            }));
            LoadSpriteButton.onClick.AddListener(() => _ = LoadSpriteAsync());
            ReleaseSpriteButton.onClick.AddListener(() => _ = ExecuteAsync("归还精灵引用", _ =>
            {
                ReleaseSprite();
                return Task.CompletedTask;
            }));
            LoadGameObjectButton.onClick.AddListener(() => _ = LoadGameObjectAsync());
            ReturnInstancesButton.onClick.AddListener(() => _ = ReturnInstancesAsync());
            CancelLoadButton.onClick.AddListener(CancelLoad);
            ClearPoolButton.onClick.AddListener(() => _ = ClearPoolAsync());
            UnloadIdleButton.onClick.AddListener(() => _ = ExecuteAsync("卸载空闲资源", _ => UnloadIdleAsync()));
            ReleaseAllButton.onClick.AddListener(() => _ = ExecuteAsync("释放全部并卸载", async _ =>
            {
                ReleaseTexture();
                ReleaseSprite();
                await ReturnInstancesCoreAsync(_instances.Count);
                await ClearPoolCoreAsync();
                await UnloadIdleAsync();
            }));
            UnloadUnityUnusedButton.onClick.AddListener(() => _ = ExecuteAsync("Unity 清理未使用资产", async _ =>
            {
                // 闲置实例也持有原型；先清池和缓存，再由 Unity 扫描未引用资产。
                await ClearPoolCoreAsync();
                await UnloadIdleAsync();
                var cleanup = Resources.UnloadUnusedAssets();
                while (!cleanup.isDone) await Awaitable.NextFrameAsync();
                AppendLog("Unity 全局空闲资产扫描完成；仍被场景或静态字段引用的资产会保留。");
            }));
            RefreshView();
        }

        private void Start()
        {
            _ = ExecuteAsync("初始化", async token =>
            {
                if (Config == null) throw new InvalidOperationException("请指定场景专用 ResourceSystemConfig。");
                _system = new RS(Config.ConvertToOptions());
                await _system.InitializeAsync(token);
                AppendLog("系统就绪。输入 Resources 相对地址；加载中可取消，实例可按数量回收。");
            });
        }

        public Task LoadTextureAsync() => ExecuteAsync("加载纹理", async token =>
        {
            var address = ReadAddress(TextureAddressInput);
            AppendLog("请求纹理：" + address);
            var loaded = await _system.LoadAsync<Texture2D>(ResourceSource.Resources, address, token: token);
            if (token.IsCancellationRequested) { loaded.Dispose(); token.ThrowIfCancellationRequested(); }
            // 新资源交付前保留旧预览，失败或取消不会清掉当前图片。
            ReleaseTexture();
            _texture = loaded;
            TexturePreview.texture = loaded.Value;
            TexturePreview.enabled = true;
            AppendLog($"纹理已加载：{loaded.Value.width} × {loaded.Value.height}，持有 1 份引用。");
        }, cancellable: true);

        public Task LoadSpriteAsync() => ExecuteAsync("加载精灵", async token =>
        {
            var address = ReadAddress(SpriteAddressInput);
            AppendLog("请求精灵：" + address);
            var loaded = await _system.LoadAsync<Sprite>(ResourceSource.Resources, address, token: token);
            if (token.IsCancellationRequested) { loaded.Dispose(); token.ThrowIfCancellationRequested(); }
            ReleaseSprite();
            _sprite = loaded;
            SpritePreview.sprite = loaded.Value;
            SpritePreview.enabled = true;
            AppendLog($"精灵已加载：{loaded.Value.name}，持有 1 份引用。");
        }, cancellable: true);

        public Task LoadGameObjectAsync() => ExecuteAsync("批量加载 GameObject", async token =>
        {
            // 整批使用点击时的输入，取消只影响当前批次。
            var address = ReadAddress(PrefabAddressInput);
            var count = ReadCount(LoadCountInput, "加载数量");
            AppendLog($"请求实例：{address}，追加 {count} 个。");
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var lease = await _system.RentAsync<GameObject>(ResourceSource.Resources, address, token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    lease.Value.transform.SetParent(InstanceRoot, false);
                    lease.Value.transform.localScale = Vector3.one;
                    _instances.Add(lease);
                }
                catch { lease.Dispose(); throw; }
                StatusText.text = $"批量加载 {i + 1}/{count}，当前在用 {_instances.Count} 个";
                RefreshView();
                // 缓存命中可能同步完成；逐帧推进，让输入和取消按钮有机会处理。
                if (i + 1 < count) await Awaitable.NextFrameAsync();
            }
            AppendLog($"本批已加载 {count} 个，当前在用 {_instances.Count} 个。");
        }, cancellable: true);

        public Task ReturnInstancesAsync() => ExecuteAsync("按数量回收实例", async _ =>
        {
            var requested = ReadCount(ReturnCountInput, "回收数量");
            var count = Math.Min(requested, _instances.Count);
            await ReturnInstancesCoreAsync(count);
            AppendLog($"请求回收 {requested} 个，实际回收 {count} 个；后加载的先回收。");
        });

        public Task ClearPoolAsync() => ExecuteAsync("清空实例池", _ => ClearPoolCoreAsync());

        public void CancelLoad()
        {
            if (_loadCancellation == null || _loadCancellation.IsCancellationRequested || _closing) return;
            StatusText.text = "正在取消加载…";
            _loadCancellation.Cancel();
            RefreshView();
        }

        private static string ReadAddress(InputField input)
        {
            var address = input.text.Trim();
            if (address.Length == 0) throw new ArgumentException("资源地址不能为空，请填写 Resources 下的相对路径（不含扩展名）。");
            return address;
        }

        private static int ReadCount(InputField input, string label)
        {
            if (!int.TryParse(input.text, out var count) || count <= 0)
                throw new ArgumentException(label + "必须是大于 0 的整数。");
            return count;
        }

        private Task ExecuteAsync(string label, Func<CancellationToken, Task> action, bool cancellable = false)
        {
            if (IsBusy || _closing) return Task.CompletedTask;
            IsBusy = true;
            StatusText.text = label + "…";
            return _operation = ExecuteCoreAsync(label, action, cancellable);
        }

        private async Task ExecuteCoreAsync(string label, Func<CancellationToken, Task> action, bool cancellable)
        {
            using var request = cancellable ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token) : null;
            _loadCancellation = request;
            var token = request?.Token ?? _lifetime.Token;
            RefreshView();
            try
            {
                await action(token);
                if (!_closing) StatusText.text = label + "完成";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (!_closing)
                {
                    StatusText.text = label + "已取消";
                    AppendLog($"{label}已取消；当前在用实例 {_instances.Count} 个，已交付内容保留。");
                }
            }
            catch (Exception error)
            {
                if (!_closing)
                {
                    StatusText.text = label + "失败：" + error.Message;
                    AppendLog(StatusText.text);
                }
                if (!(error is ArgumentException) && !(error is ResourceException)) Debug.LogException(error);
            }
            finally
            {
                IsBusy = false;
                _loadCancellation = null;
                if (!_closing) RefreshView();
            }
        }

        private void ReleaseTexture()
        {
            if (TexturePreview != null) { TexturePreview.texture = null; TexturePreview.enabled = false; }
            _texture?.Dispose();
            _texture = null;
        }

        private void ReleaseSprite()
        {
            if (SpritePreview != null) { SpritePreview.sprite = null; SpritePreview.enabled = false; }
            _sprite?.Dispose();
            _sprite = null;
        }

        private async Task ReturnInstancesCoreAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var last = _instances.Count - 1;
                _instances[last].Dispose();
                _instances.RemoveAt(last);
            }
            // Dispose 投递归还；等上下文处理与可能的帧末销毁完成后刷新快照。
            if (count > 0) await Awaitable.NextFrameAsync();
        }

        private async Task ClearPoolCoreAsync()
        {
            var result = await _system.ClearPoolAsync();
            await Awaitable.NextFrameAsync();
            AppendLog($"清池结果：销毁 {result.Destroyed}，仍在用 {result.StillActive}，失败 {result.Failed.Count}；在用实例归还时销毁。");
            foreach (var failure in result.Failed) AppendLog(failure.PrototypeKey.Address + "：" + failure.Error.Message);
        }

        private async Task UnloadIdleAsync()
        {
            var result = await _system.UnloadAsync();
            AppendLog($"卸载结果：已卸载 {result.Released.Count}，在用跳过 {result.SkippedInUse.Count}，失败 {result.Failed.Count}。");
            foreach (var failure in result.Failed) AppendLog(failure.Key.Address + "：" + failure.Error.Message);
        }

        private void AppendLog(string message)
        {
            _log.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            while (_log.Count > 6) _log.Dequeue();
            if (LogText != null) LogText.text = string.Join("\n", _log);
        }

        private void RefreshView()
        {
            var available = IsReady && !IsBusy;
            LoadTextureButton.interactable = available;
            ReleaseTextureButton.interactable = available && _texture != null;
            LoadSpriteButton.interactable = available;
            ReleaseSpriteButton.interactable = available && _sprite != null;
            LoadGameObjectButton.interactable = available;
            ReturnInstancesButton.interactable = available && _instances.Count > 0;
            CancelLoadButton.interactable = !_closing && _loadCancellation != null && !_loadCancellation.IsCancellationRequested;
            ClearPoolButton.interactable = available;
            UnloadIdleButton.interactable = available;
            ReleaseAllButton.interactable = available;
            UnloadUnityUnusedButton.interactable = available && _texture == null && _sprite == null && _instances.Count == 0;
            TextureAddressInput.interactable = available;
            SpriteAddressInput.interactable = available;
            PrefabAddressInput.interactable = available;
            LoadCountInput.interactable = available;
            ReturnCountInput.interactable = available;

            var text = new StringBuilder();
            if (_system != null)
            {
                var snapshot = _system.Snapshot();
                text.AppendLine($"缓存条目：{snapshot.Entries.Count}    残留：{snapshot.Residues.Count}");
                foreach (var entry in snapshot.Entries)
                    text.AppendLine($"{entry.Key.RequestedType.Name}  |  持有 {entry.Holds}  |  {entry.Key.Address}");
                var idle = 0;
                foreach (var pool in snapshot.Pools)
                {
                    idle += pool.Idle;
                    text.AppendLine($"池  |  在用 {pool.Active} / 闲置 {pool.Idle}{(pool.Closed ? " / 已关闭" : "")}  |  {pool.PrototypeKey.Address}");
                }
                InstanceCountText.text = $"在用 {_instances.Count}    闲置 {idle}    实例残留 {snapshot.InstanceResidues.Count}";
            }
            else
            {
                text.AppendLine("等待初始化");
                InstanceCountText.text = "在用 0    闲置 0    实例残留 0";
            }
            CacheText.text = text.ToString();
        }

        /// <summary>退出时先结束请求与实例，再归还引用并关闭本场景拥有的系统。</summary>
        public Task ShutdownAsync()
        {
            if (_shutdown != null) return _shutdown;
            _closing = true;
            _lifetime.Cancel();
            return _shutdown = ShutdownCoreAsync();
        }

        private async Task ShutdownCoreAsync()
        {
            try
            {
                await _operation;
                ReleaseTexture();
                ReleaseSprite();
                await ReturnInstancesCoreAsync(_instances.Count);
                if (_system != null) await _system.DisposeAsync();
            }
            finally
            {
                _lifetime.Dispose();
                if (_font != null) Destroy(_font);
            }
        }

        private async void OnDestroy()
        {
            try { await ShutdownAsync(); }
            catch (Exception error) { Debug.LogException(error); }
        }
    }
}
