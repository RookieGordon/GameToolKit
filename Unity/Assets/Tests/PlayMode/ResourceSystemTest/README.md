# ResourceSystem PlayMode 测试

独立程序集：`ResourceSystem.PlayModeTests`。在真实 Unity 帧循环中运行并覆盖 `UnityExecutionContext`，无需场景、外部网络服务或项目业务配置。

最初的 49 项用例全部通过，随后新增 27 项并发、取消及释放边界用例、15 项远程配置用例，以及本次修复对应的 3 项边界用例，当前共 94 项，全部通过。所有运行均使用当前资源系统源码的副本，在隔离 Unity 6000.6.7f1 工程执行；原项目的其他程序集和 Player 构建不在验证范围内。

## 覆盖范围

| 文件 | 用例数 | 主要行为 |
| --- | ---: | --- |
| `ResourceLoadPlayModeTests.cs` | 14 | 初始化顺序、失败重试、同键合并、独立取消、失败接续、迟到结果清理、参数/类型/来源身份、进度异常隔离、引用与选择卸载、转换与释放线程、并发、排队超时、外部策略快照、替换与退出、卸载残留重试 |
| `ResourceInterleavingPlayModeTests.cs` | 11 | A 取消后立即 B、串行/并行执行名额、排队 A 撤销、共享 A 独立取消、旧 A 未退出时新 A 与 B 接续、迟到失败、跨来源隔离、回放/诊断异常隔离、回放中同步取消并重入后的等待者清理 |
| `ResourceConversionBoundaryPlayModeTests.cs` | 8 | 转换期间取消后加载 B、转换器响应/不响应取消、共享转换独立取消、退出时迟到产物清理、中间产物释放失败、无效产物释放与残留重试的线程、多线程重复归还与并发卸载 |
| `UnityResourceAdapterPlayModeTests.cs` | 8 | 后台线程切回 Unity 主线程、异步接续、异常/取消、真实 Resources 文本加载与卸载重载、PNG 转纹理、可读性、跨帧销毁、非法图片 |
| `ResourceInstancePoolPlayModeTests.cs` | 8 | 实例激活/停用/复用、重复归还、闲置容量、零容量、清池与忙碌租约、清池后新桶、原型等待、创建中取消、系统退出后的晚归还 |
| `ResourcePoolFailurePlayModeTests.cs` | 6 | 共享原型独立取消、原型 A 取消后 B 成功、Create/OnRent/OnReturn/Destroy 失败清理与恢复、销毁残留持有原型、选择性清池隔离 |
| `ResourceFilePlayModeTests.cs` | 13 | 本地字节/UTF-8/空文件、错误诊断、共享下载、启动清理/容量/TTL、跨系统缓存、取消 A 后 B、同 URL 旧轮退出后新轮、借用缓存的系统退出隔离、owner Stop 等待真实填充清理 |
| `ResourceManagerPlayModeTests.cs` | 11 | Config → Options → Manager、关闭初始化创建开关后的注册与策略、同来源后项覆盖、初始化失败重试、Task/协程接口、成功/失败/取消回调、A 取消后 B 成功且迟到 A 不覆盖界面、预热预算与取消 |
| `RemoteFileLoaderConfigPlayModeTests.cs` | 15 | 自动初始化与关闭、本机 HTTP 下载/磁盘复用、目录/容量/TTL 快照、后注册与外部所有权、失败/取消重试、同来源覆盖、关闭回调快照/逆序/失败继续、Manager 关闭/销毁、等待迟到填充清理、0/1 次下载重试与参数快照、连接/读取超时 |

## 当前回归结果

2026-11-04，修复无效产物释放与进度回放取消遗漏后，完整 PlayMode **94/94 通过，0 失败、0 跳过**；原两项失败测试保留原断言并通过。
结果：`Temp/ResourceSystemFailureFixPlay/results.xml`；同目录 `tests.log` 保存编译与运行日志。
同时运行配置和 Manager 的完整 EditMode 测试 **48/48 通过**；结果为 `Temp/ResourceSystemFailureFixEdit/results.xml`。
直接链接当前核心源码的 .NET ResourceSystem 行为测试也 **32/32 通过**；结果为 `Temp/RS/CurrentRegression/results/ResourceSystem.Regression.trx`。
全部下载参数扩展前先运行字段序列化红测，确认因缺少 `MaxRetries` 失败（`Temp/ResourceSystemDownloadConfigRed/results.xml`）；接线后该测试及 7 项边界、快照测试全部通过。
连接/读取超时使用 1000 毫秒配置，资源请求总超时设为不限并禁用重试，分别在响应头与正文阶段暂停服务器响应；两项均以网络超时结束，证明配置作用于当前 Unity 的实际异步传输。

## 交叉加载的验证边界

新增时序使用 `TaskCompletionSource` 门闩以及后端进入/退出序号控制，不以固定延时或随机压力推测是否发生竞态。

- **合作后端，串行来源**：取消 A 后立即请求 B，A 退出后 B 使用同一个名额。
- **不合作后端，串行来源**：取消只结束 A 的请求等待。A 的真实加载仍占名额，退出并清理迟到产物后才执行 B。
- **不合作后端，并行/不同来源**：B 可以在 A 真正退出前成功，A 的迟到结果和错误不能影响 B。
- **排队 A**：A 尚未执行便取消，随后 B 能执行，A 不得进入后端。
- **共享 A**：取消一位等待者不能取消另一位；所有等待者退出后才取消实际轮次。
- **旧 A → 新 A + B**：旧 A 已取消但未退出时，同身份的新 A 等待旧轮结束并重新加载，不能领取旧产物；独立 B 仍能使用空闲名额。
- **文件缓存**：RemoteFileLoader 自己的等待可以先结束，外部 FileCache 填充继续退出清理。不同 URL 的 B 可开始；同 URL 的新填充必须等旧填充真正退出，旧临时文件不能被发布。
- **Manager 协程**：A 的取消回调与 B 的成功回调各一次，A 迟到后只释放，不能再触发成功或覆盖 B 的显示值。

## 两项故障与修复

2026-11-03，隔离 Unity 6000.6.7f1 工程实际运行 **76 项：74 通过、2 失败、0 跳过**。结果与日志位于仓库 `Temp/ResourceSystemPlayModeBoundaries3/results.xml` 和同目录 `tests.log`。原有 49 项仍通过；新增的 27 项中 25 项通过。A 取消后 B 的排队、串行、并行、跨来源、转换、实例原型、文件填充及 Manager 回调用例均通过。

当时只补测试，保留了以下两项正常失败用例；2026-11-04 已修复并完整回归通过，没有使用 `Ignore`、`Explicit` 或改变原断言。

### 1. 被拒绝的非空产物没有释放（已修复）

- 用例：`ResourceConversionBoundaryPlayModeTests.InvalidLoaderProductWithNullValueStillReturnsItsReleaseOwnership`。
- 时序：自定义 loader 返回非空 `IResource`，但 `Value == null` → 请求收到 `LoaderInvalidResult` → 关闭系统。
- 预期：拒绝交付后仍归还该产物携带的释放责任，`Release` 一次。
- 修复前：`Release` **0 次**；没有进入缓存或残留记录，关闭系统也未补上释放。`LoadManager.RunRoundAsync` 在结果检查失败时直接返回错误，漏掉了已经收到的产物。
- 修复：错误返回前复用 `DiscardAsync`，经执行上下文释放；释放失败继续保留原来的 `LoaderInvalidResult`，并把清理失败的产物保存在残留中。新增测试验证主线程释放、显式重试及成功后不再重复释放。

### 2. 迟到进度回放异常导致等待者无法退出（已修复）

- 用例：`ResourceInterleavingPlayModeTests.ThrowingLateProgressReplayDoesNotLeaveAGhostWaiterAfterBothRequestsCancel`。
- 时序：A 开始且保持挂起 → 发布进度 0.5 → 同键的迟到请求带入会抛异常的进度监听器 → 对两个请求分别取消。
- 预期：监听器异常不影响请求生命周期；两个请求均取消，最后一个等待者离开后底层轮次收到取消。
- 修复前：首请求 `Canceled`、迟到请求 `Faulted`，底层 `roundCanceled=False`。`SharedOperation.GetAsync` 增加等待者后直接回放，异常发生在负责 `Leave` 的 `try/finally` 之前。
- 修复：回放与广播共用 `ReportProgress` 隔离同步异常；加入后的回放、启动、等待统一由 `finally` 负责退出。新增测试验证诊断回调也抛错，以及回放内同步取消并重入加入请求时，最后一位退出仍准确取消执行。

精简复查：复用已有释放/残留流程，合并回放和广播的异常处理，缩小产物变量作用域；没有新增状态。保留 `finally` 和执行上下文释放，以保障等待者计数、线程与清理失败的重试责任。

## 在 Unity 中运行

1. 打开项目 `Unity`。
2. 打开 Test Runner，切换到 **PlayMode**。
3. 选择 `ResourceSystem.PlayModeTests` 程序集并运行。

也可以从仓库根目录执行下列 PowerShell 命令。运行前关闭占用该项目的 Unity 编辑器；结果和进程临时目录均放在工作区 `Temp`。

```powershell
$workspace = (Get-Location).Path
$testTemp = Join-Path $workspace 'Temp'
New-Item -ItemType Directory -Path $testTemp -Force | Out-Null
$env:TEMP = $testTemp
$env:TMP = $testTemp
$env:TMPDIR = $testTemp
& 'D:/Unity/6000.6.7f1/Editor/Unity.exe' `
    -batchmode -nographics `
    -projectPath (Join-Path $workspace 'Unity') `
    -runTests -testPlatform PlayMode `
    -assemblyNames ResourceSystem.PlayModeTests `
    -testResults (Join-Path $testTemp 'ResourceSystem.PlayMode.results.xml') `
    -logFile (Join-Path $testTemp 'ResourceSystem.PlayMode.log')
```

## 隔离与契约边界

- 每项测试创建独立的系统和对象，结束时归还租约、关闭系统，并等待一帧使 `Object.Destroy` 生效。异步等待有 10 秒上限，不在主线程阻塞任务。
- 文件测试只在 Unity 项目内的 `Temp/ResourceSystemPlayMode/<随机目录>` 写入并清理自己创建的文件。远端下载主要使用可控 Fetch；默认下载器用例临时监听 `127.0.0.1` 随机端口，验证真实 HTTP 与磁盘发布/复用，不访问外网。
- `Resources/ResourceSystemPlayMode/SampleText.txt` 是独立的只读测试资源。Manager 测试注入由测试配置构造的真实核心，随后调用公开接口，不读写业务的 `Resources/ResourceSystemConfig.asset`；固定配置路径的资产查找不属于这组 Manager 用例。
- AssetBundle 后注册使用测试 loader 验证接入契约；目前的占位 `AssetBundleLoader` 不具有可测试的真实包加载实现。HTTP 下载器验证本机 GET、重试与异步超时；真实 CDN 环境以及构建 Player 的平台兼容性不在本组 Editor PlayMode 验证范围内。
- 取消时可能已创建迟到实例；测试要求它被销毁、已交付租约被归还、后续预热项不再执行。Resources 卸载按原生已加载集合判断，避免 Editor 可恢复资产包装的 `== null` 行为干扰。
- 卸载失败按 `ResourceCache.UnloadAsync` 的公开契约保留残留，并在下次显式卸载重试；`IResource.Release` 的“至多调用一次”说明与这一重试契约不一致。这里只记录并验证现有公开流程，不修改系统代码。

复查后保留单一等待/清理基类。新增测试在失败时也放行门闩、等待请求结束并归还迟到成功的引用；文件缓存由创建者关闭。删除了交错测试桩里重复保存的退出信号，直接从退出序号判断。没有新增通用模拟框架；不同取消阶段和所有权场景仍保留独立断言，合并为一个“取消测试”会丢失可复现的阶段边界。
