# 资源系统重写设计符合性审查

**结论：整体职责划分符合方案，但关键流程尚未满足设计契约，当前不宜判定为重写验收完成。** 主要问题在取消之后的底层所有权、实例池关闭、磁盘容量记账，以及公开配置和扩展接口的实际行为。修复应沿现有模块补齐流程，不需要再增加插件框架或通用背书层。

审查以[资源系统重写实施方案](D:/Workspace/Porjects/GameToolKit/Docs/ResourceSystemRewrite/资源系统重写实施方案.md)为基准，覆盖当前工作区的 ResourceSystem、Unity Runtime Resource 集成和资源系统单元测试；HEAD 为 fa3e34b（资源加载系统重写）。本文的代码行号对应审查时工作区。

现有资源系统测试实际执行 **64 项，全部通过**。隔离程序进一步复现了现有测试未覆盖的身份碰撞、配置失效、容量错误、关闭挂起及原型泄漏。Unity 请求和 GameObject 行为按源码流程及对应 API 语义审查，**没有执行 Unity PlayMode 或 Player 验证**。动态复现与静态结论在各项中分别标明。

## 1 总体符合性

| 设计目标 | 审查结论 |
|---|---|
| 统一业务入口与可替换加载器 | 已落实 Load/Rent 和 IResourceLoader 分工；无需引入独立插件系统 |
| 核心不依赖目录和清单形式 | 符合；BundleLocation、依赖及定位留在具体 AB 实现 |
| 共享结果与独立持有 | 非池化 ResourceRef/InstanceLease、独立等待者、交付前登记持有已落实 |
| 同键取消与卸载屏障 | ResourceStore 主体实现符合；AB 容器内部未完整遵守同一规则 |
| 远端优先本地 | 文件有效命中不联网的正常流程已实现；默认远端身份和有效性规则有严重偏差 |
| 三类缓存分离 | 内存条目、FileCache 和实例池职责分离符合；容量、代际与关闭边界仍有缺陷 |
| 配置丰富且行为确定 | 类型已经提供，但存在不冻结、不验证、默认值错误和部分配置不生效 |
| 用户与开发者两层错误 | 模型符合；映射器未接入、部分故障被吞掉或错误归类 |
| Unity 绑定和生命周期集成 | Host、工厂和自动归还存在；绑定与作用域管理尚未交付 |

以下改动属于合理实现选择，不应为了逐字匹配 UML 再改一遍：内部辅助类放在同一文件；FileCache 使用短同步锁管理状态；维持单独的内存、文件和实例策略；未默认内置第三方 SDK 适配器；未把现有 KeyedAsyncLock 叠加到已经串行的状态提交上。ResolveAsync 的两阶段形式也与当前文档一致；此前关于是否简化它的讨论没有形成已确定的新契约。

## 2 问题清单

P1 表示应在验收前优先修复，会造成错误资源、所有权丢失、请求无法结束或容量约束失效。P2 表示明确的接口、扩展、异常边界或交付偏差；并不代表不会泄漏。编号用于后续修复和回归追踪，同一问题的多个触发方式没有按测试数量重复计数。

| 编号 | 优先级 | 问题 |
|---|---|---|
| R01 | P1 | 默认远端身份把不同请求当成同一内容，并永久信任无版本 URL |
| R02 | P1 | AB 容器共享加载受首个资源操作的取消令牌控制 |
| R03 | P1 | Unity 请求取消只结束包装任务，迟到对象失去所有者 |
| R04 | P1 | AB 的 Unloading 标志没有形成同容器卸载屏障 |
| R05 | P1 | 默认 GameObject 工厂二次租用后仍位于失活根节点 |
| R06 | P1 | 解码回退未确认完成就放开源文件 pin |
| R07 | P1 | 关闭期间创建失败绕过清理语义，报告虚假的安全关闭 |
| R08 | P1 | 无人等待的迟到实例执行 OnReturn 时没有持有记录，回调关闭会泄漏实例并提前释放原型 |
| R09 | P1 | CanCreate 异常使租用悬挂，并永久遗失已取得的原型引用 |
| R10 | P1 | 容量申请将已写字节再次当成未写预留，能装下的文件被拒绝 |
| R11 | P1 | 取消关闭等待会终止真正关闭，再次关闭却直接成功 |
| R12 | P1 | 在下载完成、提交前关闭会丢掉请求完成信号 |
| R13 | P1 | 重启后 generation 从零重用，与仍被旧租约使用的文件路径冲突 |
| R14 | P1 | 元数据没有实施大小上限，可在成功提交时突破总容量 |
| R15 | P1 | 失败的元数据临时文件既不清理也不计量，重启也遗漏 |
| R16 | P1 | 部分删除失败后，重试会重复扣已删除数据长度 |
| R17 | P2 | AB 加载 catch 把实际故障吞成 asset.not_found |
| R18 | P2 | 本地加载忽略已选择的解码器，两个文件加载器都丢失解码参数 |
| R19 | P2 | GameObject DestroyAsync 超时后正常成功，未确认销毁 |
| R20 | P2 | 自动归还组件重新绑定后仍保留 detached 状态 |
| R21 | P2 | LoadError 默认承诺清理完成，与隔离安全默认值冲突 |
| R22 | P2 | 内置字节/图片解码器将主动取消包装成文件 I/O 故障 |
| R23 | P2 | 原型在获取后失效的 catch 分支丢失引用 |
| R24 | P2 | 诊断回调异常中断 FaultBucket，ClearPool 永远得不到终局 |
| R25 | P2 | 加载器独立内存容量策略被忽略，配置为不缓存仍被缓存 |
| R26 | P2 | IsAlive 抛异常被当成已销毁，成功创建的实例不再清理 |
| R27 | P2 | 旧代池销毁失败时，同 PoolKey 的新池仍接受后续租用 |
| R28 | P2 | 配置没有冻结和完整验证 |
| R29 | P2 | 省略加载器策略时没有采用默认四路并发 |
| R30 | P2 | 注入的用户错误映射器没有参与实际映射 |
| R31 | P2 | 重复注册会静默替换已有加载器和工厂 |
| R32 | P2 | 清理空闲资源接口忽略取消等待的令牌 |
| R33 | P2 | 工厂切换原型加载器后无法按原请求清理实例池 |
| R34 | P2 | 文档承诺的绑定和作用域管理尚未交付 |

## 3 逐项证据与修正建议

### R01 P1 默认远端身份把不同请求当成同一内容，并永久信任无版本 URL

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[RemoteFileLoader.cs:128](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Loaders/RemoteFileLoader.cs:128)，关键实现 131–135；LocalKey 使用该身份在 52–54。
- 触发：默认装配下，依次加载 `https://host/image?id=A` 和 `https://host/image?id=B`（或同一主机不同端口）；或者服务器替换 `/config.json` 的内容。
- 事实：默认身份只保留 `uri.Host + uri.AbsolutePath`，revision 是相同路径的 SHA256，而非内容版本；最后一律 `FileValidity.Immutable`。查询参数和端口均被抹除。
- 影响：A/B 获得相同 LocalKey，第二次甚至不进入 FileCache 就返回第一次内容；同一 URL 内容更新后，内存条目即使淘汰，磁盘旧文件仍永久有效。签名 URL 应可由明确业务映射合并，不能默认把所有 query 都当签名。
- 偏离：§3.3 文档 237、247、251 行；§10.2 文档 1535 行明确禁止把未版本化可变地址默认视为永久有效。
- 最小修正：默认身份保留影响资源内容的完整 URL（含端口/查询；身份可哈希，日志单独脱敏）；为无明确版本的默认请求提供 TTL 配置，或要求 builder 显式给出有效性。只有用户提供稳定 ArtifactId/Revision 时才合并签名 URL。
- 验收：query/port 不同内容隔离；动态 URL TTL 后重新下载；显式版本相同、签名不同仍共用文件。

### R02 P1 AB 容器共享加载受首个资源操作的取消令牌控制

- 证据类型：已核对源码控制流；未执行对应 Unity 或故障注入场景。
- 位置：[SimpleAssetBundleLoader.cs:205](D:/Workspace/Porjects/GameToolKit/Unity/Assets/UnityToolKit/Runtime/Resource/Loaders/SimpleAssetBundleLoader.cs:205)，205–218；下传发生于 250、266、270。
- 触发：同时加载同一 bundle 中的 asset A 和 asset B，容器先由 A 创建；A 的调用者全部取消，B 仍在等待。
- 事实：fresh.Loading = `_LoadBundleEntryAsync(..., ct, ...)`，ct 是 A 对应 ResourceEntry 的操作令牌。B 虽使用 WaitWithCancellation 独立取消等待，却共享了受 A 令牌控制的容器下载/打开操作。
- 影响：A 的取消导致 FileCache/Unity 包加载提前失败，B 也失败；同时没有容器级等待者计数，任务成功但全部等待者取消时，Holds=0 的 bundle 可能留在字典而无人触发释放。
- 偏离：§8.3 文档 1392、1398、1400；容器必须有独立操作令牌，先登记成功等待者持有再完成任务，取消/迟到结果规则沿用 §6。
- 最小修正：BundleEntry 自己持有操作 CTS、等待者记录和终结屏障；每个资源请求只取消自己的等待，最后等待者退出才取消容器操作；零接收者的成功结果进入释放。
- 验收：同包不同 asset 的 A 取消不影响 B；所有等待者退出后 bundle/dependency/file lease 都最终归零。

### R03 P1 Unity 请求取消只结束包装任务，迟到对象失去所有者

- 证据类型：已核对源码控制流；未执行对应 Unity 或故障注入场景。
- 位置：[SimpleAssetBundleLoader.cs:365](D:/Workspace/Porjects/GameToolKit/Unity/Assets/UnityToolKit/Runtime/Resource/Loaders/SimpleAssetBundleLoader.cs:365)（365–379、382–397）；[ResourcesLoader.cs:94](D:/Workspace/Porjects/GameToolKit/Unity/Assets/UnityToolKit/Runtime/Resource/Loaders/ResourcesLoader.cs:94)（94–108，另有 72）。
- 触发：Resources.LoadAsync、AssetBundle.LoadFromFileAsync 或 bundle.LoadAssetAsync 已发起但尚未完成时，操作令牌取消。
- 事实：ct 回调只对 TCS.TrySetCanceled；没有取消已发起的 Unity operation。随后 completed 回调 TrySetResult 会失败，却不接管/释放 request.assetBundle 或 request.asset。await 抛出还会跳过 registration.Dispose。ResourcesLoader 第 72 行另可在已经得到 asset 后抛取消、丢掉资产。
- 影响：框架认为加载已经终止，迟到的 Unity 对象却无人管理；AB 外层 catch 还会提前归还 bundle/dependency/file lease，实际底层请求可能仍在访问它们。新请求可能重新打开同一包，而旧操作尚未终结。
- 偏离：§3.4 文档 278–280；§6 同键 Draining 屏障；§8.3；§13 迁移条目明确要求 Resources 取消后的迟到结果处理。
- 最小修正：不可中断的 Unity 请求必须观察到真实完成。业务取消由上层等待者处理；加载器成功后返回带释放器的 LoadedAsset，让 Draining 路径回收，或自己完成并确认回退后才结束加载任务。注销 cancellation registration 放 finally。
- 验收：阻塞 Unity 请求→取消全部等待者→释放底层完成；没有业务交付，实际 bundle/asset 释放一次，旧请求真实终结前新请求不能越过屏障。

### R04 P1 AB 的 Unloading 标志没有形成同容器卸载屏障

- 证据类型：已核对源码控制流；未执行对应 Unity 或故障注入场景。
- 位置：[SimpleAssetBundleLoader.cs:331](D:/Workspace/Porjects/GameToolKit/Unity/Assets/UnityToolKit/Runtime/Resource/Loaders/SimpleAssetBundleLoader.cs:331)，331–353；获取端 186–207。
- 触发：旧包最后一个持有正在后台回退并投递主线程 Unload，此时另一资源键请求同一包；或者 Unload/依赖回退抛异常。
- 事实：标记 Unloading 后立即清空所有权字段、从 `_entries` 移除，之后才调用 Unity Unload，再释放依赖和文件。新请求查不到旧 entry，直接建立 fresh entry，永远观察不到卸载标记。释放失败时旧容器和文件所有权也没有可查询的隔离记录。
- 影响：同包不同资源键可在旧包释放完成前重新开包；发生回退失败时，重试继续加载而旧所有权未知。核心的 ResourceKey 屏障不能替代容器级屏障，因为两个 asset 的 ResourceKey 不同。
- 偏离：§8.3 文档 1402 之后的释放流程，以及 1400 对 §6 屏障要求的复用。
- 最小修正：保留 entry 和 Terminal 到实际 Unload、依赖与文件回退完成；失败留隔离状态及所有权记录。新请求等待 Terminal，不使用 Task.Yield 自旋。
- 验收：延迟主线程 Unload 时同容器新 Load 不启动；Unload/依赖回退故障时条目仍可诊断且不重复开包。

### R05 P1 默认 GameObject 工厂二次租用后仍位于失活根节点

- 证据类型：已核对源码控制流；未执行对应 Unity 或故障注入场景。
- 位置：[GameObjectInstanceFactory.cs:80](D:/Workspace/Porjects/GameToolKit/Unity/Assets/UnityToolKit/Runtime/Resource/GameObjectInstanceFactory.cs:80)，80–95；默认 root 32–35 被 SetActive(false)。
- 触发：默认工厂 Rent → Dispose 归还 → 再次 Rent，同一个闲置实例被复用。
- 事实：OnReturn 将实例挂到 inactive `_poolRoot`；OnRent 只 SetActive(true)，没有移出池根节点。
- 影响：activeSelf 为 true，但 activeInHierarchy 仍为 false；二次租出的对象不可见、组件不更新，与第一次租用行为不同。业务当然可以自行重新挂父节点，但该默认工厂并没有要求或提供这种前置契约。
- 偏离：§3.5/§7.2 OnRent 必须完成可交付状态重置；§14 P01/P05。
- 最小修正：OnRent 先从内部池根节点移出或恢复明确的租用父节点，再激活；创建和复用路径使用同一重置规则。
- 验收：默认父节点下两次 Rent 的 activeInHierarchy 都为 true。
- Unity 官方依据：[GameObject.SetActive](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/GameObject.SetActive.html) 明确说明父节点 inactive 时 SetActive(true) 不会改变 activeInHierarchy。

### R06 P1 解码回退未确认完成就放开源文件 pin

- 证据类型：已核对源码控制流；未执行对应 Unity 或故障注入场景。
- 位置：[RemoteFileLoader.cs:99](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Loaders/RemoteFileLoader.cs:99)，99–124。
- 触发：RequiresSourceFile=true 的用户解码器取得会继续访问源文件的中间对象后，抛 Cleanup=Incomplete/Unknown；或 decoded.ReleaseAsync 的回退失败。
- 事实：catch ResourceLoadException 无论 Cleanup 值/回退结果都在 115 行 file.Dispose；未分类异常也在 121 行无条件 Dispose。没有保存隔离文件租约。
- 影响：FileCache 认为 pin 已归零，容量淘汰能删除仍被中间对象使用的文件。核心对资源条目的隔离无法补回被加载器提前释放的文件保护。
- 偏离：§8.2 文档 1347–1357 明确规定，只有已确认回退完成才能归还 FileLease，否则由加载器保留隔离所有权。
- 最小修正：仅 Cleanup=Complete 或 decoded 释放成功时归还；Unknown/Incomplete 保存文件租约和中间结果到加载器隔离记录并报告关联错误。回退失败必须把总体 Cleanup 升为 Incomplete。
- 验收：自定义解码器故障后 Trim 无法删掉该文件；明确完成回退后才可删除。

### R07 P1 关闭期间创建失败绕过清理语义，报告虚假的安全关闭

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[InstancePool.cs:524](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Instancing/InstancePool.cs:524)，重点 524–541。
- 流程：启动一次 `RentAsync`；工厂的 `CreateAsync` 在 TCS 处等待；调用 `ClearPoolAsync` 让桶进入 Closing；随后工厂以 `ResourceLoadException(CleanupStatus.Incomplete)` 完成，表示尚有创建残留。
- 实际行为：`_OnCreateCompleted` 先令 `Creating=0`，再因桶不是 Open 直接 `_CheckBucketClosed` 返回。后面的 `error.Cleanup` 检查完全跳过。桶关闭、原型释放、ClearPool 和 Shutdown 均返回成功，诊断中没有该清理故障。
- 复现输出：`close=success, shutdown=success, prototype-released=1, errors=0`。
- 影响：失败创建留下的实例/SDK 对象仍可能依赖原型，原型却可被卸载；上层拿到“已安全关闭”的错误保证。
- 设计依据：§7.3 创建失败路径（方案 1191–1194 行）要求 Unknown/Incomplete 转 FaultBucket 并保留原型；§7.4（1235–1245 行）要求真实清理完成才释放原型。
- 修正方向：先处理失败结果及清理状态，再处理桶的 Open/Closing 分支。关闭不改变失败回退的责任；不完整失败须保留未清理工作记录和原型，关闭任务返回故障。

### R08 P1 无人等待的迟到实例执行 OnReturn 时没有持有记录，回调关闭会泄漏实例并提前释放原型

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[InstancePool.cs:579](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Instancing/InstancePool.cs:579)，重点 579–593。
- 流程：工厂创建尚未完成时取消唯一 Rent 调用者；放行创建，使实例进入 `_DeliverCreated` 的“无人等待”分支；工厂 `OnReturn` 内同步调用 `ClearPoolAsync`（例如用户脚本在禁用/归还过程中关闭界面或池）。
- 实际行为：此处没有先增加 `Returning`，实例也尚未进入 Idle/Active；桶因此被判为空并关闭、释放原型。OnReturn 返回后没有重新检查桶状态，又将实例入队到已关闭且已脱离跟踪的旧桶。
- 复现输出：`close=success, tracked-pools=0, destroyed=0, prototype-released=1, shutdown=success`。
- 影响：活着的实例永远失去框架追踪，DestroyAsync 不再执行；原型可能在实例存活期间卸载。
- 设计依据：§7.3/§7.4 要求 Creating/Preparing/Returning/Destroying 全部归零才能放原型；方案 1219 行明确所有工厂回调都可能执行用户脚本，返回后必须重查 manager、桶和等待者。普通 Return 分支已经实现 Returning 计数及重查，这条迟到结果路径遗漏了同一保护。
- 修正方向：把迟到结果按与普通 Return 一致的状态流处理：进入 Returning 计数后执行回调；回调返回原子转 Idle 或 Destroying，并重查关闭与容量；不要复制另一套缺少保护的归还流程。

### R09 P1 CanCreate 异常使租用悬挂，并永久遗失已取得的原型引用

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[InstancePool.cs:316](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Instancing/InstancePool.cs:316)，关联 255–345。
- 流程：加载器正常返回原型，用户实现的 `IInstanceFactory.CanCreate` 抛出异常。
- 实际行为：CanCreate 没有 try/catch。异常从 `_OnPrototypeAcquired` 穿透到被忽略的 `_InitializeBucketAsync` 任务；等待者一直 Pending，原型只在局部变量里，尚未写入 `bucket.Prototype`，没有释放。后续 ClearPool 虽能关闭该桶，却没有原型引用可以归还。
- 复现输出：`rent=TimeoutException, pools=1, holds=1`；调用 Clear 后 `clear=success, rent=ResourceLoadException, shutdown=TimeoutException, prototype-released=0`。
- 影响：默认配置下用户会等到请求超时；原型持有计数仍永久非零，正常 Shutdown 永远不能完成。
- 设计依据：§15.3 第 3 条（2527 行）明确 IsAlive/CanCreate/GetInstanceKey 抛异常需按阶段归类并走清理路径；§7.3（1159 行）明确检查不通过要失败等待者、回退原型和移除桶。
- 修正方向：原型获取之后用明确的所有权转移/try-finally 保护本地 `ResourceRef`；捕获 CanCreate 异常，失败所有等待者，归还原型，终结桶。只有写入 `bucket.Prototype` 成功后本地所有权才转交桶。

### R10 P1 容量申请将已写字节再次当成未写预留，能装下的文件被拒绝

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[FileCache.cs:773](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/FileCache.cs:773)，对应 `EnsureReservationAsync` 第 1085–1106 行。
- `EnsureReservationAsync(requiredRemaining)` 要求的是尚未写入的预留；每块成功后代码已经把该块从 R 转入 A。下一块却传入 `written + read + metadata`，把历史写入再次计入 R。
- 可复现输入：数据 131072 字节，块大小 65536，元数据预留 1024，容量 180000。实际所需 132096，小于容量；第二块申请时 A=65536、R=66560，要求 R=132096，导致虚假的 `cache.capacity_exceeded`。默认块大小下的大型 AssetBundle 同样受影响，还会无必要地淘汰其他缓存。
- 基准：§10.5 `A + R <= M` 中 R 是“尚未落盘的容量预留”；§10.6 每块只申请本次新写入的所需空间。
- 修复：申请本块 `read + metadata` 的剩余预留，或统一为“总承诺”接口并内部扣除已写部分，不能混用两个口径。增加“多块但总长度明确低于预算”的成功用例。
- 复现方法：`Probe_DiskBudgetFitsButFails`。

### R11 P1 取消关闭等待会终止真正关闭，再次关闭却直接成功

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[FileCache.cs:1561](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/FileCache.cs:1561)，1578–1591 行。
- `Interlocked.Exchange` 后的后续调用直接 `return`，没有等待共享关闭任务。第一次调用把实际收尾放在带调用者取消的 await 后面；该等待取消后，刷新访问元数据、Closed 状态和 `_lockHandle.Dispose()` 永远没有执行方。
- 触发：持有一个 FileLease 时开始关闭，第二个 `ShutdownAsync()` 已成功返回；取消第一个关闭等待，再归还 lease，缓存仍在 Closing，下一实例初始化同一目录报 `cache.root_in_use`。
- 影响：违反“ct 只取消等待”的 API 语义；正常宿主销毁/超时可能永久泄露目录独占权，直到进程退出。
- 基准：§15.2 `ShutdownAsync` 等待所有 pin/事务归零后释放根锁；取消关闭等待不撤销关闭。§10.7 同根独占锁必须保持到实际关闭完毕。
- 修复：独立、不可被调用者令牌中断的唯一关闭流程；每次调用都仅用自己的令牌等待这同一个流程。
- 复现方法：`Probe_DiskShutdownCancellationLeaksLock`。

### R12 P1 在下载完成、提交前关闭会丢掉请求完成信号

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[FileCache.cs:828](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/FileCache.cs:828)，同类分支 991–1006；作业 finally 646–666。
- `RunAttemptsAsync` 发现缓存关闭后直接 return；外层 finally 只清文件、移除作业、完成 Terminal，不结算尚未取消的 `job.Waiters`。只有异常路径会 `_FailJobWaitersNoLock`。
- 触发：完整正文读取结束，在响应关闭与提交检查之间开始 `FileCache.ShutdownAsync()`。可以在可控传输的 Dispose 回调触发该交错，等价于并发线程在这个窗口调用关闭。
- 结果：缓存已 Closed、Jobs=0，原 `AcquireAsync` 仍永久未完成。提交期间的 abandoned 分支也可能出现同一结果。
- 基准：§10.6 `AbandonJob` 要将 Pending 等待者按自身取消或关闭错误全部结算；每个获取请求只有一次终态。
- 修复：关闭作业的所有正常提前返回统一经过“结算剩余等待者→清理→完成 Terminal”；不能把“所有调用者取消”和“缓存主动关闭”视为同一个已经无等待者的情况。
- 复现方法：`Probe_DiskShutdownAtCompletedDownloadLeavesWaiter`。

### R13 P1 重启后 generation 从零重用，与仍被旧租约使用的文件路径冲突

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[FileCache.cs:450](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/FileCache.cs:450)，恢复 299–365 行，提交 905 行。
- 恢复读取并保留已有 `record.Generation`，但从未推进 `_generationSeed`。新实例第一个作业又分配 generation=1，生成与上一实例相同的不可变代次路径。
- 触发：第一实例下载 generation=1 并关闭；第二实例恢复并持有该文件 lease；TTL 到期后获取同一身份。旧文件因 pin 必须保留，新下载 MoveFile(overwrite:false) 必然碰撞。
- 影响：刷新失败；普通 IOException 还被误报网络中断并重试。未 pin 时旧删除与新提交竞速，因此不能认为旧文件“通常会先删掉”就安全。
- 基准：§10.3 FileLease 绑定实际代次；§10.7 数据文件使用不可变代次路径，不能与旧代次相同。
- 修复：持久唯一代次，例如 GUID，或恢复所有受管理路径的最大代次后继续递增，同时确保孤立/失败残留也不会发生碰撞。
- 复现方法：`Probe_DiskGenerationReuseAfterRestart`。

### R14 P1 元数据没有实施大小上限，可在成功提交时突破总容量

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[FileCache.cs:912](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/FileCache.cs:912)，`_WriteMeta` 第 1730–1757 行，访问刷新 1519–1541 行。
- 下载只按 `MetadataAllowanceBytes` 预留，但 `_WriteMeta` 直接向磁盘序列化任意长度身份/路径，从未验证写入量不超过 allowance；完成后才把实际长度转入 A/R，甚至可把 R 减为负。
- 可复现：MaxBytes=128、MetadataAllowanceBytes=8、正文 8 字节，配置通过验证，元数据数百字节仍成功提交，A 超过 M。使用默认 16 KiB 上限但较长身份字符串同理。
- 基准：§9.1 allowance 是“每文件最大”而非估算；§10.5 元数据必须有上限并提前预留；X10 MetadataBudget。
- 修复：先在有界内存缓冲序列化并验证长度，或用受限写入器在每次写前申请预算；超限失败时不能先写出超限字节。下载提交和访问刷新采用同一实现。
- 复现方法：`Probe_DiskMetadataExceedsBudget`。

### R15 P1 失败的元数据临时文件既不清理也不计量，重启也遗漏

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[FileCache.cs:912](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/FileCache.cs:912)，失败清理 1244–1283 行，恢复 266 行。
- 作业只记录一个 `PartPath`（已移动的数据文件）。`metaTmp` 写出后若重命名失败，外层只清数据；元数据临时路径没有进入所有权和 A/R 核对。最终所有预留释放，临时元数据留在 `meta/<key>/...tmp-*`。
- 启动扫描只读取 `.meta`，随后仅扫描 `data` 和 `staging`，也不会把这些 `.tmp-*` 或 `.access.tmp` 残留纳入 Garbage。访问刷新失败同样不清理/核对临时元数据。
- 可复现：注入 metadata MoveFile 失败，正文被清理但实际目录仍有数百字节；快照 A=0、R=0。
- 基准：§10.5 A 包含临时文件和失败残留；§10.7 恢复矩阵明确临时元数据需要入账并回收。
- 修复：下载/元数据刷新事务都登记全部拥有的候选路径；成功明确转交，失败逐个核对实际残留并转为 Garbage。启动完整扫描受管理路径，不能靠扩展名只统计成功记录。
- 复现方法：`Probe_DiskMetadataFailureLeaksUnaccountedFile`。

### R16 P1 部分删除失败后，重试会重复扣已删除数据长度

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[FileCache.cs:1225](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/FileCache.cs:1225)，1184–1192 行。
- 删除数据成功但元数据失败时，`entry.Length` 因 `FileExists(data)==false` 而保留旧长度，`MetaLength` 通过剩余总量减旧长度推算。下次重试时，“数据已不存在”再次按旧 Length 计入 FreedBytes；若元数据继续删除失败，A 再次下降，而真实文件大小完全没变。多次可出现负账本。
- 触发：数据文件允许删除、仅元数据文件持续失败；推进 5 秒退避后再次 Trim。现有 F12 测的是相反方向“数据失败、元数据成功”，覆盖不到。
- 基准：§10.5 删除成功后才能扣减且每份字节只能扣一次；F12 DeleteFailure。
- 修复：按每个实际路径分别维护已核算长度，删除确认后立即将该路径计量置零；不可从合计量倒推数据/元数据分配。
- 复现方法：`Probe_DiskPartialDeleteDoubleDeducts`。

### R17 P2 AB 加载 catch 把实际故障吞成 asset.not_found

- 证据类型：已核对源码控制流；未执行对应 Unity 或故障注入场景。
- 位置：[SimpleAssetBundleLoader.cs:289](D:/Workspace/Porjects/GameToolKit/Unity/Assets/UnityToolKit/Runtime/Resource/Loaders/SimpleAssetBundleLoader.cs:289)，289–305；二次包装 230–235；外层回退 144–147。
- 触发：远端 bundle 的 DNS/HTTP/磁盘错误，依赖环错误，或者本地开包异常。
- 事实：catch 不保存 exception，清理后 return null；上层统一制造没有 Cause 的 asset.not_found。外层 catch 中 ReleaseBundleAsync 一旦抛异常也覆盖原始错误。
- 影响：用户错误类别可能改变，开发者不能从同一失败看到 DNS/HTTP/路径/依赖错误；“用户粗粒度、开发者可排查”的核心需求失效。
- 偏离：§8.3 文档 1386、1399；§11 同一失败保留原始证据与 RelatedErrors。
- 最小修正：共享任务保留原始 ResourceLoadException；回退失败加入 RelatedErrors 并正确提升 Cleanup，而不是 return null 或覆盖首个原因。依赖环使用明确稳定诊断码。
- 验收：远端包 DNS 故障仍保留 network.dns_failed / Download / Cause；依赖环保留 bundle.dependency_cycle；回退失败不替换主错误。

### R18 P2 本地加载忽略已选择的解码器，两个文件加载器都丢失解码参数

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[LocalFileLoader.cs:35](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Loaders/LocalFileLoader.cs:35)（35–41、51–53）；[RemoteFileLoader.cs:48](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Loaders/RemoteFileLoader.cs:48)（48–56、70）。
- 触发：注册一个输出 string 的自定义 decoder，通过 Parameters 指定其 Id；或者解码器需要传入格式/裁切等参数。
- 事实：Local Resolve 已用显式 decoder.Id 形成 LocalKey，但 Payload 仅保存 path；Load 再 Resolve(null, type)，选择默认 decoder。两加载器调用 DecodeAsync 时都固定传 null；非 string Parameters 被静默忽略。
- 影响：缓存键宣称某 decoder 的结果，实际运行另一 decoder；扩展参数既未验证也未传递，用户无法正确扩展解码行为。
- 偏离：§3.3 文档 247、251；§8.2 文档 1328、1335。
- 最小修正：定义文件加载器自己的不可变参数/载荷，保存已选择 decoder 和解码参数快照；影响结果的参数进入 LocalKey；不支持的参数明确拒绝。
- 验收：指定自定义 decoder 的调用计数为 1、默认 decoder 为 0；不同解码参数形成不同内存身份但相同原始文件仍共用下载。

### R19 P2 GameObject DestroyAsync 超时后正常成功，未确认销毁

- 证据类型：已核对源码控制流；未执行对应 Unity 或故障注入场景。
- 位置：[GameObjectInstanceFactory.cs:114](D:/Workspace/Porjects/GameToolKit/Unity/Assets/UnityToolKit/Runtime/Resource/GameObjectInstanceFactory.cs:114)，114–125。
- 触发：在给定期限内 Unity 对象未被确认销毁，或 Environment.TickCount 加 10000 发生有符号溢出。
- 事实：循环到期限后直接结束 Task，不抛错误；调用方把成功完成理解为对象已经不存在。
- 影响：池会减少 Destroying 并可能释放原型/AssetBundle，实际对象状态仍未确认。超时后正常成功违反了明确的销毁完成语义。
- 偏离：§3.5、§7 池销毁确认与故障隔离；§14 P02/P07。
- 最小修正：使用安全的单调时间差或 Stopwatch；超时抛带 Incomplete 的 instance.destroy_failed，让池保留原型与隔离记录。
- 验收：注入永不完成的销毁/可控时钟，超时进入故障状态，不能释放原型。

### R20 P2 自动归还组件重新绑定后仍保留 detached 状态

- 证据类型：已核对源码控制流；未执行对应 Unity 或故障注入场景。
- 位置：[InstanceAutoReturn.cs:25](D:/Workspace/Porjects/GameToolKit/Unity/Assets/UnityToolKit/Runtime/Resource/InstanceAutoReturn.cs:25)，25–43。
- 触发：lease A 绑定后 Detach、主动归还；对象复用时 Bind(lease B)，随后业务 Destroy 对象。
- 事实：Detach 永久置 `_detached=true`，Bind 不重置；OnDestroy 直接 return，不 Dispose B。Bind 覆盖旧 `_lease` 也未验证先解除旧绑定。
- 影响：租约 B 仍登记在用，原型无法释放，Shutdown 等待未归还租约。
- 偏离：§12.2 具体租约自动归还及池复用生命周期。
- 最小修正：Bind 要验证不存在旧有效绑定并重新启用该次绑定；Detach 原子清除旧绑定，明确“只解绑”与“归还”的不同职责。
- 验收：Bind A → Detach → Dispose A → Bind B → Destroy，B 归还恰好一次。

### R21 P2 LoadError 默认承诺清理完成，与隔离安全默认值冲突

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[ResourceErrors.cs:133](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Definition/ResourceErrors.cs:133)，136 行默认 `CleanupStatus.Complete`；WithRelated 159 保持原 Cleanup。
- 触发：用户加载器用最简 `new LoadError(code, stage)` 包装未确认清理状态的异常；或 Complete 主错误追加 Incomplete 回退错误。
- 事实：enum 的 0 为 Unknown，但构造函数默认参数改成 Complete；WithRelated 不反映回退失败后的整体状态。ResourceStore 399 行只检查 error.Cleanup 来决定是否隔离。
- 影响：没有明确确认的回退被视为已经完成；同键可重新加载并跳过隔离，与开放加载器扩展的契约风险直接相关。
- 偏离：§3.4/§8.2/§11 CleanupStatus 的显式清理状态，未知故障保守隔离。
- 最小修正：构造默认值设 Unknown 或强制必填；添加关联清理失败时显式构造总体 Incomplete 错误。不是仅靠 enum 默认值来表达契约。
- 验收：缺少 Cleanup 声明的扩展错误进入隔离；关联回退失败不会让主条目成为可重试的 Removed。

### R22 P2 内置字节/图片解码器将主动取消包装成文件 I/O 故障

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[IResourceDecoder.cs:131](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Decoding/IResourceDecoder.cs:131)（131–137）；[UnityImageDecoder.cs:44](D:/Workspace/Porjects/GameToolKit/Unity/Assets/UnityToolKit/Runtime/Resource/Decoding/UnityImageDecoder.cs:44)（44–50）。
- 触发：ReadAllBytesAsync 正在读盘时操作取消，或解码入口收到已取消令牌。
- 事实：catch(Exception) 包住 OperationCanceledException，_ReadError 将其归为 file.io_failed。已用当前构建的 Tools.dll 最小程序实测：已取消令牌得到 ResourceLoadException/file.io_failed，而非 OCE。
- 影响：ResourceStore 不再走正常取消分支，而当作失败处理，可能生成不该有的故障诊断。图片解码器后半段的 OCE catch 无法保护前面的读盘 try。
- 偏离：§11.1/§11.4，§14 E06 CancelIsNotFailure。
- 最小修正：在读盘 catch(Exception) 前单独传播关联令牌的 OperationCanceledException；未知来源的 OCE 按文档单独分类。
- 验收：读取取消返回 OCE，故障 Report 数为 0。

### R23 P2 原型在获取后失效的 catch 分支丢失引用

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[InstancePool.cs:309](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Instancing/InstancePool.cs:309)，重点 309–313。
- 流程：原型在 Store 发放 `ResourceRef` 时有效，进入池的 `prototype.Value` 检查前被外部销毁。复现使用可控 IsAlive：首次返回 true，第二次返回 false。
- 实际行为：catch 调用 `_OnPrototypeAcquired(bucket, null, ex)`，主动把已取得的 `prototype` 换成 null。递归错误分支的 `prototype?.Dispose()` 因而不执行；桶移除但资源持有仍为 1。
- 复现输出：`rent=ResourceLoadException, pools=0, holds=1, shutdown=TimeoutException, released=0`。
- 影响：一次可解释的外部对象失效变成永久泄漏和关闭挂起。
- 设计依据：§7.3 原型失败回退；§15.4 所有权必须闭环。
- 修正方向：先 Dispose 原始 prototype，或把原有引用带入统一的失败清理方法；避免用递归调用加 null 参数模拟状态转换。可与 R09 使用相同所有权保护修复。

### R24 P2 诊断回调异常中断 FaultBucket，ClearPool 永远得不到终局

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[InstancePool.cs:802](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Instancing/InstancePool.cs:802)，重点 802–811；同类风险分布于核心中的直接 `_diagnostics.Report` 调用。
- 流程：注入一个 Report 抛异常的 IResourceDiagnostics；实例 DestroyAsync 失败；调用 ClearPool。
- 实际行为：`_FaultBucket` 已把状态设为 Faulted，但 802 行 Report 抛出后，809 行 `Closed.TrySetException` 和后续清理/关闭检查未运行。未来 ClearPool 遇到 Faulted 直接返回同一个永远未完成的 Closed 任务。
- 复现输出：`clear=TimeoutException, bucket=Faulted, shutdown=AggregateException`。
- 影响：可替换的诊断实现故障会破坏主生命周期；异常也可能从同步 Post 清理回调穿透到业务 Dispose。这个问题不应要求所有注入的日志接收器永不失败才能保证资源安全。
- 设计依据：§11.4（2193 行）明确日志监听器抛异常不得破坏成功加载，不重入原故障链；§11.5（2221 行）要求 Dispose 只提交释放意图，后台失败由维护/Shutdown 报告。
- 修正方向：装配时包一层不抛出的诊断分发器，隔离 Report/Trace 观察者异常；先提交所有状态及 TCS 终局，再做诊断通知，观察者故障使用独立兜底出口，禁止递归调用同一坏监听器。

### R25 P2 加载器独立内存容量策略被忽略，配置为不缓存仍被缓存

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[ResourceStore.cs:606](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Core/ResourceStore.cs:606)，关联 719–742。
- 流程：系统使用默认 Memory.MaxIdleEntries=128，给某加载器注册 `LoaderPolicy.Memory = new MemoryPolicy { MaxIdleEntries = 0 }`；加载并释放一个资源。
- 实际行为：建条目时保存了加载器 policy，但 Release 只读取其 IdleLifetime；是否缓存读取 `_defaultMemory.MaxIdleEntries`，预算淘汰也完全读取 `_defaultMemory`。加载器的 MaxIdleEntries 和 MaxEstimatedIdleBytes 没有被消费。
- 复现输出：`released=0, rows=1, state=Idle`。
- 影响：用户明确要求不保留某加载器的资源，实际仍缓存；加载器的容量配置表面可设、实质不生效。
- 设计依据：§9.1（1432–1436 行）LoaderPolicy.Memory 是完整策略，MaxIdleEntries=0 表示不保留；§9.2（1455 行）初始化合并具名加载器配置。
- 修正方向：先明确并实现容量预算的作用域（系统总预算和加载器预算可以同时约束），至少对条目策略 MaxIdleEntries=0 执行归零即卸载。若设计实际只允许按加载器覆盖 TTL，就必须收窄公开类型和文档，不能留无效配置。

### R26 P2 IsAlive 抛异常被当成已销毁，成功创建的实例不再清理

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[InstancePool.cs:944](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Instancing/InstancePool.cs:944)，关联 555–565。
- 流程：CreateAsync 成功转交一个活对象，紧随其后的工厂 IsAlive 抛异常。
- 实际行为：`_FactoryIsAlive` 报告日志并返回 false；`_OnCreateCompleted` 以无效结果失败等待者，既不保存该对象记录，也不调用 DestroyAsync。后续 Shutdown 正常放掉原型。
- 复现输出：`rent=ResourceLoadException, destroyed=0, shutdown=success, prototype-released=1`。
- 影响：观察存活状态失败并不能证明对象已不存在；实际对象泄漏，依赖却提前释放。Return 以及 Idle 检查分支使用同一 helper，同样会把查询异常误当成已销毁。
- 设计依据：§15.3 第 3 条要求查询异常按当前流程清理；§7.3 返回非空实例已经发生所有权转移，必须追踪或销毁。
- 修正方向：区别“确认不存活”和“无法检查存活”。后者保留对象记录，按工厂销毁协议尝试清理；清理失败转故障隔离。不要用 bool false 吞掉查询异常。

### R27 P2 旧代池销毁失败时，同 PoolKey 的新池仍接受后续租用

- 证据类型：隔离程序已复现相关关键行为。
- 位置：[InstancePool.cs:785](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Instancing/InstancePool.cs:785)，关联 170–178。
- 流程：租用旧桶实例；ClearPool 让旧桶 Closing 但实例尚未归还；再次 Rent 建立同 PoolKey 新桶；归还旧实例时 DestroyAsync 失败；再发起第三次 Rent。
- 实际行为：`_FaultBucket` 只 TryAdd 旧桶到 `_currentBuckets`，因新桶已存在而失败。JoinOrRent 只检查当前新桶的状态，因此忽略 trackedBuckets 中同键旧桶的 Faulted 屏障，第三次 Rent 成功。
- 复现输出：`old-close=ResourceLoadException, new-rent=success, faulted-pools=1`。
- 影响：文档承诺的“同 PoolKey 清理故障阻止新租用”在允许新旧代际重叠的正常操作下失效。
- 设计依据：§7.4（1240–1245 行）FaultBucket 保留同 PoolKey 故障屏障、拒绝新的租用。旧代归还应绑定原桶这一点实现正确，但隔离不能依赖旧桶恰好还占当前桶索引。
- 修正方向：将 PoolKey 故障标记独立于当前桶索引保存；故障发生后阻止新申请，已交付租约继续按原桶归还。同步明确如何终结同键新桶的待交付工作，避免其继续交付。

### R28 P2 配置没有冻结和完整验证

- 位置：[ResourceManager.cs:86](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/ResourceManager.cs:86)、[ResourceManager.cs:118](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/ResourceManager.cs:118)、[FileCache.cs:185](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Files/FileCache.cs:185)。
- 设计依据：§3.3、§9.2 要求初始化合并并冻结不可变策略快照；负容量和非法时间应在配置阶段拒绝。
- 实际行为：管理器、加载器注册项、工厂注册项和文件缓存直接保留传入的可变配置对象。管理器只验证系统默认 Memory/Pool，注册的 LoaderPolicy/PoolPolicy 没有相应验证。
- 动态复现：首次加载后，将外部 options.Memory.IdleLifetime 从 10 秒改成 0，释放立即执行，说明运行中条目仍受外部对象修改影响；注册负并发数及 MaxIdleEntries=-1 也可以成功加载。
- 影响：同一资源生命周期内策略会漂移。文件缓存的 Directory 若被修改，组件仍持有旧目录的独占锁，却可能按新目录计算后续路径；这已经影响所有权边界。
- 修正方向：复制并验证框架已知配置，合并为内部不可变快照；首次请求后只读。扩展 Parameters 遵循其自身不可变契约，不要求框架深拷贝任意用户对象。
- 验收：构造/注册后修改原配置不影响实际行为；系统、具名加载器、具名工厂的非法容量/时间都在装配阶段报错。

### R29 P2 省略加载器策略时没有采用默认四路并发

- 位置：[ResourceManager.cs:32](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/ResourceManager.cs:32)。
- 设计依据：§9.1 明确 LoaderPolicy.MaxConcurrentLoads 默认 4，按实际底层加载计数。
- 实际行为：policy 为 null 时 max 取 0，LoadSlots 为 null，等价于无限并发；只有显式 new LoaderPolicy() 才得到 4。
- 动态复现：按常规 RegisterLoader(name, loader) 注册，发起 8 个不同资源请求并阻塞底层完成，8 个底层加载全部已经启动。
- 修正方向：缺省注册项先生成并验证默认策略，再创建并发槽位；明确无限并发的显式配置方式。
- 验收：不传策略时完成前最多启动 4 个加载；第 5 个在释放槽位后启动，同一资源的多个等待者只占一次。

### R30 P2 注入的用户错误映射器没有参与实际映射

- 位置：[ResourceManager.cs:88](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/ResourceManager.cs:88)、[ResourceErrors.cs:149](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Definition/ResourceErrors.cs:149)。
- 设计依据：§11 允许业务替换用户错误类别映射，并保持开发者诊断内容不变。
- 实际行为：构造函数保存 _errorMapper，后续没有任何使用；LoadError 直接调用 DefaultErrorMapper 的静态映射。
- 动态复现：注入总是返回 StorageFull 的测试映射器，触发加载器未注册错误；映射器调用数为 0，最终仍是默认 OperationUnavailable。
- 修正方向：在统一错误交付/发布边界应用注入的映射器，保留 DiagnosticId、Cause、Context 和关联错误；同一共享故障的各等待者使用同一映射结果。
- 验收：自定义映射对路由、加载器、下载和释放错误都有效；调整用户类别不改变诊断证据。

### R31 P2 重复注册会静默替换已有加载器和工厂

- 位置：[ResourceManager.cs:118](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/ResourceManager.cs:118)、[ResourceManager.cs:139](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/ResourceManager.cs:139)。
- 设计依据：§3.1 明确同名 Register 报错，只有配置期的 ReplaceLoader 才表达替换。
- 实际行为：RegisterLoader/RegisterFactory 直接通过字典索引赋值，没有重复检测。
- 动态复现：连续注册两个同名加载器无异常，实际加载只调用第二个。注册表冻结本身已经实现，这个问题发生在冻结之前。
- 修正方向：Register 使用拒绝重复的添加语义；Replace 保持显式替换语义，并清理被替换注册项自有的并发控制对象。
- 验收：重复 Register 明确失败；配置期 Replace 成功；首次请求后两者都被禁止。

### R32 P2 清理空闲资源接口忽略取消等待的令牌

- 位置：[ResourceManager.cs:422](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/ResourceManager.cs:422)，关键为第 429 行。
- 设计依据：§3.1 维护 API 的取消参数只取消调用者等待，不回滚已开始的清理。
- 实际行为：UnloadUnusedAsync 的 cancellationToken 完全未使用，直接等待 Task.WhenAll；同文件 ClearPoolAsync 已使用可取消等待。
- 动态复现：使底层 ReleaseAsync 等待手动栅栏，取消 UnloadUnusedAsync 的调用令牌后任务仍未完成；只有放行释放才正常成功返回。
- 修正方向：底层清理继续使用自己的生命周期，外层等待使用与 ClearPoolAsync 相同的取消等待逻辑。
- 验收：调用者取消后立即收到 OCE，底层释放仍继续并且只执行一次。

### R33 P2 工厂切换原型加载器后无法按原请求清理实例池

- 位置：[InstancePool.cs:187](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Instancing/InstancePool.cs:187)、[InstancePool.cs:828](D:/Workspace/Porjects/GameToolKit/Unity/Assets/ToolKit/Tools/Common/ResourceSystem/Instancing/InstancePool.cs:828)。
- 设计依据：§3.5 允许工厂 GetPrototypeRequest 返回另一加载器的原型请求；§7.3 要保存原始业务 loader/address/factory 与池的关联。
- 静态触发：RentAsync 使用业务加载器 A，工厂将原型映射到加载器 B。建桶时 AssociationLoader 保存 protoRegistration.Name，也就是 B；随后 ClearPoolAsync(address, loader:A, factory:...) 按 A 查找。
- 影响：原调用使用的清池参数找不到池，接口成功返回但闲置实例和原型仍保留。错误来自业务关联和原型身份混用，不能通过要求所有工厂永远使用同一加载器规避扩展契约。
- 修正方向：池身份继续使用已解析原型身份；维护接口另外保存原始业务请求关联，二者不混用。同一池若可由多个业务地址到达，应维护全部有效关联。
- 验收：跨加载器的原型映射仍能通过原 Rent 请求参数清池，并覆盖当时已存在的全部代际。

### R34 P2 文档承诺的绑定和作用域管理尚未交付

- 设计依据：[方案第 12 章](D:/Workspace/Porjects/GameToolKit/Docs/ResourceSystemRewrite/资源系统重写实施方案.md:2225)及第 15 章实施清单。
- 代码范围：当前 Unity/Assets 下未找到 ResourceBinder、ResourceScope、IResourceApplicator、BindingResult 或等价的 target+slot 绑定管理实现；[UnityResourceHost.cs:75](D:/Workspace/Porjects/GameToolKit/Unity/Assets/UnityToolKit/Runtime/Resource/UnityResourceHost.cs:75) 只负责启动管理器关闭。
- 影响：文档中的后发请求优先、应用失败保留旧展示、先解绑再释放、独立槽位和作用域逆序清理没有实现。它们与核心 LoadAsync 正确与否是不同的验收范围。
- 修正方向：若本轮声明完整实施文档，则补齐该集成层；若明确缩减首版范围，应同步调整交付清单与使用示例。不要在核心加载状态机中补 UI 生命周期逻辑。
- 验收：E09–E13、X09 应在真实绑定实现上执行；协程/回调适配若交付，还需 E14/E15。可选 Addressables/YooAsset 适配器未内置不属于本项缺失。

## 4 验证结果与验收缺口

### 已执行的验证

现有测试通过 dotnet test 的 ResourceSystem 筛选执行，结果为 64 通过、0 失败、0 跳过。审查还运行了三个隔离复现程序：管理入口与文件缓存、核心与实例池、文件加载器。复现仅使用假加载器、可控 TaskCompletionSource、临时目录和可注入文件系统，不请求真实 CDN，不修改项目业务源码。

[复现与测试证据](D:/Workspace/Porjects/GameToolKit/Docs/ResourceSystemRewrite/资源系统Review验证记录.json)保存了现有测试统计、隔离复现输出和审查源码哈希。这里“复现成功”表示观察到了报告指出的偏差，不表示对应功能通过验收。

这些结果不能直接换算成设计文档 75 项验收用例的完成比例：现有测试包含新增场景，也有编号与文档不一致。例如 FileCacheTests 的 F07 实际测试 UnknownLength，而文档 F07 是 ParallelReservations。必须按场景与断言核对，不能只数测试名。

### 现有测试未证明的关键条件

- ResourceStoreTests 的 C17 使用立即完成的 FakeLoader 释放操作，第二次请求开始时旧卸载已经完成，没有真正覆盖“释放尚未结束时再次请求”。应给 ReleaseAsync 加可控栅栏。
- C08 等核心用例只证明同一 ResourceEntry 的调用者取消互不影响，不能证明同一 AB 容器内不同资源键的取消互不影响。
- 多数文件测试使用小文件和成功关闭路径，没有覆盖本次复现的多块预留、重启代次复用、元数据事务失败、关闭等待取消和部分删除重复扣账。
- P01 使用假实例，不能证明默认 GameObject 工厂复用后的 activeInHierarchy，也不能验证 Unity 原生异步请求取消后的实际结果回收。
- 现有异常测试没有覆盖 CanCreate/IsAlive/诊断回调异常，也没有覆盖 Closing 与 Create 失败、OnReturn 重入的组合。
- §12 的绑定行为没有对应实现可供执行验收。不能仅添加空壳类或同名测试来宣称完成。

正式回归应优先转入本次隔离复现，并补 Unity PlayMode 用例：同包不同资源共享取消；不可取消 Unity 请求迟到完成；延迟 Unload；默认 GameObject 二次租用；Destroy 确认；自动归还组件的重新绑定。

### 交付可重复性

本次测试使用当前工作区，其中 ToolKit/Tests/ResourceSystem/Fakes.cs 与 InstancePoolTests.cs 在审查开始时尚未被 Git 跟踪。64 项通过不能证明仅检出 fa3e34b 就能得到相同测试集。提交完整测试文件后，应从干净检出再次执行既定检查。

## 5 设计文档也需要同步澄清的地方

1. **C17 的验收文字与主体算法冲突。** §5、§6 规定 Draining/Unloading 期间保留同键屏障，后来的请求等待旧操作结束；§14 C17 却写“新请求创建新记录”，容易被理解为允许并行开新代。本次按主体状态机和伪代码审查，应把 C17 改成“等待旧卸载完成后创建新记录，旧完成不能删除新记录”。不能据此把正确的 ResourceStore 屏障判成偏差。
2. **明确内存容量策略的统计作用域。** 文档允许 LoaderPolicy.Memory 覆盖，当前 API 也公开完整 MemoryPolicy；实现只支持其中一部分。应说明系统总预算与具名加载器预算如何同时生效，或者明确收窄可覆盖字段。0 表示不保留的既有契约仍应遵守。
3. **确认首版集成范围。** 若决定暂不交付 Binder/Scope、协程/回调适配，应作为明确范围变更更新第 12、14、15 章。模块的命名、文件摆放或未复用 KeyedAsyncLock，不属于需要修改设计的理由。

## 6 建议修复顺序

1. **先固定所有权和结束条件。** 修复 Unity 迟到结果、AB 容器独立操作和卸载屏障、实例池创建/归还/初始化错误分支、FileCache 关闭任务与等待者结算。每条异步路径先有明确的最终持有者，再允许完成请求或释放依赖。
2. **修复磁盘身份和容量约束。** 默认 URL 身份、可变资源有效性、跨重启唯一代次、数据和元数据统一预留、失败残留入账、逐路径删除计量一起校验。每次成功/失败/重启后都核对实际文件与账本。
3. **兑现公开契约。** 配置快照、默认并发、具名内存策略、错误映射、解码参数、查询异常、重复注册和维护取消。没有生效能力的选项不应继续作为可用功能暴露。
4. **补齐集成与正式验收。** 修复 GameObject 复用和销毁确认，落实或明确延期绑定层，提交现有测试与本次回归，再执行纯 .NET 和 Unity 集成验证。

修复的完成标准是原有模块承担其已声明的责任：加载器管理来源内部所有权，资源仓库管理共享持有，实例池管理具体实例和原型，文件缓存管理文件及真实容量。增加更多接口或状态名称本身不能解决这些偏差。
