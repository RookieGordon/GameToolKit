# ResourceSystem

跨 Unity 项目的通用资源加载框架：内置本地文件、远程文件、Unity Resources 三种来源，AssetBundle 提供统一加载契约由业务接入。
设计依据见 `Documents/Discussion/资源加载系统代码审阅/资源加载系统需求.md`（v6 定稿）及 `Docs/ResourceSystemRewrite/资源系统设计纠错记录.md`。

## 核心模型与并发规则

核心以**资源键生命周期**作为唯一状态中心，不再叠加通用工单/工作线程池组件：

```
ResourceSystem.LoadAsync<T>(source, address, progress, token, parameters)
  → ResourceKey                身份 = 来源 + 地址 + 目标类型 + 参数(Equals)
  → ResourceStore.AcquireAsync 单一监视器：_ready（可用表）与 _loading（加载中表）互斥
      ├─ 命中 _ready           同一把锁内 Holds++，零分配直接返回
      └─ 命中或创建 LoadOperation（主动对象 Active Object）
           → SourceLane.ProduceAsync：来源信号量限流 → 执行上下文内调用 ILoader → 必要时转换 → 原子提交或清理
  → ResourceRef<T>             独立引用凭证；Dispose 幂等且任意线程立即返回；Retain 同步追加独立持有
  → ResourceSystem.UnloadAsync 锁内摘除 Holds == 0 的条目取得唯一释放权，锁外经执行上下文释放，失败记入残留
```

三条并发不变量：
1. **单监视器（Monitor Object）**：`ResourceStore`、`InstancePool`、`FileCache` 各自一把锁串行状态；锁内只改表与计数，不 await、不回调、不调引擎。
2. **锁内决定、锁外执行**：Future 完成通知、进度回调、引擎加载/转换/释放均在锁外进行。
3. **摘除即所有权**：从等待链表摘除取得唯一结算权；从 `_ready` 摘除取得唯一释放权；从池闲置队列取出取得实例处置权。无需 `Indexed` / `Unloading` 状态标志或 `Outcome` 异常抢占包装。

## 文件与职责（核心执行 10 份）

| 文件 | 职责 | 分类 |
| --- | --- | --- |
| `ResourceSystem.cs` | 外观：配置快照、幂等可重试初始化、来源注册与替换、对外入口、退出编排 | 核心 1 |
| `Core/ResourceStore.cs` | 状态中心：`_loading`/`_ready` 两表互斥、命中、持有、归还、卸载、残留、停止与快照 | 核心 2 |
| `Core/LoadOperation.cs` | 单键加载主动对象：有序等待者、Q01 最早承担者接续、原子批量交付、进度广播与回放 | 核心 3 |
| `Core/SourceLane.cs` | 来源通道：信号量限流、执行上下文内调用加载器与转换器、未交付产物清理 | 核心 4 |
| `Core/ResourceRef.cs` | `ResourceEntry`、`ResourceResidue`、`UnloadResult`、`ResourceRef<T>` | 核心 5 |
| `Pool/InstancePool.cs` | 实例池：分桶、准入、租约、非阻塞归还、清池、通过 `ResourceRef` 聚合持有原型 | 核心 6 |
| `Files/FileCache.cs` | 远程落盘缓存：按 URL 共享填充、临时文件改名发布、TTL 基准、启动容量清理 | 核心 7 |
| `Files/SharedOperation.cs` | 值结果共享原语：同 key 多等一、无人等待取消、旧轮收尾后开新轮、进度回放 | 核心 8 |
| `Loaders/FileLoaders.cs` | `LocalFileLoader`、`RemoteFileLoader`（含 `UseDownloader`、`AddRemoteFiles`）、`TextConverter` | 核心 9 |
| `UnityToolKit/.../ResourcesLoader.cs` | Unity Resources 来源加载 | 核心 10 |
| `Definition/*`（4 份） | `ResourceContracts`（身份/进度/产物/扩展接口）、`ResourceSystemOptions`、`IExecutionContext`、`ResourceException` | 定义 |
| `UnityToolKit/...`（接入与配置） | `UnityExecutionContext`、`GameObjectFactory`、`TextureConverter`、`ResourceSystemConfig`（`LoaderConfig.Install` 模板方法）、`RemoteFileLoaderConfig`、`ResourceManager` | Unity 接入 |

## 线程与契约要点

- **执行上下文亲和**：`ILoader.LoadAsync`、`IProductConverter.ConvertAsync`、`IInstanceFactory` 及 `IResource.Release` 均由框架在 `IExecutionContext`（Unity 下为主线程）内启动或执行；加载器不再接收 `IExecutionContext` 参数，也无需自行做“主线程 → 线程池 → 主线程”往返切换。文件与网络加载器内部使用异步 IO，不阻塞主线程。
- **Q01 接续语义**：每轮尝试开始时选定当前最早等待者为承担者。本轮后端失败仅结算该承担者；若承担者已提前取消，失败不会转嫁给剩余等待者，剩余等待者在同一驱动循环内自动进入下一轮尝试。
- **原子交付**：一轮加载成功时，在同一临界区内摘除全部等待者、以等待者人数初始化 `Holds` 并写入 `_ready`，不存在“已入库但 `Holds == 0` 被并发卸载抢走”的窗口。
- **外部加载器后注册与策略快照**：`LoaderConfig.InitializeOnStartup` 控制启动时是否通过 `Install(options)` 创建实例；关闭时仅经 `Config → Options → ResourceSystem` 保存来源并发与超时快照，外部系统就绪后调用 `RegisterLoader` 自动沿用该快照（显式传入策略仅覆盖当次注册）。同来源多项配置以最后整项为准。
- **所有权边界**：通过 `RemoteFileLoaderConfig` 启动装配时，自建的 `FileCache` 与 `SimpleDownloader` 分别挂入 `Initializers` 与 `ShutdownActions`，随系统初始化清理、随系统退出关闭；业务手动创建的外部缓存与下载器仅被借用，生命周期归创建者。

## 最小装配示例（纯代码）

```csharp
var context = new UnityExecutionContext(); // Unity 主线程创建
var downloader = new SimpleDownloader(maxConcurrency: 2);
var cache = new FileCache(
    new HashedPathConverter(Path.Combine(Application.persistentDataPath, "rs")),
    RemoteFileLoader.UseDownloader(downloader),
    startupTargetBytes: 512L << 20,
    maxAge: TimeSpan.FromDays(7));

var options = new ResourceSystemOptions { ExecutionContext = context, InstanceFactory = new GameObjectFactory() }
    .AddLoader(ResourceSource.Resources, new ResourcesLoader())
    .AddLoader(ResourceSource.Local, new LocalFileLoader(Application.streamingAssetsPath))
    .AddConverter<string>(new TextConverter())
    .AddConverter<Texture2D>(new TextureConverter());
RemoteFileLoader.AddRemoteFiles(options, cache);

await using var system = new ResourceSystem(options);
await system.InitializeAsync();

using (var icon = await system.LoadAsync<Texture2D>(ResourceSource.Remote, "https://cdn/x/icon.png")) { /* icon.Value */ }
using (var enemy = await system.RentAsync<GameObject>(ResourceSource.Resources, "Prefabs/Enemy")) { /* enemy.Value */ }
await system.UnloadAsync(key => key.Source == ResourceSource.Remote);
```

## 验证命令

```powershell
dotnet test ToolKit/Tests/Tests.csproj --filter "FullyQualifiedName~ResourceSystem"
dotnet build ToolKit/Tests/UnityAdapterCompileCheck/UnityAdapterCompileCheck.csproj
```
