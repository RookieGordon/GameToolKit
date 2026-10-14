# 文件缓存与 HTTP 解耦实施方案

本次把文件缓存与 HTTP 下载的职责分开：**缓存只负责本地文件的复用和保存，下载模块负责网络获取，加载器组合两者。** FileCache 不再依赖网络配置、URL、请求头、HTTP 响应或网络错误分类。

下载能力采用明确的复用方案：**复用项目现有 Network/Download 模块与 DownloadTask 的单任务下载逻辑，补齐可等待的下载入口；不在资源系统内继续维护第二套下载器。** 现有下载工具不能原样接入，必须先修正结果、取消和写入模式的契约。

本文交给实施代理执行。代码参考基准为 36e153e；实施前以实际工作区核对调用方。本文替代上一份《下载文件缓存删减实施说明》中“FileCache 接收 IFileTransport、NetworkOptions 并组织网络下载”的规定。此前确定的启动清理、缓存路径生命周期和资源引用规则继续有效。

## 1 目标与范围

完成后应同时满足：

1. FileCache 能通过一个普通的本地写文件回调完成全部缓存行为，不需要构造网络对象。
2. 下载工具能独立把远端内容写到调用者给出的路径，不知道该路径是否属于资源缓存。
3. RemoteFileLoader 和内置 AB 加载器共享应用级缓存和下载器；它们负责连接缓存需求与下载操作。
4. 用户替换加载器或下载实现时，不需要修改 FileCache。
5. 现有资源引用、对象池、解码器、AB 容器及依赖管理不随本次工作重写。

本次不建设通用文件系统、下载协议路由、Provider 注册中心或缓存中间件管线。不因 HTTP 解耦恢复文件租约、容量预留、元数据事务和运行期淘汰。

## 2 先确定流程与依赖

### 应用启动

~~~text
装配根创建 FileCache 与 SimpleDownloader
→ FileCache.InitializeAsync 执行一次启动扫描和清理
→ 把缓存和下载方法注入需要远端文件的加载器
→ 开放资源业务入口
~~~

缓存初始化不创建 HTTP 客户端、不连接服务器，也不要求下载器先开始工作。

### 加载一个远端资源

~~~text
加载器确定缓存身份、有效期和下载请求
→ 缓存查询本地文件
    命中：校验并返回路径，完全不调用下载方法
    未命中：同 key 共享一次填充
        缓存分配临时路径
        → 调用加载器提供的 fill 回调
            → 下载器限流、请求、按网络规则重试、写临时路径
            → 关闭网络响应及写入流后返回
        → 缓存校验内容，改名为唯一完整文件
        → 更新缓存记录，返回路径
→ 加载器解码或交给 Unity 加载
~~~

### 依赖方向

~~~text
RemoteFileLoader / SimpleAssetBundleLoader
    ├── FileCache
    │     └── 本地文件操作 + 调用 fill
    └── 注入的下载方法
          └── Network/Download/SimpleDownloader
                └── 复用 DownloadTask 的网络执行逻辑
~~~

FileCache 不引用 Network/Download。下载模块不引用资源系统的缓存、加载请求或错误类型。加载器可以同时使用两边的契约，公共 ResourceManager 业务接口不增加网络参数。

| 职责 | 归属 |
|---|---|
| 缓存身份、TTL、长度及摘要要求 | FileCache 的纯缓存请求 |
| 本地命中、同 key 填充合并、临时路径、校验、发布、启动清理 | FileCache |
| URL、请求头、重定向、HTTP 状态、连接与读取超时 | 下载模块 |
| 网络并发、网络重试与退避、取消实际网络操作 | 下载模块 |
| 将资源地址映射成缓存身份与下载请求 | 加载器 |
| 将下载诊断映射成资源系统两层错误 | 加载器接入边界 |
| 解码、资源引用、AB 容器卸载 | 各自现有模块 |

## 3 现有下载工具的复用结论

### 已有能力和实际缺口

项目已经有 [SimpleDownloader](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Network/Download/SimpleDownloader.cs)、[ParallelDownloader](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Network/Download/ParallelDownloader.cs) 和 [DownloadTask](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Network/Download/DownloadTask.cs)。两种下载器都使用 DownloadTask 执行网络读取和落盘，SimpleDownloader 本身也支持多任务并发。

| 核对项 | 当前代码事实 | 本次处理 |
|---|---|---|
| 指定目标路径、流式落盘 | DownloadTask.DownloadCoreAsync 使用 SavePath，并在 finally 中关流 | 复用这条单任务执行逻辑 |
| 下载重试、超时与速度等设置 | 已有相关参数与执行代码 | 保留可用能力，网络行为仍归下载模块；实际异步超时效果必须验证 |
| 可直接 await 的下载结果 | ExecuteAsync 为 internal；失败和取消设置状态、触发回调后正常 return | 增加明确的 Task 成功、失败、取消语义，不能原样 await 后当成功 |
| 结果等待 | IDownloadTask 只有状态、进度、Pause、Cancel，没有完成任务 | 资源接入使用新增 Task 入口，不轮询 Status 或拼装回调完成源 |
| 请求头与重定向 | 现有构造参数没有资源加载所需的完整设置入口 | 补到下载请求，不能退化丢弃 |
| 续传 | DownloadTask 有 Range、Append、恢复状态逻辑 | 资源缓存使用明确的从头写入模式，每次重试重新截断 |
| 错误信息 | OnFailed 只提供粗粒度 EDownloadError；原始异常主要写日志 | 新入口保留结构化原因、HTTP 状态和原异常 |
| 结束与释放 | SimpleDownloader.CancelAll/Dispose 不提供可靠的异步排空完成信号 | 接入路径必须等真实操作及关流完成，不能取消等待后立即删文件或释放并发对象 |
| 平台后台与通知 | IPlatformDownloadHandler 提供后台状态和通知能力 | 不作为缓存成立的前提，不把 Unity 平台桥接引入 FileCache |

具体依据见 [DownloadTask.ExecuteAsync](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Network/Download/DownloadTask.cs:170)、[失败与取消处理](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Network/Download/DownloadTask.cs:204)、[HTTP 写入与关流](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Network/Download/DownloadTask.cs:264) 和 [SimpleDownloader.CancelAll](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Network/Download/SimpleDownloader.cs:209)。

### 采用的实施路线

给 SimpleDownloader 增加一个可直接等待的 DownloadAsync 入口，复用 DownloadTask 内部的请求、流式读写与清理逻辑。旧队列入口可以继续对业务暴露暂停、恢复、进度和回调；资源缓存通过新入口发起一次完整下载。

共同的执行逻辑只保留一份。可以把 DownloadTask 内部方法调整为“执行后返回成功或抛出异常”，由旧 ExecuteAsync 包装并转换为原来的状态和回调。不得让新入口先调用会吞掉故障的旧 ExecuteAsync，再根据若干回调猜测操作是否结束。

**不采用以下两条路线：**

- 直接把当前 SimpleDownloader.Start 和 OnCompleted/OnFailed 包成 Task。这样无法完整表达原始错误、取消排空和从头写入语义。
- 在 ResourceSystem 内另写 HttpDownloader，或把 HttpFileTransport 简单换个目录后与 DownloadTask 长期并存。这样只是换位置保留两套下载逻辑。

当前 [HttpFileTransport](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/HttpFileTransport.cs) 中已经实现的请求头、重定向控制、超时处理和诊断分类可以作为迁移参考。把所需能力整合进下载模块后，删除资源系统专属的 HTTP 实现。无需为了这次改造强制更换底层 HTTP API；判断标准是契约和测试，不是使用哪个客户端类型。

ParallelDownloader 继续复用 DownloadTask，不在本次合并两个队列调度器或重写移动平台桥接。旧入口只做共享执行逻辑所必需的兼容调整。

## 4 最小接口

### 纯文件缓存

保留 FileCache、FileIdentity、FileValidity、FileKeyEncoding 的现有名称。FileRequest 收缩为纯缓存请求，移除 Source 和 TransportContext：

~~~csharp
// 字段使用当前项目已有的只读 DTO 风格。
public sealed class FileRequest
{
    public readonly FileIdentity Identity;
    public readonly FileValidity Validity;
    public readonly long? ExpectedLength;
    public readonly string? ExpectedSha256;
}

public sealed class FileCache
{
    // 保留必要的诊断与时钟注入，不再收网络对象。
    public FileCache(FileCacheOptions options,
                     IResourceDiagnostics? diagnostics = null,
                     Func<DateTime>? utcNow = null);

    public Task InitializeAsync(CancellationToken cancellationToken = default);

    public Task<string> GetOrCreateAsync(
        FileRequest request,
        Func<string, CancellationToken, Task> fill,
        CancellationToken cancellationToken = default);

    public Task ShutdownAsync(CancellationToken cancellationToken = default);
}
~~~

FileCacheOptions 继续只有缓存目录与 StartupTargetBytes。文件层只识别缓存身份、有效期、可选内容校验和本地 I/O；构造、初始化、命中均不需要网络配置。

fill 表示“向指定临时路径写入完整内容”。这是一个委托，无须新增 IFileSource、IFileProvider、IFileWriter 或传输注册服务。

### 下载模块

在现有 ToolKit.Tools.Network 命名空间增加：

~~~csharp
public Task DownloadAsync(
    DownloadRequest request,
    string destinationPath,
    CancellationToken cancellationToken = default);
~~~

这是 SimpleDownloader 的实例方法。DownloadRequest 放在 Network/Download，承载 URL、请求头和重定向设置。新 DownloadAsync 固定从头写入，每次尝试重新截断，禁止 Range/Append；无须为这个入口新增公开写入模式枚举。旧业务的续传能力继续由原任务入口支持，内部执行逻辑区分两种行为，不能为缓存改造统一关闭所有业务续传。

现有 NetworkOptions 从 ResourceSystem/Files 迁入 Network/Download，保留必要的并发、超时和重试含义。可沿用名称，但不能继续定义在资源缓存契约文件中。给 SimpleDownloader 增加接收该配置的构造重载，新入口使用构造时复制的配置；旧构造重载映射到一致的默认值，不维护两个相互矛盾的并发上限。

成功完成 Task 表示文件已写完、响应和写入句柄已关闭；失败使 Task 失败；取消使 Task 取消，并且实际 I/O 已停止、句柄已关闭。不用 bool、Status 或“回调已通知”代替这些语义。

### 加载器组合

把现有请求构造器返回的内容拆成一个简单组合：

~~~csharp
// 属于加载器接入契约，不放在 Files。
public sealed class RemoteFileRequest
{
    public readonly FileRequest Cache;
    public readonly DownloadRequest Download;
}
~~~

RemoteFileLoader 的 requestBuilder 改为返回 RemoteFileRequest。内置 AB 的远端定位数据也使用这个组合；本地 AB 路径继续走原逻辑。

两个加载器均可接收一个下载委托，默认由装配根传入 downloader.DownloadAsync：

~~~csharp
Func<DownloadRequest, string, CancellationToken, Task> download;

var path = await cache.GetOrCreateAsync(
    request.Cache,
    (temporaryPath, cacheToken) =>
        DownloadWithResourceErrorMappingAsync(
            download, request.Download, temporaryPath, cacheToken),
    callerToken);
~~~

DownloadWithResourceErrorMappingAsync 表示加载器接入处的一小段错误映射，可复用已有错误辅助代码；不要求创建新的公开服务接口。网络配置、URL 和 HTTP 诊断留在闭包外部所属的加载器与下载模块中，FileCache 不解析它们。

## 5 必须固定的行为契约

### 缓存填充与路径归属

- FileCache 创建专用缓存目录，选取独占的 .part 路径。fill 只能写这个路径，不能修改最终文件名或缓存索引。
- fill 成功返回后不再持有写入句柄或后台写入任务。FileCache 随后执行长度/摘要校验并发布文件。
- fill 失败或取消时，必须先结束自身 I/O，再把异常交回缓存；缓存清理自己的临时文件。
- HTTP 内部重试使用同一目标临时路径也可以，但每次尝试必须先关掉上一轮响应与流，再从零截断写入。不能把上次半截文件当作缓存命中。
- FileCache 不做重试。一次共享填充只调用一次 fill，网络重试全部包含在下载模块这一次调用之内。
- 发布后的完整文件在本次应用运行内不被缓存删除或覆盖。TTL 到期创建新物理路径，旧路径留到下次启动。

缓存只判断回调的成功、失败、取消，不要求回调提供 HTTP 状态、ETag、ContentLength 或网络统计。HTTP 响应自身的完整性属于下载器；调用方声明的缓存长度和摘要属于缓存校验。

### 合并请求与取消

同 key 共享一次填充仍由 FileCache 负责，这是缓存行为。查本地、登记共享任务与确认发布使用原有短临界区；校验 I/O 和 fill 执行必须在临界区外。命中路径直接返回，不得顺便登记或启动填充。

多个同 key 请求提供不同回调时，只执行创建共享任务的那一个。缓存不能比较 URL、签名或请求头；调用方必须保证相同身份表示相同内容。已知长度/摘要冲突继续按现有缓存规则报错，每个接收者的交付校验不能丢失。

调用者取消只结束自己的 GetOrCreateAsync 等待。fill 使用缓存生命周期令牌；即使所有等待者取消，也允许填充完成并进入缓存。缓存 Shutdown 才取消并等待共享填充真正结束。不要重新引入等待者归零就终止网络的文件层状态机。

### 下载并发与停止

SimpleDownloader 的新入口直接启动受限的异步下载，无须业务先 AddTask 再 Start。排队等待并发槽位也必须响应取消。

默认创建一个资源加载专用的 SimpleDownloader 实例，由远端文件与 AB 加载器共享，只走新 DownloadAsync 入口。本次不要求同一实例混用旧 AddTask/Start 队列；用简单入口检查明确拒绝两套入口混用，开始使用一种入口后不切换模式，无须新增用户配置或调度框架。旧批量下载业务使用其原有实例。

网络操作只取得一次槽位，不能在下载器和 DownloadTask 内重复加锁；也不能为每个 fill 创建一个下载器，导致并发限制失效。新入口不能使用 Start 会替换的批次 CTS；每次下载直接响应传入的取消令牌。新入口采用固定并发配置，开始使用后调用 SetMaxConcurrency 明确报错；旧队列保留原有的空闲调整方式，不新增统一动态调度机制。

共同复用的是 DownloadTask 的实际执行逻辑；不强制改造旧队列的调度结构。缓存没有网络并发字段，不同缓存键的网络任务由同一个下载器限制。本地命中和纯本地 fill 不进入网络队列。

装配根按“停止新资源请求 → 排空资源 → 关闭缓存并等待全部 fill → 释放资源专用下载器”的顺序退出。下载器由装配根拥有，FileCache 不能 Dispose 它。

已有 CancelAll/Dispose 不能被当成排空完成信号。接入流程通过缓存关闭等待所有实际 DownloadAsync Task 收尾后才 Dispose 下载器；不把这个顺序要求扩大成同时重构旧队列和 ParallelDownloader 的关闭协议。

### 错误分层

下载模块提供自身的结构化故障，例如一个 DownloadException：保留现有粗粒度 EDownloadError，并增加可排查的原因、可选 HTTP 状态和 InnerException。不得只把原异常打到日志后返回“失败”。网络模块不引用 LoadError、ResourceLoadException 或资源系统 DiagnosticCodes。

加载器接入边界将下载故障映射成现有资源错误：

| 故障发生点 | 谁识别 | 交给资源层的信息 |
|---|---|---|
| 地址、DNS、连接、TLS、HTTP 状态、读取中断或超时 | 下载模块 | 具体原因、状态和原异常，加载器映射诊断码 |
| 下载器创建或写入目标文件失败 | 下载模块的写入步骤 | 存储故障，不能伪装为网络故障 |
| 缓存扫描、校验、发布、删除临时文件失败 | FileCache | 现有文件/缓存诊断 |
| 取消 | 实际执行层 | 保留 OperationCanceledException，不包装成网络问题 |

面向用户的 NetworkProblem 等表述继续由现有资源错误映射决定；404、权限、磁盘满等沿用各自现有分类，不把所有下载失败统一成网络问题。开发者仍能得到足够的诊断信息，日志避免输出请求头和带签名的完整 URL。

FileCache 对 fill 抛出的已分类错误透传。不要对所有回调异常做一次“IOException 等于网络中断”的猜测，也不要再识别 HTTP 错误以决定重试。

## 6 下载模块需要补齐的具体行为

本节是复用的完成条件，不要求重写下载模块全部能力：

1. 从 DownloadTask 提取或整理一条能传播真实结果的内部执行路径；旧 ExecuteAsync 负责原有状态和回调适配，新 DownloadAsync 等待真实完成。
2. 在下载请求传入请求头和重定向设置，保留用户现有远端加载参数。参数缺失不能静默回退为不支持鉴权的下载。
3. 新 DownloadAsync 固定从头写入：尝试开始时重置已写入字节、续传状态和文件内容；重试也同样执行。不要只在第一次下载时截断。
4. 只有适合重试的瞬态网络故障进入重试。地址错误、404、授权拒绝、磁盘满和权限问题直接失败。取消不重试；缓存校验失败也不会回到下载器自动重试。
5. 验证连接/响应头等待和响应体读取均有实际生效的超时。不能仅因对象设置了 Timeout/ReadWriteTimeout 就认为异步路径已有保证。
6. 取消请求后等待底层网络与文件写入真实退出。收到 token 通知就立即完成 Task 的包装方式不符合契约。
7. 读取失败和写入失败在发生位置分类，保留原异常。只有流正常结束且响应完整时才成功，服务器中途断开不能发布半文件。
8. 事件订阅者抛异常不能使已完成下载被当作网络失败再次下载。新 Task 的结果不依赖回调是否成功。
9. 默认资源接入不调用 PauseAll/ResumeAll，也不触发平台通知。旧业务继续保留这些能力；不扩展本次资源 API 去管理它们。

资源接入只要求一个可等待的完整下载入口。不新增后台断点数据库、持久任务队列或下载会话管理。

## 7 缓存关键逻辑

~~~text
GetOrCreateAsync(cacheRequest, fill, callerToken):
    验证纯缓存参数与初始化顺序
    等待已经开始的初始化，检查仍接受请求
    查询并验证本地候选
    若命中：
        检查调用者取消
        直接返回路径

    短临界区内重新检查记录与关闭状态
    若发现新发布记录：离开临界区重新验证并复用
    若已有同 key 填充：取得共享 Task
    否则登记新的共享 Task
    在临界区外启动需要执行的填充
    仅可取消地等待自己的结果，不取消共享 Task
    按当前请求执行交付校验
    检查调用者取消并返回路径

FillAndPublish(cacheRequest, fill, cacheLifetimeToken):
    分配本次填充独占的临时路径
    try:
        await fill(临时路径, cacheLifetimeToken)
        校验文件长度和可选摘要
        检查缓存生命周期取消
        将临时文件改名为唯一完整文件
        更新普通内存记录
        完成共享 Task
    catch:
        以实际异常完成共享 Task
        按现有诊断机制观察失败
    finally:
        尽力清理尚存在的本次临时文件
        按实际 Task 身份移除共享记录
~~~

共享任务必须在执行前登记，每个路径都要完成结果信号。失败即使无人等待也要被观察；既记录故障又把异常放进 Task 后，仍须确保该 Task 的异常已被观察。

伪代码中没有 URL 校验、网络槽位、HTTP 响应、重试退避或状态码。网络下载只是 fill 的一种实现。

## 8 按文件迁移

| 当前文件或调用点 | 必须完成的调整 |
|---|---|
| [FileCache.cs](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/FileCache.cs) | 改为 GetOrCreateAsync；移除传输字段、网络选项、下载槽位、网络重试和分类；保留启动清理、纯缓存记录、共享填充、校验与发布 |
| [FileContracts.cs](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/FileContracts.cs) | FileRequest 移除 Source/TransportContext；保留缓存 DTO；NetworkOptions 移到下载模块 |
| [IFileTransport.cs](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/IFileTransport.cs) | 删除 IFileTransport 和 TransportResponse；不把它们改名成另一组缓存传输接口 |
| [HttpFileTransport.cs](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/HttpFileTransport.cs) | 所需能力合入现有下载模块后删除，不保留第二份 HTTP 下载实现 |
| [SimpleDownloader.cs](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Network/Download/SimpleDownloader.cs) | 新增直接 await 的 DownloadAsync；让新入口复用下载核心并受实例并发限制；修正相关结束和空闲判定 |
| [DownloadTask.cs](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Network/Download/DownloadTask.cs) | 整理真实结果传播；补请求配置、从头写入、取消排空和详细故障；旧入口保留状态/回调适配 |
| [IDownloadTask.cs](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Network/Download/IDownloadTask.cs) 与 [EDownloadError.cs](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Network/Download/EDownloadError.cs) | 旧契约保留兼容性；新入口的完整 Task 与故障对象无需强行塞进旧接口 |
| [RemoteFileLoader.cs](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Loaders/RemoteFileLoader.cs) | 拆分请求；注入下载方法；通过 fill 组合；在接入处转换下载错误；保留默认 TTL、URL 身份和解码释放规则 |
| [SimpleAssetBundleLoader.cs](D:/Workspace/Porjects/GameToolKit/Unity/Assets/UnityToolKit/Runtime/Resource/Loaders/SimpleAssetBundleLoader.cs) | BundleLocation.RemoteRequest 改为组合请求，远端分支同样调用缓存加 fill；本地分支、容器引用、依赖及卸载屏障保持原有规则 |
| 应用装配、示例、测试 | 显式创建并共享缓存与下载器，先初始化缓存再开放入口；删除旧构造参数与 FakeTransport |

TransportContext 的请求头和重定向字段并入下载模块的 DownloadRequest，删除 Files 中的定义。RemoteFileRequest 放在加载器契约位置；不让纯缓存通过这个组合间接重新接收网络参数。

删除源文件时同步处理对应 Unity meta，新增文件按项目惯例生成 meta。保留 ResourceFileIo 等仍在使用的本地 I/O 辅助代码。

目前 Network/Download 与资源公共代码都由 [Tools.csproj](D:/Workspace/Porjects/GameToolKit/ToolKit/Tools/Tools.csproj) 纳入编译。这次先形成正确的源码依赖，不为证明分层再拆多个程序集。

## 9 与上次简化的衔接

以下规则保持：

- 应用启动、资源加载之前清理一次；重建 ResourceManager、下载完成或缓存超量都不触发清理。
- StartupTargetBytes 是启动保留目标，运行期不承诺容量硬上限。
- 已发布文件路径本次运行内不删除、不覆盖；没有文件租约或文件使用计数。
- TTL 命中规则、唯一文件名、.part 不可命中、目录归属规则继续执行。
- 同 key 共享填充，调用者仅取消自己的等待，应用级缓存关闭负责取消并等待实际操作。

当前物理目录名 http-cache-v2 可以作为历史兼容目录名保留。本次不因为名称含 http 就迁移格式、删除缓存或制造重新下载。代码和接口去掉 HTTP 行为依赖即可；不要把旧格式清理扩大到包含新缓存的父目录。

上一份文档中以下要求由本文修正：

| 旧要求 | 本次替代 |
|---|---|
| FileCache 接收 IFileTransport、NetworkOptions | FileCache 只接收缓存配置，每次未命中调用 fill |
| FileRequest 含 URL 与 TransportContext | FileRequest 只含缓存需求，下载请求在加载器组合 |
| FileCache 负责下载并发、重试和网络分类 | 现有下载模块负责，缓存透传结果 |
| 缓存测试使用 FakeTransport | 改为本地可控 fill；网络行为在下载模块测试 |
| 保留资源专属 HttpFileTransport | 能力合入既有 DownloadTask 路径后删除 |

资源核心、解码器及 AB 的取消和释放语义不随文件缓存解耦放宽。

## 10 测试与验收

测试应分别证明两边可以独立工作，再覆盖加载器组合。缓存测试不连接真实 CDN，也不构造虚假的 HTTP 响应来测试普通文件行为。

| 编号 | 场景与断言 |
|---|---|
| H01 | FileCache 仅依靠写入测试字节的 fill 完成初始化、命中、过期、发布、重启恢复与启动清理；不创建下载器 |
| H02 | 预置命中或连续第二次请求，fill 调用数不增加；下载器即使不可用也能命中 |
| H03 | 同 key 并发只执行一次 fill；成功共享路径，失败共享同一次故障；已知身份声明冲突仍报错 |
| H04 | 一个或全部调用者取消仅取消各自等待，fill 可继续完成；缓存关闭才取消并等待实际 fill 收尾 |
| H05 | fill Task 尚未结束时，缓存不发布或删除其临时文件；fill 实现必须在 Task 结束前关闭句柄；失败/取消只清自身临时文件 |
| H06 | TTL 刷新使用新路径，旧路径有效；运行期无自动清理，重复初始化不清理 |
| H07 | 下载器独立写入给定临时测试路径，不依赖任何 FileCache、FileIdentity 或 ResourceLoadException |
| H08 | 新下载 Task 仅在文件完整且句柄关闭后成功；网络失败为失败 Task，真实取消为取消 Task，不把旧 ExecuteAsync 的正常返回误判为成功 |
| H09 | 请求头和重定向配置实际生效；下载中断后的重试重新截断，不叠写、不隐式发送续传 Range |
| H10 | 不同 key 的远端请求共享下载并发上限，等待槽位可取消；无双重占槽，同实例混用新入口与旧队列会明确报错 |
| H11 | 连接/响应头及响应体超时生效，取消结束后无后台写入；失败重试有限，404、授权、存储错误和取消不重试 |
| H12 | 网络读取、磁盘写入分别分类；原异常、诊断原因和 HTTP 状态可追踪，用户仍获得现有简明错误 |
| H13 | RemoteFileLoader 保留 URL query/port 身份、稳定身份复用、默认 TTL、解码参数和结果释放 |
| H14 | 远端 AB 使用缓存加 fill 后，容器引用、依赖、迟到完成和卸载屏障不退化，本地 AB 不需下载器 |
| H15 | Shutdown 等待缓存填充结束后才释放下载器；旧 DownloadTask/队列受本次改动影响的暂停、恢复、回调路径通过回归 |
| H16 | Files 不再引用网络配置、URL 请求 DTO、HTTP 响应或网络故障分类；Network/Download 不依赖资源专属类型，旧 IFileTransport/HttpFileTransport 已删除 |

原有 [FileCacheTests.cs](D:/Workspace/Porjects/GameToolKit/ToolKit/Tests/ResourceSystem/FileCacheTests.cs) 中网络限流、DNS/HTTP 等用例移到下载模块；纯缓存、生命周期、校验用例改用 fill。更新 [FileLoaderTests.cs](D:/Workspace/Porjects/GameToolKit/ToolKit/Tests/ResourceSystem/FileLoaderTests.cs) 与 [ReviewFixDiskTests.cs](D:/Workspace/Porjects/GameToolKit/ToolKit/Tests/ResourceSystem/ReviewFixDiskTests.cs) 的构造、请求和组合测试。

下载测试使用可控本地 HTTP 服务或局部内部测试替身，分别触发响应头等待、读中断和写盘失败；不能把真实 CDN 稳定性当成测试前提。使用栅栏控制并发与取消交错，不靠固定延时猜测完成。

既有资源系统回归、Network/Download 受影响用例、Unity 调用方编译和必要的 AB PlayMode 验证均应执行。未能执行的验证明确报告，不以静态检查替代运行结论。

## 11 实施顺序与交付

1. 检索当前 FileCache、FileRequest、IFileTransport、NetworkOptions、DownloadTask 与下载器调用方，确认工作区变化。
2. 先给现有下载模块补可等待入口和必要契约，用独立下载测试验证成功、错误、取消、写入模式与并发。
3. 收缩缓存请求和构造，移除网络代码，使用纯本地 fill 跑通缓存测试。
4. 迁移远端文件加载器、AB 和装配，接入下载故障映射。
5. 删除旧资源专属传输类型、兼容空壳、旧网络字段及重复测试；同步修正文档。
6. 完成组合回归，交付变更范围、复用点、删除项、兼容变化与验证结果。

最终检查的重点是：缓存可以完全独立于 HTTP 工作；下载能力汇入现有模块；同一行为只有一个主要实现。不能仅搬文件、改类名或新增一层接口后继续让 FileCache 处理网络策略，也不能为了复用旧工具，把资源缓存拖入暂停、通知和后台下载的业务状态。
