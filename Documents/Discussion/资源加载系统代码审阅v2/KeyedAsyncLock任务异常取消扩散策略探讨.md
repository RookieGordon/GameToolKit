# KeyedAsyncLock 任务异常／取消扩散策略 扩展设计

版本：v1（2026-10-15）
参与：用户（提出扩展方向）、大肥鱼（设计与影响面核对）
状态：**设计提案，待用户确认后实现；尚未改动代码**

**状态图例**：✅ 已确认 ｜ 🔶 我的提议（待你点头或修改）｜ ⏳ 未定 ｜ ⚠️ 现状与文档不一致，需澄清

---

## 1. 结论先行

你的方向成立，而且**机制已经有了一半**：`KeyedAsyncLock` 现在就有 `FailWaitingRequests(error)`，语义正是"当前排队批次全部失败"。缺的部分是：

| 维度 | 现状 | 缺口 |
| --- | --- | --- |
| 批次**失败** | ✅ `FailWaitingRequests(error)` 已实现（含测试） | — |
| 批次**取消** | ❌ 无 | 只能由持有者构造 `OperationCanceledException` 当作"失败"传进去，语义不对 |
| 只作用于**单个任务** | ✅ 本来就是默认（不调用批次方法即可） | 需要明确成"策略"而不是"沉默的默认" |

所以真正要加的是**批次取消**，以及把"扩散范围"从命名里显式表达出来——现在方法名里的 "WaitingRequests" 已经在说批次，但没有一个可选项让调用方表达"我只要影响自己"。

---

## 2. 影响面（先说清，再谈设计）

我按符号检索了全仓：

- **生产代码消费者：无。** `Unity/Assets/ToolKit/Tools/` 下除 `KeyedAsyncLock.cs` 自身与一份设计文档外，**没有任何调用点**；资源加载系统当前用的是 `ResourcePipeline` 里的记录 + 原子认领，没有引用这个锁。
- **测试消费者**：`ToolKit/Tests/AsyncLockSystem/KeyedAsyncLockTests.cs`，8 个用例，其中 2 个直接调用 `FailWaitingRequests`。
- **文档引用**：`Unity/Assets/ToolKit/Tools/Common/AsyncLockSystem/信号量与KeyedAsyncLock原理.md`。

**含义**：改公开 API 的兼容成本极低（只有仓内测试 + 文档），可以放心把命名理顺，不必为"没人用的兼容层"背包袱。

---

## 3. ⚠️ 一处必须先澄清的文档漂移

那份设计文档描述的**不是当前资源加载实现**：

| 文档写的 | 当前实现 |
| --- | --- |
| `LoadManager` 使用 `KeyedAsyncLock<ResourceKey>` | 没有 `LoadManager`；当前是 `ResourceManager` + `ResourcePipeline`，未使用该锁 |
| `Local` 默认 `FailWaitingRequests`，`Remote` 显式 `ContinueWaitingRequests` | 第 13 轮决定**所有来源统一接续，并删除 `FailWaitingRequests`**（与文档相反） |
| 内置 `UnityResourceHost.RegisterRemoteLoader` 已选第二种策略 | 代码中不存在 `UnityResourceHost` |
| "加载器未确认清理完成 → 保留故障记录并阻止同键再次加载" | 当前设计是**残留移交后同键可重新加载**（有测试 `释放失败的残留随结果返回且不封锁同键`） |

**这不是要你改文档**，而是提醒：你提的"按来源分策略"和这份文档的旧默认是一致的，也就是说这可能**不是新增需求，而是原本就有、后来在第 13 轮被合并掉的**。第 13 轮的结论要有依据地推翻，这一点我按你的确认来处理（见第 7 节）。

---

## 4. 扩散范围：两个正交维度

"异常/取消"是**结果类型**，"整批/单个"是**影响范围**。现在只有一个方法把两者绑死了，拆开就是：

| | 只作用于自己 | 作用于当前等待批次 |
| --- | --- | --- |
| **失败** | 不调用任何方法（沉默默认） | `FailWaitingRequests(error)` ✅ 已实现 |
| **取消** | 不调用任何方法 | **缺失** |

- **"只作用于单个任务"不是一个 API**——它就是"什么都不做"。持有者的失败/取消由它自己 `throw`；等待者各自的取消由 `LockAsync` 的令牌负责（已实现）。**这一格不需要新代码，需要的是把它写成策略、不再靠默认沉默。**
- 真正的缺口只有一格：**批次取消**。

---

## 5. 提议的 API 🔶

### 5.1 方案（推荐）

```csharp
/// <summary> 本次失败/取消对同键等待批次的影响范围 </summary>
public enum WaitingRequestSpread
{
    /// <summary> 只作用于本次调用：等待者不受影响，各自继续等待或自行重试 </summary>
    None = 0,

    /// <summary> 作用于调用时已排队的等待批次；之后到达的请求不受影响 </summary>
    WaitingBatch = 1,
}
```

```csharp
// Releaser 上：
/// <summary> 以同一个异常结束当前等待批次；等待者的异常实例与传入实例相同 </summary>
public void FailWaitingRequests(Exception error, WaitingRequestSpread spread = WaitingRequestSpread.WaitingBatch);

/// <summary> 以取消结束当前等待批次；每个等待者收到携带自己令牌的 OperationCanceledException </summary>
public void CancelWaitingRequests(CancellationToken cancellationToken,
    WaitingRequestSpread spread = WaitingRequestSpread.WaitingBatch);
```

### 5.2 为什么默认值保留 `WaitingBatch`

现有签名 `FailWaitingRequests(error)` 的语义就是批次；加一个默认参数后**调用点不变、行为不变**，测试也不用改。

### 5.3 批次取消的语义细节 🔶

| 问题 | 提议 |
| --- | --- |
| 用哪个令牌？ | **每个等待者自己的令牌**，不是持有者的。等待者收到的 `OperationCanceledException.CancellationToken` 应该是它自己传给 `LockAsync` 的那个（若它传的是 `CancellationToken.None`，则用一个已取消的令牌）。理由：业务判断"是不是我自己取消的"要看自己的令牌。 |
| 未传令牌时？ | 用一个 `new CancellationToken(canceled: true)`，异常语义仍成立。 |
| 作用范围 | 与 `FailWaitingRequests` 一致：**调用时已排队的那一批**。之后到达的请求仍可正常排队、等持有者 `Dispose` 后开始新一轮。 |
| 是否释放执行权？ | **不释放**。与 `FailWaitingRequests` 一致——持有者必须自己完成清理后再 `Dispose`（这条已有测试守着）。 |
| 重复调用 | 幂等：已放出结果的节点不重复通知（沿用现有 `TrySetException` / `TrySetCanceled` 的写法）。 |

---

## 6. 与资源加载策略的对接 🔶

扩展点有了之后，按来源的策略就能自然表达（这也是你上一轮的意见）：

| 来源 | 策略 | 锁上的动作 |
| --- | --- | --- |
| AB / Resources / Local | 整批失败 | 持有者失败时调用 `FailWaitingRequests(error)` |
| Remote | 只本次失败 | 持有者失败时**什么都不调**，其余请求自行接续 |

**能表达，但不等于该用。** 我需要如实指出两条边界（这与"是否用这个锁"是独立问题，见第 8 节）：

1. 资源加载的等待者不只"等锁"，它们还要**拿到独立引用**（`ResourceRef`）。锁只协调执行权，不承载产物所有权——所以即使走锁，产物交付那块仍要自己写。
2. 当前资源实现的"接续"是**记录级**的（承担者失败、其余请求在同一记录上重新解析），与锁的"放行下一个"不是同一形状。

---

## 7. 需要你确认的判定依据

| 序号 | 事项 | 我的倾向 |
| --- | --- | --- |
| 1 | 批次**取消**是否也按"当前批次"而非"此后所有请求" | 是，与失败保持一致 |
| 2 | 批次取消时每个等待者收到**自己的令牌** | 是 |
| 3 | 保留 `FailWaitingRequests` 并加可选参数（不改调用点），而不是改名/新方法 | 是，兼容成本最低 |
| 4 | `WaitingRequestSpread.None` 是否需要真的传进方法 | **倾向不要**：None 就是"不调用"。留着会让 API 出现一个永远不该传的值，违反"不增加无依据的状态" |
| 5 | 第 3 节那处按来源策略的旧默认，是否作为"原本就有、被误删"处理 | 需要你判断，它决定这次是"恢复"还是"新增" |

第 4 条我特意标出来：如果采纳，那么 `WaitingRequestSpread` 里其实只有 `WaitingBatch` 一个有效值——**那就不该有枚举**，直接两个方法即可。请你在两者间选一个：

- **(A) 不带枚举**：`FailWaitingRequests(error)` + `CancelWaitingRequests(token)`，范围固定为当前批次。"只影响自己"用"不调用"表达。← 我倾向这个
- **(B) 带枚举**：为将来可能的第三档范围留位置。但按项目"每项新增抽象必须有具体需求"的约定，现在没有第三档需求。

---

## 8. 这次不讨论的

- **是否用 `KeyedAsyncLock` 替换资源加载的合并逻辑**：这是独立议题（涉及产物交付、独立引用、最后一人退出等），不并入本次扩展。
- **产物处置与续传策略**：见《资源加载系统产物处理策略探讨.md》。
- 本次只做 `KeyedAsyncLock` 自身的扩展 + 对应测试 + 更新那份设计文档中与之相关的段落。

---

## 变更记录

| 版本 | 时间 | 内容 |
| --- | --- | --- |
| v1 | 2026-10-15 | 首次建立：影响面核对（无生产消费者）、文档漂移（`LoadManager` 与按来源旧默认）、两维矩阵、批次取消缺口与 API 提议、资源层对接边界、待确认 5 项 |
