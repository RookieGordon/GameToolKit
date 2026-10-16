/*
 * 资源系统测试替身：可控加载器、可控实例工厂、计数诊断、可推进的假时钟。
 * 这些替身只表达测试需要观察的契约：调用次数、释放次数、存活与生命周期。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class FakeProduct
    {
        public FakeProduct(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public bool Released { get; internal set; }
    }

    /// <summary>
    /// 可控加载器：解析身份、实际加载次数、可注入的失败与阻塞。
    /// 测试替身把"加载 + 转换"合在一起实现（加载器持有转换器），
    /// 因此 OnLoad 仍直接交产物；转换步骤只是把内容原样交出——与内置文件来源的分工一致。
    /// </summary>
    public sealed class FakeLoader : IResourceLoader, IConvertingLoader
    {
        private int _resolveCount;
        private int _loadCount;

        public FakeLoader(string prefix = "fake")
        {
            Prefix = prefix;
        }

        public string Prefix { get; }

        /// <summary> 每次解析时调用；可用于注入解析失败 </summary>
        public Action<ResourceRequest>? OnResolve { get; set; }

        /// <summary> 每次加载时调用；可用于注入加载失败、阻塞或自定义产物 </summary>
        public Func<ResolvedResource, CancellationToken, Task<LoadedAsset>>? OnLoad { get; set; }

        /// <summary> 转换步骤覆盖；默认把加载内容当作最终资产 </summary>
        public Func<LoadedContent, Type, CancellationToken, Task<LoadedAsset>>? OnConvert { get; set; }

        public int ResolveCount => Volatile.Read(ref _resolveCount);

        public int LoadCount => Volatile.Read(ref _loadCount);

        public List<string> LoadedKeys { get; } = new List<string>();

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _resolveCount);
            lock (ResolutionKeys) ResolutionKeys.Add(request.Address);
            cancellationToken.ThrowIfCancellationRequested();
            OnResolve?.Invoke(request);
            var localKey = request.Address;
            return Task.FromResult(new ResolvedResource(localKey, request.RequestedType, localKey));
        }

        public List<string> ResolutionKeys { get; } = new List<string>();

        public async Task<LoadedContent> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var count = Interlocked.Increment(ref _loadCount);
            lock (LoadedKeys) LoadedKeys.Add(resource.LocalKey);
            if (OnLoad != null)
            {
                // 替身把产物直接作为内容交出：转换步骤再原样返回它。
                return new LoadedContent(await OnLoad(resource, operationToken).ConfigureAwait(false));
            }

            operationToken.ThrowIfCancellationRequested();
            var value = new FakeProduct(resource.LocalKey);
            return new LoadedContent(value, releaseAsync: () =>
            {
                value.Released = true;
                return Task.CompletedTask;
            });
        }

        async Task<LoadedAsset> IConvertingLoader.ConvertAsync(
            LoadedContent content, ResolvedResource resolved, CancellationToken operationToken)
        {
            try
            {
                if (OnConvert != null) return await OnConvert(content, resolved.RepresentationType, operationToken)
                    .ConfigureAwait(false);
                if (content.Value is LoadedAsset asset)
                {
                    // 用例注入的产物自带存活与释放语义：原样交出。
                    if (!resolved.RepresentationType.IsInstanceOfType(asset.Value))
                    {
                        throw new ResourceLoadException(new LoadError(
                            DiagnosticCodes.AssetTypeMismatch, LoadStage.Convert, CleanupStatus.Complete, null,
                            new Dictionary<string, object>
                            {
                                { "requestedType", resolved.RepresentationType.Name },
                                { "actualType", asset.Value.GetType().Name },
                            }));
                    }
                    return asset;
                }
                operationToken.ThrowIfCancellationRequested();
                return LoadedAsset.FromContent(content);
            }
            catch (Exception)
            {
                // 转换没有交出资产：已取到的内容仍归本加载器，必须在这里释放，不能留下无人负责的产物。
                await content.ReleaseAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    public sealed class FakeInstance
    {
        public FakeInstance(int id)
        {
            Id = id;
        }

        public int Id { get; }

        public bool Destroyed { get; internal set; }

        public bool RentReset { get; internal set; }

        public bool ReturnReset { get; internal set; }
    }

    /// <summary> 可控实例工厂：原型请求映射、创建/销毁次数、可注入的失败与存活判断 </summary>
    public sealed class FakeInstanceFactory : IInstanceFactory
    {
        private int _createCount;
        private int _destroyCount;

        public string InstanceKey { get; set; } = "default";

        public Func<object, Type, bool>? OnCanCreate { get; set; }

        public Func<object, CancellationToken, Task<object>>? OnCreate { get; set; }

        public Func<object, bool>? OnIsAlive { get; set; }

        public Func<object, Task>? OnDestroy { get; set; }

        public int CreateCount => Volatile.Read(ref _createCount);

        public int DestroyCount => Volatile.Read(ref _destroyCount);

        public List<object> Created { get; } = new List<object>();

        public List<object> Destroyed { get; } = new List<object>();

        public ResourceRequest GetPrototypeRequest(ResourceRequest instanceRequest) =>
            new ResourceRequest(instanceRequest.Source, instanceRequest.Address, typeof(FakeProduct),
                instanceRequest.Parameters);

        public string GetInstanceKey(ResourceRequest instanceRequest) => InstanceKey;

        public bool CanCreate(object prototype, Type instanceType) =>
            OnCanCreate?.Invoke(prototype, instanceType) ?? instanceType == typeof(FakeInstance);

        public async Task<object> CreateAsync(
            object prototype, ResourceRequest creationRequest, CancellationToken operationToken)
        {
            var id = Interlocked.Increment(ref _createCount);
            if (OnCreate != null) return await OnCreate(prototype, operationToken).ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            var instance = new FakeInstance(id);
            lock (Created) Created.Add(instance);
            return instance;
        }

        public bool IsAlive(object instance) =>
            OnIsAlive?.Invoke(instance) ?? (instance is FakeInstance alive && !alive.Destroyed);

        public void OnRent(object instance)
        {
            if (instance is FakeInstance typed) typed.RentReset = true;
        }

        public void OnReturn(object instance)
        {
            if (instance is FakeInstance typed) typed.ReturnReset = true;
        }

        public async Task DestroyAsync(object instance)
        {
            Interlocked.Increment(ref _destroyCount);
            lock (Destroyed) Destroyed.Add(instance);
            if (OnDestroy != null)
            {
                await OnDestroy(instance).ConfigureAwait(false);
                return;
            }
            if (instance is FakeInstance typed) typed.Destroyed = true;
        }
    }

    /// <summary> 计数诊断：可断言框架报出的最终失败 </summary>
    public sealed class RecordingDiagnostics : IResourceDiagnostics
    {
        public List<LoadError> Errors { get; } = new List<LoadError>();

        public void Report(LoadError error)
        {
            lock (Errors) Errors.Add(error);
        }

        public bool Contains(string diagnosticCode)
        {
            lock (Errors)
            {
                foreach (var error in Errors)
                {
                    if (error.DiagnosticCode == diagnosticCode) return true;
                }
            }
            return false;
        }
    }

    /// <summary> 可推进的假时钟：让闲置寿命与超时在测试中确定发生 </summary>
    public sealed class FakeClock
    {
        private double _seconds;

        public double Now => Volatile.Read(ref _seconds);

        public void Advance(TimeSpan delta) => Volatile.Write(ref _seconds, _seconds + delta.TotalSeconds);
    }
}
