# UnifiedWorkDemo_v3：组合，而不是融合

v3 不再写一个新的 WorkCenter 把排队和共享揉在一起，而是直接组合两个各自验证过的组件：

- **工作队列** `TicketScheduler`：M 个消费者、K 个执行名额（源码复制自 TicketQueueDemo）。
- **共享操作** `SharedOperation`：同 key 多等一、进度回放、无人等待策略、关闭（源码复制自 SharedOperationDemo）。

`Core/WorkCenter.cs` 78 行：同 key 的请求先在 SharedOperation 归并成一次执行，这一次执行再作为一张票据进入 TicketScheduler。

本目录自包含：`Core/` 下直接放着复制来的源码，只把命名空间改成了 `UnifiedWork.V3`，没有引用其他工程。整个目录可以单独拷走。Core 面向 **.NET Standard 2.1 / C# 7.3**，不使用 Channels 或第三方包。Tests/Demo 为 .NET 8。`Unity/` 下是接入示例源码，未编译、未在 Unity 中运行。

## 运行

```powershell
& .\Validate.ps1
# 或
dotnet run --project Tests/UnifiedWork.V3.Tests.csproj -c Release
dotnet run --project Demo/UnifiedWork.V3.Demo.csproj -c Release
```

本次 Release 验证：**11 / 11 测试（重复 5 次）、4 / 4 演示场景通过**，详见 [VALIDATION.txt](VALIDATION.txt)。

## 结构

| 文件 | 行数 |  职责 |
|---|---|---|---|
| `Core/WorkCenter.cs` | 78 | 组合：共享操作的执行器就是"提交一张票据并等它" |
| `Core/TicketQueue.cs` | 293 | 票据、结果窗口、M 消费者 K 名额的调度器 |
| `Core/SharedOperation.cs` | 270 | 同 key 归并、进度回放、无人等待策略、关闭 |
| `Core/OperationProgress.cs` | 25 | 进度值加说明 |
| `Core/OperationResult.cs` | 48 | 回调风格的成功/失败/取消三态 |
| `Unity/UnifiedWorkHost.cs` | 155 | 应用级 owner；工单簿是一个 `Dictionary<Guid, WorkTicket>` |
| `Unity/UnifiedWorkExample.cs` | 110 | 用户工单页面：提交、恢复、撤单、关页只停止观察 |

复制来的四个文件首行注明了来源和日期；除命名空间外与来源一致。来源 Demo 若有修改，重新复制并改一行命名空间即可。

## 最小接入

```csharp
var center = new WorkCenter<string, string>(
    async (key, progress, token) =>
    {
        progress.Report(new OperationProgress(0.5, "处理中"));
        await Task.Delay(100, token).ConfigureAwait(false);
        return key + " 完成";
    },
    workerCount: 4,
    maxConcurrency: 2,
    policy: NoWaitersPolicy.ContinueInBackground);

string result = await center.GetAsync("bug:login", progress, pageToken);   // 一次调用一张工单
IDisposable sub = center.Subscribe("bug:login", r => { }, p => { });        // 回调回到订阅线程
await center.CompleteAsync();                                                // 或 StopAsync()
```

## 设计思路

### 两个正交的问题

"同 key 多等一"回答的是：谁在等、进度给谁、失败给谁、最后一个人走了执行怎么办。"排队与限流"回答的是：什么时候执行、最多几个一起执行、关闭时排空还是取消。前者不关心执行发生在哪个线程、有几个名额；后者不关心有几个人在等同一件事。

v1/v2 把两件事放进同一个 WorkCenter，工单状态机因此既要表达"排队中 / 执行中 / 等待旧轮"，又要维护注册表，再加一层分发器把结果送回线程。三件事互相缠绕，改任何一处都要重新推一遍状态。v3 的判断是：两件事各自独立成立，就各自独立实现，然后用一行代码组合。

### 用现成的原语代替自造概念

| 需要的概念 | 用什么 | 为什么够用 |
|---|---|---|
| 工单 | `GetAsync` 返回的 `Task<TResult>` | 可多次 await；成功 / 失败 / 取消三态终结；异常对所有等待者共享 |
| 撤单 | 调用方自己的 `CancellationToken` | 只影响持有它的那次等待；不需要"撤单请求"对象 |
| 在途登记 | `Dictionary<TKey, Entry>` 加一把锁 | 一个 key 同一时刻最多一轮在途，字典就是全部状态 |
| 分发器 | 调用方的 `SynchronizationContext` | `await` 自己回去；`Progress<T>` 在创建线程捕获上下文 |
| 排队与限流 | `Queue<T>` 加两个 `SemaphoreSlim` | 一个计"有活"，一个计"名额" |

### 共享在外、队列在内

两个组件只有一个接缝：SharedOperation 的执行器委托 `(key, progress, token) => Task<TResult>`，与 TicketScheduler 的 `Submit(work, token)` 返回的票据 Task，类型刚好对得上。所以组合不需要适配类，WorkCenter 构造函数里的一个 lambda 就是全部胶水。

```
请求线程                        SharedOperation                         TicketScheduler（线程池消费者）
GetAsync(key, progress, token)
  ├─ 同 key 在途且可加入 ──► Waiters++，登记 progress，回放最新进度
  └─ 否则开新一轮 ────────► RunAsync ──► 执行器 = Submit(work, 本轮 token) ──► 入队 ──► 领取 ──► work(...)
                                            └─ await Results.WaitAsync(票据)  ◄─────────── 票据完成
  await 共享 Task（自己的 token 只结束自己的等待）
```

方向选"共享在外、队列在内"：归并发生在入队之前，同 key 的 N 个请求只产生一张票据，重复请求既不占队列位置也不占执行名额，队列看到的永远是去重后的工作。反过来放（队列在外、归并在内）的话每个请求都要先入队，归并要等到被领取时才发生，队列里会堆满注定被合并的票据，限流名额也会被重复请求挤占。

代价：`Submit` 不再由固定的业务线程调用，而是由 SharedOperation 在决定开新一轮时调用，可能在首个请求者的线程上，也可能在等旧轮退出后的线程池线程上。所以 WorkCenter 创建 TicketScheduler 时不注入业务线程校验。

### 两层取消

- **等待者的 token**：`GetAsync` 收到的那个。只结束这一次等待，不碰共享 Task，也不碰执行。
- **本轮的 token**：`Entry.Cancellation`。这才是传给执行器、进而传给 `Submit` 和业务 `work` 的 token。触发它的只有两件事：策略为 `CancelExecution` 时最后一个等待者离开，或者 `StopAsync`。

队列只认识第二层：排队中被取消的票据在被领取时跳过，执行中被取消的交给业务代码自己响应。这正是 TicketQueue 原本 `Submit(work, cancellationToken)` 的契约，所以两边都不用改。

### 先封存再发布

`Entry.Joinable` 是 SharedOperation 里唯一的"状态位"。一轮在三个时刻被封存（置为 false）：

1. **完成前一刻**（`RunAsync`）：先封存并从字典移除，再 `SetResult`。完成后到达的同 key 请求因此一定开新一轮、重新执行，不会拿到一份已经完成的旧结果和陈旧的进度回放。
2. **最后一个等待者撤单且策略为 `CancelExecution`**（`Leave`）：先封存，再 `Cancel`。取消期间到达的新请求看到的是"不可加入"，于是开新一轮。
3. **`StopAsync`**（`ShutdownAsync`）：先封存，再 `Cancel`。

新一轮开始前要等旧轮真正退出（`RunAsync` 开头的 `await Quiet(previous.Completion.Task)`）。两个保证由此而来：同 key 不重叠执行；新请求拿不到旧轮的迟到结果。不需要"不重叠"这条时删掉那一行即可。

### 没有分发器，怎么回到调用方线程

- **请求线程**：`GetAsync` 在第一个真正的 await 之前都是同步的，上锁登记、决定开不开新一轮、调用执行器、`Submit` 入队都在这里完成。没有旧轮要等时，`GetAsync` 返回前票据已经在队列里，队列的 FIFO 顺序就是请求顺序。
- **消费者线程**：`TicketScheduler` 用 `Task.Run` 起 M 个消费者循环，业务 `work` 和它的 `progress.Report` 都发生在线程池线程上。
- **回到调用方**：await 风格靠调用方自己的 `await`；SharedOperation 内部除 `ObserveAsync` 外全部 `ConfigureAwait(false)`，既不依赖也不污染上下文。`Progress<T>` 在创建它的线程捕获上下文，在主线程 new 出来的进度对象，回调就在主线程。`Subscribe` 风格在 `ObserveAsync` 里故意不写 `ConfigureAwait(false)`，结果回调因此回到订阅线程。
- **诊断回调**（无人承接的失败、监听器或回调抛出的异常）可能来自任意线程，Unity Host 示例显式 `Post` 回主线程再打日志。

### 关闭顺序

两个组件各有自己的关闭，WorkCenter 固定先关共享操作、再关队列。原因：共享操作的 `CompleteAsync` 会等到所有在途轮结束，其中包括正在等旧轮退出、还没来得及 `Submit` 的新一轮，这些轮都需要队列还开着。等共享那边彻底安静再关队列，就不会撞上"队列已关闭，不再接收新任务"。`StopAsync` 下，还没提交的新一轮会在 `Submit` 之前就因本轮 token 取消而退出，同样不会向关闭中的队列提交。

### 故意不做的

工单注册表、工单快照、状态机、分发器接口、终态 TTL 都不在 Core 里。"凭编号找回工单"只有使用方知道自己要不要，所以在 Unity Host 示例里用一个 `Dictionary<Guid, WorkTicket>` 完成，约 50 行。

## 一次请求的流转

以页面 A 先请求、页面 B 稍后请求同一个 key 为例：

1. A 调用 `GetAsync(key, progressA, tokenA)`。锁内：字典里没有这个 key，新建 `Entry`，`Waiters = 1`，登记 `progressA`。锁外：调用 `RunAsync`。
2. `RunAsync` 同步调用执行器，也就是 WorkCenter 的 lambda：`Submit` 把工作单入队并 `_ready.Release()`，`Results.WaitAsync` 返回票据的 Task。`RunAsync` 在这里 await，`GetAsync` 随后在 `WaitAsync(entry.Completion.Task, tokenA)` 处 await，控制权回到 A。此时票据已在队列里。
3. 某个消费者醒来：先拿到"有活"信号，再拿到一个执行名额，锁内出队，`WorkItem.ExecuteAsync` 检查本轮 token 未取消，调用业务 `work(key, Reporter, 本轮 token)`。
4. B 调用 `GetAsync(key, progressB, tokenB)`。锁内：找到在途且 `Joinable` 的 Entry，`Waiters = 2`，登记 `progressB`，读出 `Latest`。锁外：把 `Latest` 回放给 `progressB`，然后和 A 一样 await 同一个 `entry.Completion.Task`。
5. 业务代码 `progress.Report(p)`：`Reporter` 锁内更新 `Latest` 并复制监听器列表，锁外逐个 `Report`，`progressA`、`progressB` 各自 Post 回自己的上下文。
6. 业务代码返回：`WorkItem` 完成票据；`RunAsync` 的 await 恢复，锁内封存并从字典移除，锁外 `SetResult`。A、B 的 await 恢复，各自经 `Leave` 把 `Waiters` 减到 0，回到各自的线程。消费者释放名额，回到循环。

撤单的分支：

- **只有 A 撤单**：`WaitAsync` 对 A 抛出 `OperationCanceledException`，`Leave` 把 `Waiters` 减到 1，执行照旧，B 不受影响。
- **A、B 都撤单且策略为 `CancelExecution`**：最后一个 `Leave` 把 `Joinable` 置 false 并取消本轮 token。票据还在排队，消费者领取时检查 token 后跳过；已在执行，业务代码从 token 看到取消。两种情况都走 `TrySetCanceled`，本轮静默结束，没有人承接也不进诊断。
- **取消期间页面 C 到来**：锁内发现 Entry 不可加入，记下它作为 `previous`，新建 Entry 顶替到字典里。C 的 `RunAsync` 先 `await Quiet(previous.Completion.Task)`，等旧轮真正退出后才 `Submit` 新票据。

## 关键代码说明

### WorkCenter：一行组合

```csharp
_scheduler = new TicketScheduler(workerCount, maxConcurrency);
_shared = new SharedOperation<TKey, TResult>(
    (key, progress, token) => _scheduler.Results.WaitAsync(
        _scheduler.Submit(ct => work(key, progress, ct), token)),
    policy, comparer, diagnose);
```

这里出现的 `token` 是 SharedOperation 交来的本轮 token，`ct` 是队列在领取时原样交回的同一个 token，等待者的 token 不会出现在这一层。`Submit` 返回票据，`Results.WaitAsync` 校验票据归属后直接返回票据内部的 Task，这个 Task 就是 `RunAsync` 等待的执行结果。失败、取消都沿这个 Task 传回 SharedOperation，再由它分发给等待者，所以 TicketScheduler 不需要自己的诊断回调。

其余成员全是转发。`CompleteAsync` 和 `StopAsync` 只负责顺序：先 `_shared`，后 `_scheduler.CompleteAsync()`。

### GetAsync：加入还是开新一轮

```csharp
lock (_gate)
{
    if (_stopped) throw new InvalidOperationException("服务已停止。");
    if (!_inFlight.TryGetValue(key, out entry) || !entry.Joinable)
    {
        previous = entry;                       // 正在取消退出的旧轮；没有则为 null
        entry = new Entry();
        _inFlight[key] = entry;
        start = true;
    }
    entry.Waiters++;
    if (progress != null)
    {
        entry.Listeners.Add(progress);
        replay = entry.Latest;                  // 晚加入者回放最新进度
    }
}
if (replay != null) progress.Report(replay);
if (start) Forget(RunAsync(key, entry, previous));
try { return await WaitAsync(entry.Completion.Task, token).ConfigureAwait(false); }
finally { Leave(entry, progress); }
```

锁内只做登记和决策，所有可能跑到用户代码的事（进度回放、启动执行、等待）都放在锁外。`previous` 只在"字典里有但不可加入"时非空，也就是正在取消退出的旧轮。`Forget` 是空方法，用来写明"这个 Task 故意不等"，`RunAsync` 自己兜住全部异常。`try/finally` 保证不管是正常返回还是被撤单，都会经过 `Leave` 把人数减掉。

### WaitAsync：只取消自己的等待

```csharp
if (task.IsCompleted || !token.CanBeCanceled) return await task.ConfigureAwait(false);
var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
using (token.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), canceled))
{
    if (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false) != task)
        throw new OperationCanceledException(token);
}
return await task.ConfigureAwait(false);
```

共享 Task 从头到尾没有被碰：撤单只是让 `WhenAny` 先看到旁路的 `canceled`。`using` 在离开时注销回调，避免长寿命 token 积累注册。`RunContinuationsAsynchronously` 让取消回调不会在调用 `Cancel` 的线程上同步跑完后面的 await 续体。

### RunAsync：收尾的顺序

```csharp
if (previous != null) await Quiet(previous.Completion.Task).ConfigureAwait(false);
entry.Cancellation.Token.ThrowIfCancellationRequested();
value = await _execute(key, new Reporter(this, entry), entry.Cancellation.Token).ConfigureAwait(false);
// ...catch 住全部异常后：
lock (_gate)
{
    entry.Joinable = false;                     // 先封存再发布：之后的同 key 请求开新一轮
    Entry current;
    if (_inFlight.TryGetValue(key, out current) && current == entry) _inFlight.Remove(key);
    abandoned = entry.Waiters == 0;
}
if (error == null) entry.Completion.SetResult(value);
else if (error is OperationCanceledException canceled) entry.Completion.TrySetCanceled(canceled.CancellationToken);
else
{
    entry.Completion.SetException(error);
    if (abandoned) Diagnose(error);             // 无人承接的失败走诊断
}
```

开头三件事按固定顺序：等旧轮、检查本轮是否在等待期间已被取消、执行。`Quiet` 把旧轮的失败或取消吞掉，新一轮不继承旧轮的结果，只继承"它已经退出"这个事实。收尾时 `current == entry` 的判断是为交接场景准备的：字典里可能已经是顶替上来的新一轮，不能误删。`abandoned` 在锁内判定，和 `Leave` 的减数互斥，不会出现"有人在等却被记成无人承接"。

### Leave：最后一个人

```csharp
lock (_gate)
{
    if (progress != null) entry.Listeners.Remove(progress);
    entry.Waiters--;
    cancel = entry.Waiters == 0 && entry.Joinable && _policy == NoWaitersPolicy.CancelExecution;
    if (cancel) entry.Joinable = false;         // 作废本轮；后来者等它退出后新开一轮
}
if (cancel) entry.Cancellation.Cancel();
```

退出者先从监听器列表摘掉，之后的进度不再送给它。`Joinable` 条件排除了"已经完成或已经被取消的轮"，保证 `Cancel` 只被这条路径触发一次。`Cancel` 放在锁外，因为它会同步运行注册在 token 上的回调，其中可能有业务代码的续体。

### Reporter：进度扇出与回放

```csharp
lock (_owner._gate)
{
    _entry.Latest = value;
    listeners = _entry.Listeners.ToArray();
}
foreach (var listener in listeners)
{
    try { listener.Report(value); } catch (Exception e) { _owner.Diagnose(e); }
}
```

`Latest` 和监听器快照在同一把锁下更新，锁外逐个发送。监听器通常是 `Progress<T>`，`Report` 只是 Post 到各自的上下文，所以这里不会阻塞业务线程。某个监听器抛异常只进诊断，执行和其他监听器不受影响。回放逻辑在 `GetAsync` 里：晚加入者加入时读一次 `Latest`，先收到它，再收到后续的。

### Subscribe：GetAsync 之上的门面

```csharp
var cts = new CancellationTokenSource();
var progress = onProgress == null ? null : new Progress<OperationProgress>(onProgress);
Forget(ObserveAsync(key, progress, cts.Token, onResult));
return new Subscription(cts);
```

```csharp
try { result = OperationResult<TResult>.Success(await GetAsync(key, progress, token)); }
catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
catch (OperationCanceledException e) { result = OperationResult<TResult>.Canceled(e); }
catch (Exception e) { result = OperationResult<TResult>.Failure(e); }
if (token.IsCancellationRequested) return;      // 结果已到但页面刚退订：静默
try { onResult(result); } catch (Exception e) { Diagnose(e); }
```

订阅就是一次带 token 的 `GetAsync`，退订就是取消这个 token。`Progress<T>` 在订阅线程创建，进度回到订阅线程；`await GetAsync` 没有 `ConfigureAwait(false)`，结果回调也回到订阅线程。两处 `token.IsCancellationRequested` 检查分别覆盖"等待中退订"和"结果已到、回调前一瞬退订"，退订之后保证没有任何回调。回调抛异常进诊断，不会变成未观察的任务异常。

### ShutdownAsync：两种关闭

```csharp
lock (_gate)
{
    _stopped = true;
    entries = _inFlight.Values.ToArray();
    if (cancel) foreach (Entry entry in entries) entry.Joinable = false;
}
if (cancel) foreach (Entry entry in entries) entry.Cancellation.Cancel();
return Task.WhenAll(entries.Select(entry => Quiet(entry.Completion.Task)));
```

`CompleteAsync` 和 `StopAsync` 只差一个 `cancel` 参数。被交接走的旧轮已经不在字典里，但顶替它的新一轮在，而新一轮要等旧轮退出才会结束，所以 `WhenAll` 间接等到了旧轮。重复调用只是再做一次快照并等待，天然幂等。

### TicketScheduler：两个信号量

```csharp
while (true)
{
    await _ready.WaitAsync().ConfigureAwait(false);
    await _slots.WaitAsync().ConfigureAwait(false);
    try
    {
        IWorkItem item;
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                if (_closed) return;
                continue;
            }
            item = _queue.Dequeue();
        }
        await item.ExecuteAsync().ConfigureAwait(false);
    }
    finally
    {
        _slots.Release();
    }
}
```

`_ready` 计"有活可干"，`Submit` 每入队一条就 `Release` 一次；`_slots` 计执行名额 K。先等活再等名额，避免没活的消费者空占名额。每个消费者完整 await 自己领到的工作单，所以同时执行的数量不超过 min(M, K)。`Submit` 里入队和 `_ready.Release()` 与 `_closed` 判断共用一把锁，保证关闭后不会再向已释放的信号量 `Release`。`CompleteAsync` 把 `_closed` 置位后 `_ready.Release(M)`，每个消费者最终都会得到一次"空队列且已关闭"的唤醒并退出；`FinishAsync` 等全部消费者退出后才释放两个信号量。

### WorkItem：领取时才检查取消

```csharp
try
{
    _cancellationToken.ThrowIfCancellationRequested();   // 排队期间取消：领取时检查，跳过实际操作
    T result = await _work(_cancellationToken).ConfigureAwait(false);
    _completion.TrySetResult(result);
}
catch (OperationCanceledException ex) { _completion.TrySetCanceled(ex.CancellationToken); }
catch (Exception ex) { _completion.TrySetException(ex); }
```

这是"排队中撤单"在队列侧的全部实现：取消的票据不会被从队列中移除，而是在被领取时跳过。票据与工作单共用一个 `TaskCompletionSource`，不需要按编号查表。业务失败只影响这张票据，消费者循环不退出。

### Unity Host：工单簿

```csharp
private readonly Dictionary<Guid, WorkTicket> _tickets = new Dictionary<Guid, WorkTicket>();

public WorkTicket Submit(string key)
{
    var ticket = new WorkTicket(key, _center);
    _tickets[ticket.Id] = ticket;
    return ticket;
}
```

```csharp
// WorkTicket 构造函数
Result = center.GetAsync(key, new Progress<OperationProgress>(OnProgress), _withdraw.Token);
```

一张工单就是一个共享结果 Task、一个撤单用的 `CancellationTokenSource`、一份最新进度。`Progress<T>` 在主线程创建，进度回调回到主线程。页面只拿 `WorkTicket`，关闭权限留在 Host；页面关闭只停止观察，显式调用 `Withdraw` 才撤单。这段代码就是 v1/v2 里 Core 内置的工单注册表，在 v3 由使用方按需维护。

## 验证对照

| 设计点 | 测试（`Tests/Program.cs`） | 演示（`Demo/Program.cs`） |
|---|---|---|
| 归并在入队之前，队列里只有一张票 | 同 key 两个工单经过队列只执行一次 | 场景 1 |
| K 和 M 各自生效 | 执行名额 K 生效；消费者数量 M 生效 | 场景 2 |
| 排队中撤单：等待者立即取消，票据领取时跳过 | 排队中撤单（CancelExecution） | 场景 3 |
| 执行中撤单传到业务代码 | 执行中撤单（CancelExecution） | 场景 3 |
| 交接：新一轮等旧轮退出，不拿旧结果 | SharedOperationDemo 的 CancelPolicyAndHandover | 场景 3 |
| ContinueInBackground：晚到者加入同一轮 | ContinueInBackground | 无 |
| 进度穿过队列并回放 | 进度穿过队列 | 场景 1 |
| 失败共享同一个异常实例 | 失败共享 | 无 |
| CompleteAsync 排空后拒绝；StopAsync 取消排队与执行中 | CompleteAsync；StopAsync | 场景 4 |
| 回调回到订阅线程，业务在消费者线程 | Subscribe | 无 |


## 取舍

- **排队中撤单**：最后一个等待者撤单时本轮 token 被取消，但票据仍留在队列里，直到某个消费者领到它、检查 token 后跳过。它不占执行名额，只占一个队列位置；等待者本身立即看到取消（Tests 第 4 条、Demo 场景 3）。
- **新一轮要等旧轮真正退出**：撤单后再来的同 key 请求，会等上一次执行退出后再入队；旧轮清理拖多久，新一轮就晚多久。
- **进度回放与并发进度的顺序**：晚加入者的回放在锁外发送。极小的窗口内，如果业务代码恰好同时报告了更新的进度，监听器可能先收到新值、再收到回放的旧值。进度是快照不是增量，UI 若要求单调可自行按 `Fraction` 取大。
- **工单簿是使用方的事**：需要"凭编号找回工单"时，像 `Unity/UnifiedWorkHost.cs` 那样用一个 `Dictionary<Guid, WorkTicket>`；Core 不替你保存终态。
- **执行器必须响应 token**：否则 `StopAsync` 会一直等它退出。
- **复制而非引用**：源码是 2026-10-22 的快照，与来源 Demo 之间不会自动同步。
- 不提供持久化、容量限制、自动重试、终态 TTL。
