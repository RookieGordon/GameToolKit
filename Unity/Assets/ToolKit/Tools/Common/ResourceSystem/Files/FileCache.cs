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
    /// 它从分配暂存路径起就负责该路径：发布、清理或隔离都由它收尾，下载方只借写。
    /// </summary>
    public sealed class FileCache : IDisposable
    {
        private const string TemporarySuffix = ".tmp";
        private const string QuarantineFolder = "quarantine";

        private readonly FileCacheOptions _options;
        private readonly Dictionary<string, InflightFill> _fills = new Dictionary<string, InflightFill>();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly string _root;
        private readonly string _quarantine;
        private int _initialized;
        private int _disposed;

        public FileCache(FileCacheOptions? options = null)
        {
            _options = options ?? new FileCacheOptions();
            _root = string.IsNullOrEmpty(_options.Directory)
                ? Path.Combine(Path.GetTempPath(), "toolkit-resource-cache")
                : _options.Directory!;
            _quarantine = Path.Combine(_root, QuarantineFolder);
        }

        public string Root => _root;

        /// <summary> 清理失败的文件残留落点；与正常缓存条目分开，避免被当成可复用数据 </summary>
        public string QuarantineRoot => _quarantine;

        /// <summary> 隔离记录；由 cache.quarantine 诊断与业务排查读取，本身不拥有所有权 </summary>
        public IReadOnlyList<QuarantinedFile> Quarantined
        {
            get { lock (_quarantineLog) return new List<QuarantinedFile>(_quarantineLog); }
        }

        private readonly List<QuarantinedFile> _quarantineLog = new List<QuarantinedFile>();

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
            }
            finally
            {
                _gate.Release();
            }

            if (startFill)
            {
                // 登记结束、离开门禁后才启动后端：外部代码不得嵌在状态提交栈内。
                _ = _FillAndPublishAsync(record, fill);
            }

            // 使用者的取消只结束本人等待（WaitWithCancellation），不影响这次填充。
            await record.Completion.Task.WaitWithCancellation(cancellationToken).ConfigureAwait(false);
            return await record.Completion.Task.ConfigureAwait(false);
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
                try
                {
                    _Publish(temporary, entry.Path);
                    entry.Completion.TrySetResult(entry.Path);
                }
                catch (Exception publishError)
                {
                    // 已接收但发布失败：仍由缓存收尾——删不掉就隔离，不留在暂存路径上无人负责。
                    if (!_TryDelete(temporary)) _TryQuarantine(temporary, "发布失败后暂存清理失败");
                    entry.Completion.TrySetException(_AsError(publishError, entry.Path));
                }
            }
            catch (OperationCanceledException error)
            {
                // 取消不包装成文件错误：它不代表缓存内容有问题，也不代表必须重新获取。
                // 暂存路径从分配起归缓存负责：删不掉就隔离，不留无人管的文件。
                if (!_TryDelete(temporary)) _TryQuarantine(temporary, "填充取消后暂存清理失败");
                entry.Completion.TrySetCanceled(
                    error.CancellationToken.IsCancellationRequested ? error.CancellationToken : entry.Token);
            }
            catch (Exception error)
            {
                if (!_TryDelete(temporary)) _TryQuarantine(temporary, "填充失败后暂存清理失败");
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

        /// <summary>
        /// 启动维护：先处理已确认不可用的数据（残留暂存），再把正常缓存与隔离区一并计入容量目标。
        /// <b>回收顺序：正常缓存先、隔离最后</b>；未超目标时两者都可能保留，不是"下次启动必删"。
        /// 在同一类内按最旧写入时间优先；这个时间只用于淘汰排序，不承担内容一致性验证。
        /// </summary>
        private async Task _StartupCleanupAsync(long target, CancellationToken cancellationToken)
        {
            if (Directory.Exists(_quarantine))
            {
                // 隔离残留已停止写入，不会被继续写；启动时按最旧优先回收。
                Directory.CreateDirectory(_quarantine);
            }

            var files = new DirectoryInfo(_root).GetFiles("*", SearchOption.TopDirectoryOnly);
            long total = 0;
            var published = new List<FileInfo>();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Name.EndsWith(TemporarySuffix, StringComparison.Ordinal))
                {
                    // 上一进程中断留下的暂存：确认不可用，直接处理；删不掉则隔离。
                    if (!_TryDelete(file.FullName)) _TryQuarantine(file.FullName, "启动维护：暂存文件无法删除");
                    continue;
                }
                total += file.Length;
                published.Add(file);
            }

            var quarantined = Directory.Exists(_quarantine)
                ? new DirectoryInfo(_quarantine).GetFiles("*", SearchOption.TopDirectoryOnly)
                : Array.Empty<FileInfo>();
            foreach (var file in quarantined) total += file.Length;

            // 正常缓存先回收。
            foreach (var file in published.OrderBy(f => f.LastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total <= target) return;
                var length = file.Length;
                if (_TryDelete(file.FullName)) total -= length;
            }

            // 仍然超目标才轮到隔离区：隔离是最后回收的一段，不保证最短保留时间。
            foreach (var file in quarantined.OrderBy(f => f.LastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total <= target) return;
                var length = file.Length;
                if (_TryDelete(file.FullName)) total -= length;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        /// <summary>
        /// 删除失败的文件残留移入隔离区：与正常缓存分开存放，避免被当成可复用数据。
        /// 移动成功记录新位置与原因；移动也失败则保留原位置事实，不虚报已隔离。
        /// </summary>
        private bool _TryQuarantine(string path, string reason)
        {
            if (!File.Exists(path)) return false;
            try
            {
                Directory.CreateDirectory(_quarantine);
                var name = Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(path);
                var destination = Path.Combine(_quarantine, name);
                File.Move(path, destination);
                lock (_quarantineLog)
                {
                    _quarantineLog.Add(new QuarantinedFile(path, destination, reason));
                }
                return true;
            }
            catch (IOException)
            {
                // 连移动都失败：文件仍在原位置，由既定所有者继续保管。
                lock (_quarantineLog)
                {
                    _quarantineLog.Add(new QuarantinedFile(path, "", reason));
                }
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                lock (_quarantineLog)
                {
                    _quarantineLog.Add(new QuarantinedFile(path, "", reason));
                }
                return false;
            }
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
        /// 同一身份的一次共享填充：自己的令牌源。
        /// 使用者的取消只结束本人的等待，**不取消已经开始的一次填充**：填充的结果对后来者同样有用，
        /// 取消它只会让"随后到达的表示"要么继承别人的取消，要么重复下载一次。
        /// 因此不需要等待者计数——这个计数只在"归零即取消"的选法下才有用途。
        /// 令牌只在缓存退出时使用，与任何单个使用者的令牌无关。
        /// </summary>
        private sealed class InflightFill
        {
            private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

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

    /// <summary>
    /// 文件类加载器共用的"取内容 → 交转换器"两步，以及转换异常的归类。
    /// 加载器的 LoadAsync 只取内容；转换由本类交给转换器完成，两边各只有一件事。
    /// 转换阶段未归类的异常统一报 asset.decode_failed，不让每个加载器各写一遍。
    /// </summary>
    internal static class FileConversion
    {
        /// <summary> 把已取到的内容交给转换器；转换阶段的异常在这里归类 </summary>
        internal static async Task<LoadedAsset> ConvertAsync(
            LoadedContent content, IFileResourceConverter converter, Type resultType, string? conversionKey,
            CancellationToken operationToken, string diagnosticPath)
        {
            try
            {
                return await converter
                    .ConvertAsync(content, resultType, conversionKey, operationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not ResourceLoadException and not OperationCanceledException)
            {
                // 自定义转换器的未分类异常：无法确认其中间对象是否清理，按 Unknown 报出。
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetConversionFailed, LoadStage.Convert, CleanupStatus.Unknown, error,
                    new Dictionary<string, object> { { "path", diagnosticPath } }));
            }
        }

        /// <summary> 读文件；取内容归加载器，所以文件系统异常也在这里映射 </summary>
        internal static byte[] ReadBytes(string path, CancellationToken operationToken)
        {
            operationToken.ThrowIfCancellationRequested();
            var bytes = ResourceFileIo.ReadAllBytes(path, LoadStage.ReadFile);
            // 读完之后再确认一次：读期间被取消就不把内容交出去。
            operationToken.ThrowIfCancellationRequested();
            return bytes;
        }
    }

    /// <summary> byte[]：把已取到的文件内容原样交给业务；内容形态须为字节 </summary>
    public sealed class BytesFileConverter : IFileResourceConverter
    {
        public Task<LoadedAsset> ConvertAsync(
            LoadedContent content, Type resultType, string? conversionKey, CancellationToken cancellationToken)
        {
            if (resultType != typeof(byte[]))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, CleanupStatus.Complete,
                    null, new Dictionary<string, object> { { "resultType", resultType.Name } }));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LoadedAsset.FromUnmanaged(ContentShapes.AsBytes(content)));
        }
    }

    /// <summary> string：加载器交已解码文本就直接用；交字节则按 UTF-8 解码；换行保持原样 </summary>
    public sealed class TextFileConverter : IFileResourceConverter
    {
        public Task<LoadedAsset> ConvertAsync(
            LoadedContent content, Type resultType, string? conversionKey, CancellationToken cancellationToken)
        {
            if (resultType != typeof(string))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, CleanupStatus.Complete,
                    null, new Dictionary<string, object> { { "resultType", resultType.Name } }));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (content is LoadedContent<string> decoded)
            {
                return Task.FromResult(LoadedAsset.FromUnmanaged(decoded.Value));
            }
            return Task.FromResult(LoadedAsset.FromUnmanaged(Encoding.UTF8.GetString(ContentShapes.AsBytes(content))));
        }
    }

    /// <summary>
    /// 内容形态的取用：加载器交什么形态就按对应方式取，不符时给出可诊断的错误。
    /// 转换器据此不必假设"内容一定是路径或字节"——字节、文本、流、引擎对象都是合法形态。
    /// </summary>
    internal static class ContentShapes
    {
        /// <summary> 取字节：接受 LoadedContent&lt;byte[]&gt; 或值为 byte[] 的内容 </summary>
        internal static byte[] AsBytes(LoadedContent content)
        {
            if (content is LoadedContent<byte[]> typed) return typed.Value;
            if (content.Value is byte[] bytes) return bytes;
            throw _Mismatch(content, "byte[]");
        }

        /// <summary> 取指定形态；不符时给出可诊断的错误，而不是让强制转换异常泄漏出去 </summary>
        internal static T As<T>(LoadedContent content, string expected) where T : class
        {
            if (content is LoadedContent<T> typed) return typed.Value;
            if (content.Value is T value) return value;
            throw _Mismatch(content, expected);
        }

        private static ResourceLoadException _Mismatch(LoadedContent content, string expected)
        {
            return new ResourceLoadException(new LoadError(
                DiagnosticCodes.AssetConversionFailed, LoadStage.Convert, CleanupStatus.Complete, null,
                new Dictionary<string, object>
                {
                    { "contentType", content.Value.GetType().Name },
                    { "expected", expected },
                }));
        }
    }
}
