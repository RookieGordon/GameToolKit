# KeyedAsyncLock 协程锁原理与资源加载用法

`KeyedAsyncLock<TKey>` 按键协调异步操作。同键请求按入队顺序取得执行权，不同键独立执行。等待通过任务完成通知推进，不占用线程等待。原字符串 `KeyedAsyncLock` 保留为兼容入口。

## 主流程

1. `LockAsync(key, token)` 查找该键的记录。没有持有者时立即返回一个释放句柄。
2. 已有持有者时，将本次等待加入队列，返回尚未完成的任务。
3. 持有者 `Dispose()`，通知队首请求取得新的句柄。队列为空时移除该键的记录。
4. 等待者取消，只退出自己的队列位置；已经取得的句柄仍由调用者负责释放。

```csharp
using (await locks.LockAsync(key, cancellationToken))
{
    // 取得执行权后再次查询，前一个操作可能已产生可复用结果。
    // 这里可以 await；真正的工作和清理都完成后，using 才释放执行权。
}
```

内部只用短同步锁保护字典和等待队列，保护范围内不执行异步业务。每个等待者通过 `TaskCompletionSource<Releaser>` 得到通知，续体异步调度。取消时移除自身节点，正常释放时放行一个节点，批次失败时通知当前队列全部节点。

释放句柄绑定取得时的记录，并保证重复 `Dispose` 无效。复制原字符串接口的结构体句柄，也不会释放后来一轮操作的执行权。

## 两种失败处理

正常释放只放行下一个请求。需要结束当前排队批次时，持有者调用 `FailWaitingRequests(error)`，队列中的请求收到同一个异常。

```csharp
using (var held = await locks.LockAsync(key, cancellationToken))
{
    try
    {
        await ExecuteAndCleanUpAsync();
    }
    catch (OperationCanceledException)
    {
        throw; // 个人取消不推断其他请求也会失败。
    }
    catch (Exception error)
    {
        if (failureAppliesToWaitingRequests)
            held.FailWaitingRequests(error);
        throw;
    }
}
```

上述方法名代表业务提供的操作，操作抛出前必须完成自己的回退。`FailWaitingRequests` 只通知调用时已排队的请求，不提前释放当前执行权，也不永久保存失败。之后的新请求仍须等待持有者清理并释放，才可开始下一轮。

锁不保存业务结果。资源、下载文件或其他结果由业务容器保存，后续操作取得锁后复查容器即可复用。锁也不会因等待超时而强制放行仍在执行的持有者；取消等待和实际操作结束是两个时刻。

## 资源加载流程

`LoadManager` 使用 `KeyedAsyncLock<ResourceKey>`，键由加载器标识和加载器解析出的资源键组成。不同加载器、不同资源版本不会仅因地址相同而混用。

```text
解析身份
→ 在执行上下文内复查资源并登记请求，同时取得锁的等待任务
→ 在上下文外异步等待资源锁
→ 复查该轮结果；需要加载时启动真实操作
→ 领取独立 ResourceRef
→ 注销待领取请求
```

正在执行的加载持有资源锁，直到加载及必要清理结束。发起请求取消等待后，只要其他请求仍需要该资源，加载继续；全部请求退出才取消操作。不能因第一个调用者取消而提前开锁。

每个请求最终领取自己的 `ResourceRef`。待领取请求会暂时保护成功结果，防止首位领取者立即归还时，其他请求尚未领取的资源被卸载。最后引用归还或最后等待者退出，统一根据内存策略缓存或卸载；从未交付的结果直接释放。

本地与远端的失败行为通过注册策略表达，核心不判断 URL 或协议：

```csharp
manager.RegisterLoader("local", localLoader); // 默认 FailWaitingRequests
manager.RegisterLoader("remote", remoteLoader, new LoaderPolicy
{
    FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests
});
```

| 策略 | 当前尝试失败后的行为 |
| --- | --- |
| `FailWaitingRequests` | 当前排队批次共同失败，后续新请求可重新尝试 |
| `ContinueWaitingRequests` | 执行本次加载的请求失败；其余请求重新解析、复查容器并继续自己的尝试 |

内置 `UnityResourceHost.RegisterRemoteLoader` 已选择第二种策略。手动注册远端加载器时显式设置策略；配置在注册时复制，之后修改原配置不影响运行中的加载器。

如果加载器未确认清理完成，保留故障记录并阻止同键再次加载，避免新旧操作重叠。这条资源所有权约定适用于两种策略。类型不匹配和调用者取消仅结束对应请求。

## Unity 执行上下文

`IExecutionContext.RunAsync(Action/Func<T>)` 用于短状态提交或主线程操作。后台调用返回任务，由主线程执行后完成；等待者用 `await` 等待，不通过事件阻塞线程。`InvokeAsync(Func<Task<T>>)` 用于在上下文内启动异步操作，并等待其真实完成。

同步 `Invoke`、资源值访问、注册、维护和快照在 Unity 下要求主线程调用，错误线程直接报错。`Post` 用于通知、归还引用及启动关闭；需要确认关闭完成时等待 `ShutdownAsync`。调度到主线程执行一个动作，不代表调用者后续代码也自动留在主线程。

自定义 `IExecutionContext` 实现需要补充两个 `RunAsync` 方法。资源锁负责同键操作顺序，执行上下文负责状态和 Unity API 的执行位置。

## 与信号量的分工

`SemaphoreSlim.WaitAsync` 仍适合限制一个加载器同时进行的真实加载数量；本实现的按键队列则负责同资源的取得执行权和批次失败通知。两者都异步等待。`MaxConcurrentLoads = 0` 表示不限制加载器并发，负数配置会被拒绝。
