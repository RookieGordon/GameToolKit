# ResourceSystem

跨 Unity 项目的通用资源加载：本地文件 / 远程文件 / Unity Resources 内置，AssetBundle 只给契约。
需求与取舍见 `Documents/Discussion/资源加载系统代码审阅/资源加载系统需求.md`（v6 定稿）。

## 主流程（从入口顺着读）

```
ResourceSystem.LoadAsync<T>(source, address, parameters)
  → ResourceKey                      身份 = 来源 + 地址 + 目标类型 + 参数(Equals)
  → LoadManager.AcquireAsync         命中缓存直接取一份持有；否则进入 WorkCenter
      WorkCenter = SharedOperation   同键多等一：一轮实际加载，所有等待者共享结果
                 + TicketScheduler   来源并发名额 (LoaderPolicy.MaxConcurrentLoads)
      一轮 = ILoader.LoadAsync → 类型不符时 IProductConverter (执行上下文内) → ResourceCache.Register
  → ResourceRef<T>                   一个业务的一份持有；Dispose 归还即失效；Retain 得到独立凭证
  → ResourceSystem.UnloadAsync       业务选择卸载：无人持有的才释放，失败记残留、不封锁键
```

实例：`RentAsync<T>` → 按工厂的 `PrototypeType` 走上面的主流程取原型 → `InstancePool` 桶接纳（有闲置复用、无闲置创建）→ `InstanceLease<T>`；
`ClearPoolAsync` 关桶：销毁闲置，已有租约归还即销毁，全部对象结束后归还原型。

远程：`RemoteFileLoader` → `FileCache.GetFileAsync`（新鲜文件直接命中；否则同 URL 只有一个填充者，写临时文件 → 设最后写入时间 → 改名发布）→ 读 byte[] → 转换。

## 文件与职责

| 文件 | 行 | 职责 |
| --- | ---: | --- |
| `ResourceSystem.cs` | 172 | 入口：配置快照、初始化（幂等可重试）、Load/Preload/Rent/Unload/ClearPool/Snapshot/DisposeAsync |
| `Core/LoadManager.cs` | 162 | 一个来源：命中/合并/名额/一轮加载/转换/登记；失败接续（Q01）；超时 |
| `Cache/ResourceCache.cs` | 192 | 复用索引 + 所有权集合 + 持有计数 + 卸载与残留，一把锁 |
| `Cache/ResourceRef.cs` | 54 | 引用凭证：Value / Retain / Dispose |
| `Pool/InstancePool.cs` | 334 | 桶、准入、租约、归还、清池、原型聚合持有、销毁残留 |
| `Files/FileCache.cs` | 158 | 共享填充、临时文件发布、TTL 基准、启动清理、默认路径映射 |
| `Loaders/LocalFileLoader.cs` | 83 | 本地文件 → byte[]；内置 byte[] → string 转换 |
| `Loaders/RemoteFileLoader.cs` | 90 | 借用文件缓存 → byte[]；`UseDownloader` 适配 SimpleDownloader；`AddRemoteFiles` 装配便利 |
| `UnityToolKit/Runtime/Resource/ResourcesLoader.cs` | 58 | Unity Resources 来源 |
| `UnityToolKit/Runtime/Resource/ToolKitResourceAdapters.cs` | 154 | 主线程执行上下文、GameObject 工厂、Texture2D 转换（接入，不编排） |
| `SharedWorkSystem/*` | 709 | 通用组件：SharedOperation（多等一）、TicketScheduler（M 消费者 K 名额）、WorkCenter（组合） |
| `Definition/*` | 560 | ResourceContracts（身份/产物/四个接口）、Options、IExecutionContext、ResourceException |

核心执行 9 份（含 ResourcesLoader），接入 1 份，通用组件 4 份，定义 4 份。

## 线程规则

- 缓存、池、引用计数全部用各自的一把锁串行；`Dispose()` 归还在任意线程立即返回，不等主线程。
- `IExecutionContext` 只负责把引擎调用（转换、Create/OnRent/OnReturn/Destroy、Release）送到正确线程：
  Unity 用 `UnityExecutionContext`（借主线程 SynchronizationContext，无需每帧 Tick），测试/控制台用 `ImmediateExecutionContext`。
- `ILoader.LoadAsync` 在线程池线程上被调用，签名里带着 `IExecutionContext`：需要引擎 API 的加载器（Resources、业务的 AssetBundle 加载器）用它切到主线程，`ResourcesLoader` 是例子；文件类加载器忽略它。
- `ILoader.LoadAsync` 收到的 token 是"这一轮"的取消（最后一个等待者退出时触发），加载器必须观察它，否则 `DisposeAsync` 会一直等它退出。

## 已选定的行为（与需求 v6 的对应）

- R04：归还幂等，归还后本凭证立即失效，不因他人持有而恢复；`Retain` 是同步调用（状态由锁保护，不需要串行域投递）。
- Q01 接续：一轮失败只结算一个承担者，其他等待者自动进入下一轮，不重新调用。承担者 = 最先观察到失败的等待者（不是最早加入者）。
- R05 迟到结果：本轮被放弃后加载器仍返回的产物由本轮直接 Release；已登记进缓存的无人产物留在缓存（可卸载）。
- R06：核心不叠加隐式重试，`LoaderPolicy` 只有并发与超时；传输重试归 SimpleDownloader。
- Q02：`FileCache` 必须给出大于 0 的 `startupTargetBytes`，初始化时删临时文件并按最后写入时间从旧到新删到目标以内。
- Q04：池无活跃上限、无排队；`PoolPolicy` 只有 `MaxIdlePerPrototype`。`IdleLifetime`（空桶自动关闭）未实现，清理由业务调用 `ClearPoolAsync` 触发。
- 所有权：`FileCache`、`SimpleDownloader` 由装配者创建与关闭（`cache.StopAsync()` 在 `system.DisposeAsync()` 之后调用）；系统只借用。
- 来源加载器可以在初始化之后再 `RegisterLoader` 注册或替换（AssetBundle 加载器通常要等包系统就绪）。替换时旧加载器在途的加载照常完成，新请求走新加载器，已缓存产物不因替换失效。

## 最小装配（Unity）

```csharp
var context = new UnityExecutionContext();                     // 主线程上创建
var downloader = new SimpleDownloader(maxConcurrency: 2);
var cache = new FileCache(new HashedPathConverter(Path.Combine(Application.persistentDataPath, "rs")),
    RemoteFileLoader.UseDownloader(downloader), startupTargetBytes: 512L << 20, maxAge: TimeSpan.FromDays(7));
var options = new ResourceSystemOptions { ExecutionContext = context, InstanceFactory = new GameObjectFactory() }
    .AddLoader(ResourceSource.Resources, new ResourcesLoader())
    .AddLoader(ResourceSource.Local, new LocalFileLoader(Application.streamingAssetsPath))
    .AddConverter<string>(new TextConverter())
    .AddConverter<Texture2D>(new TextureConverter());
RemoteFileLoader.AddRemoteFiles(options, cache);
var system = new ResourceSystem(options);
await system.InitializeAsync();

using (var icon = await system.LoadAsync<Texture2D>(ResourceSource.Remote, "https://cdn/x/icon.png")) { /* icon.Value */ }
using (var enemy = await system.RentAsync<GameObject>(ResourceSource.Resources, "Prefabs/Enemy")) { /* enemy.Value */ }
await system.UnloadAsync(key => key.Source == ResourceSource.Remote);
```

## 验证

`ToolKit/Tests/ResourceSystem`：33 个行为测试（共享与接续、独立引用、卸载与残留、池准入/清池/晚归还/三种失败/创建中取消、文件命中/到期/失败/放弃后迟到/两种表示共享下载/启动清理）。
`ToolKit/Tests/UnityAdapterCompileCheck` 只做 Unity 适配层的编译校验；未在 Unity PlayMode、真实网络或磁盘故障下验证。
