/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 文件缓存：内容如何取得、发布、判断新鲜度与删除，以及目标类型到转换实现的注册查询。
 *                同一身份只填充一次，相同身份的并发请求等待同一次结果。
 *                填充持有自己的操作令牌：使用者的取消只结束本人的等待，只要有使用者仍在等待就不取消填充；
 *                最后一个使用者退出时关闭该填充的准入并决定取消，原执行者观察真实结束后才摘除登记，
 *                因此同键不会出现两个生产者。
 *                运行期不删除已经交付的路径；初始化时按目标容量做一次启动清理并删除残留临时文件。
 *                有效期只作用于文件获取，不影响已交付的内存产物。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 把内容写入缓存分配的临时路径；实现不得直接写最终路径 </summary>
    public delegate Task FileFill(string temporaryPath, CancellationToken cancellationToken);

    /// <summary>
    /// 文件缓存 (公开类型名保留 FileCache)。公开扩展点，可由装配创建并注入给需要共享的业务流程。
    /// </summary>
    public sealed class FileCache : IDisposable
    {
        private const string TemporarySuffix = ".tmp";

        private readonly FileCacheOptions _options;
        private readonly Dictionary<string, InflightFill> _fills = new Dictionary<string, InflightFill>();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly string _root;
        private int _initialized;
        private int _disposed;

        public FileCache(FileCacheOptions? options = null)
        {
            _options = options ?? new FileCacheOptions();
            _root = string.IsNullOrEmpty(_options.Directory)
                ? Path.Combine(Path.GetTempPath(), "toolkit-resource-cache")
                : _options.Directory!;
        }

        public string Root => _root;

        /// <summary>
        /// 建立缓存目录并做一次启动清理；重复调用只执行一次。
        /// 没有有效的启动清理目标时拒绝初始化：没有目标就没有实际生效的删除策略。
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
            try
            {
                if (_options.StartupTargetBytes is not long target || target <= 0)
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.CacheIndexFailed, LoadStage.CacheLookup, CleanupStatus.Complete, null,
                        new Dictionary<string, object>
                        {
                            { "root", _root },
                            { "reason", "启用文件缓存必须提供正的 StartupTargetBytes" },
                        }));
                }
                Directory.CreateDirectory(_root);
                await _StartupCleanupAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Volatile.Write(ref _initialized, 0);
                throw;
            }
        }

        /// <summary>
        /// 取得已发布的文件路径：命中有效文件直接返回；否则由 fill 写入临时文件，成功发布后返回最终路径。
        /// 同一身份的并发请求共享一次填充；填充失败不发布、不占用最终路径。
        /// </summary>
        public async Task<string> GetOrCreateAsync(
            FileRequest request, FileFill fill, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (fill == null) throw new ArgumentNullException(nameof(fill));
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(FileCache));
            cancellationToken.ThrowIfCancellationRequested();

            var key = _ComposeKey(request.Identity);
            var path = Path.Combine(_root, key + ".bin");
            if (_IsFresh(path, request.Validity, DateTimeOffset.UtcNow)) return path;

            InflightFill record;
            var startFill = false;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_fills.TryGetValue(key, out var existing))
                {
                    // 已有一次填充在收尾：不接受加入，独立等它真实结束后再争取新的机会。
                    record = existing;
                }
                else
                {
                    var created = new InflightFill(key, path);
                    _fills.Add(key, created);
                    record = created;
                    startFill = true;
                }
                // 加入必须在门内计数：否则"最后一个使用者退出"可能在计数归零后才被观察到。
                record.AddWaiter(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }

            if (startFill)
            {
                // 登记与计数都结束、离开门禁后才启动后端：外部代码不得嵌在状态提交栈内。
                _ = _FillAndPublishAsync(record, fill);
            }

            try
            {
                await record.Completion.Task.WaitWithCancellation(cancellationToken).ConfigureAwait(false);
                return await record.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                record.RemoveWaiter();
            }
        }
        /// <summary>
        /// 一次填充的运行：用自己的令牌驱动后端，成功发布，取消与失败都不发布。
        /// 无论哪种结束都先处理未发布文件，最后才摘除登记。
        /// </summary>
        private async Task _FillAndPublishAsync(InflightFill entry, FileFill fill)
        {
            var temporary = entry.Path + "." + Guid.NewGuid().ToString("N") + TemporarySuffix;
            try
            {
                await fill(temporary, entry.Token).ConfigureAwait(false);
                if (!File.Exists(temporary))
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.FileNotFound, LoadStage.Download, CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "path", temporary } }));
                }
                _Publish(temporary, entry.Path);
                entry.Completion.TrySetResult(entry.Path);
            }
            catch (OperationCanceledException error)
            {
                // 取消不包装成文件错误：它不代表缓存内容有问题，也不代表必须重新获取。
                _TryDelete(temporary);
                entry.Completion.TrySetCanceled(
                    error.CancellationToken.IsCancellationRequested ? error.CancellationToken : entry.Token);
            }
            catch (Exception error)
            {
                _TryDelete(temporary);
                entry.Completion.TrySetException(_AsError(error, entry.Path));
            }
            finally
            {
                // 收尾完成才摘除登记：期间到达的请求会看到这份记录并等它结束，不会另起一个生产者。
                await _gate.WaitAsync().ConfigureAwait(false);
                try { _fills.Remove(entry.Key); }
                finally { _gate.Release(); }
                entry.Terminate();
            }
        }

        private static void _Publish(string temporary, string finalPath)
        {
            try
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(temporary, finalPath);
                // 写入句柄已关闭、改名已完成，此时写入的时间才是这份文件的 TTL 基准。
                File.SetLastWriteTimeUtc(finalPath, DateTime.UtcNow);
            }
            catch (Exception error)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.FileIoFailed, LoadStage.Download, CleanupStatus.Complete, error,
                    new Dictionary<string, object> { { "path", finalPath } }));
            }
        }

        private static Exception _AsError(Exception error, string path)
        {
            if (error is ResourceLoadException) return error;
            var diagnostic = _IsDiskFull(error)
                ? DiagnosticCodes.CacheDiskFull
                : DiagnosticCodes.FileIoFailed;
            return new ResourceLoadException(new LoadError(
                diagnostic, LoadStage.Download, CleanupStatus.Unknown, error,
                new Dictionary<string, object> { { "path", path } }));
        }

        private static bool _IsDiskFull(Exception error)
        {
            // 磁盘满与普通 I/O 失败分开映射；证据不足时不猜测具体原因。
            return error is IOException
                   && (error.HResult == unchecked((int)0x80070070)
                       || error.HResult == unchecked((int)0x8007006F));
        }

        public async Task DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            List<InflightFill> fills;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                fills = new List<InflightFill>(_fills.Values);
                _fills.Clear();
            }
            finally
            {
                _gate.Release();
            }
            // 退出时才取消尚未结束的填充；进行中的临时文件由下次启动清理处理。
            foreach (var fill in fills) fill.Cancel();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _gate.Dispose();
        }

        #region 有效期、启动清理与校验

        /// <summary>
        /// 文件是否仍然新鲜。基准是这份文件自己的最后写入时间 (发布时刻)，期限来自本次请求。
        /// 不保存"标记过期"状态，也不因为过期就删除文件。
        /// </summary>
        private static bool _IsFresh(string path, FileValidity validity, DateTimeOffset now)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var file = new FileInfo(path);
                if (file.Length <= 0) return false;
                if (validity.ExpiresAt is not DateTimeOffset expiresAt) return true;
                var publishedAt = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                return now < publishedAt + (expiresAt - publishedAt);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private async Task _StartupCleanupAsync(long target, CancellationToken cancellationToken)
        {
            var files = new DirectoryInfo(_root).GetFiles("*", SearchOption.TopDirectoryOnly);
            long total = 0;
            var published = new List<FileInfo>();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Name.EndsWith(TemporarySuffix, StringComparison.Ordinal))
                {
                    _TryDelete(file.FullName); // 上次进程中断留下的临时文件
                    continue;
                }
                total += file.Length;
                published.Add(file);
            }
            if (total <= target) return;

            foreach (var file in published.OrderBy(f => f.LastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total <= target) return;
                var length = file.Length;
                if (_TryDelete(file.FullName)) total -= length;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static bool _TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        #endregion

        /// <summary> 缓存键按结构化字段派生 (逐字段长度前缀)，不依赖展示字符串 </summary>
        private static string _ComposeKey(FileIdentity identity)
        {
            var builder = new StringBuilder();
            _AppendField(builder, identity.Source);
            _AppendField(builder, identity.Path);
            _AppendField(builder, identity.Revision);
            _AppendField(builder, identity.Variant);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            var key = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) key.Append(b.ToString("x2"));
            return key.ToString();
        }

        private static void _AppendField(StringBuilder builder, string value)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        }

        /// <summary>
        /// 同一身份的一次共享填充：自己的令牌源、自己的等待者计数。
        /// 使用者的取消只结束本人的等待，**不取消已经开始的一次填充**：填充的结果对后来者同样有用，
        /// 取消它只会让"随后到达的表示"要么继承别人的取消，要么重复下载一次。
        /// 因此这里的令牌只用于缓存退出，与任何单个使用者的令牌无关。
        /// </summary>
        private sealed class InflightFill
        {
            private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
            private int _waiters;

            internal InflightFill(string key, string path)
            {
                Key = key;
                Path = path;
                Completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Token = _cancellation.Token;
            }

            /// <summary> 登记用的身份键；收尾时按它摘除，不靠文件路径反推 </summary>
            internal string Key { get; }

            internal string Path { get; }

            /// <summary> 这次填充自己的操作令牌；只在缓存退出时取消，不跟随任何使用者 </summary>
            internal CancellationToken Token { get; }

            internal TaskCompletionSource<string> Completion { get; }

            /// <summary> 加入本次填充；在文件缓存门禁内调用，保证计数与登记同序 </summary>
            internal void AddWaiter(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _waiters);
            }

            /// <summary> 退出本次等待；只减少计数，不取消已经开始的一次填充 </summary>
            internal void RemoveWaiter()
            {
                Interlocked.Decrement(ref _waiters);
            }

            /// <summary> 当前仍在等待的使用者数；A13 的共享语义用它做诊断 </summary>
            internal int WaiterCount => Volatile.Read(ref _waiters);

            /// <summary> 缓存退出时才取消尚未结束的填充 </summary>
            internal void Cancel()
            {
                try { _cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            internal void Terminate() => _cancellation.Dispose();
        }
    }

    /// <summary> 文件转换器注册表；公开类型名保留 FileResourceConverters </summary>
    public sealed class FileResourceConverters
    {
        private readonly Dictionary<Type, IFileResourceConverter> _converters =
            new Dictionary<Type, IFileResourceConverter>();

        /// <summary> 注册或替换某个目标类型的转换器 </summary>
        public void Register(Type resultType, IFileResourceConverter converter)
        {
            if (resultType == null) throw new ArgumentNullException(nameof(resultType));
            if (converter == null) throw new ArgumentNullException(nameof(converter));
            _converters[resultType] = converter;
        }

        /// <summary> 按目标类型精确查询；未注册时抛出可诊断错误 </summary>
        public IFileResourceConverter Get(Type resultType)
        {
            if (resultType == null) throw new ArgumentNullException(nameof(resultType));
            if (_converters.TryGetValue(resultType, out var converter)) return converter;
            throw new ResourceLoadException(new LoadError(
                DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Resolve, CleanupStatus.Complete, null,
                new Dictionary<string, object> { { "resultType", resultType.Name } }));
        }

        /// <summary> 内置实现：byte[] 与 string </summary>
        public static FileResourceConverters CreateDefault()
        {
            var converters = new FileResourceConverters();
            converters.Register(typeof(byte[]), new BytesFileConverter());
            converters.Register(typeof(string), new TextFileConverter());
            return converters;
        }
    }

    /// <summary> 共同文件 I/O：把文件系统异常映射为带阶段与路径的诊断错误 </summary>
    internal static class ResourceFileIo
    {
        internal static byte[] ReadAllBytes(string path, LoadStage stage)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new ResourceLoadException(new LoadError(
                    _DiagnosticOf(error), stage, CleanupStatus.Complete, error,
                    new Dictionary<string, object> { { "path", path } }));
            }
        }

        private static string _DiagnosticOf(Exception error)
        {
            if (error is FileNotFoundException || error is DirectoryNotFoundException)
                return DiagnosticCodes.FileNotFound;
            if (error is UnauthorizedAccessException)
                return DiagnosticCodes.FileAccessDenied;
            return DiagnosticCodes.FileIoFailed;
        }
    }

    /// <summary> byte[]：直接读取整个文件 </summary>
    public sealed class BytesFileConverter : IFileResourceConverter
    {
        public Task<LoadedAsset> ConvertAsync(
            string path, Type resultType, string? conversionKey, CancellationToken cancellationToken)
        {
            if (resultType != typeof(byte[]))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, CleanupStatus.Complete,
                    null, new Dictionary<string, object> { { "resultType", resultType.Name } }));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = ResourceFileIo.ReadAllBytes(path, LoadStage.ReadFile);
            return Task.FromResult(LoadedAsset.FromUnmanaged(bytes));
        }
    }

    /// <summary> string：按 UTF-8 读取文本；换行保持文件原样，不做平台归一化 </summary>
    public sealed class TextFileConverter : IFileResourceConverter
    {
        public Task<LoadedAsset> ConvertAsync(
            string path, Type resultType, string? conversionKey, CancellationToken cancellationToken)
        {
            if (resultType != typeof(string))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, CleanupStatus.Complete,
                    null, new Dictionary<string, object> { { "resultType", resultType.Name } }));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = ResourceFileIo.ReadAllBytes(path, LoadStage.ReadFile);
            return Task.FromResult(LoadedAsset.FromUnmanaged(Encoding.UTF8.GetString(bytes)));
        }
    }
}
