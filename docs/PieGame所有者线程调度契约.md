# PieGame：所有者线程同步块调度

## 范围

`PieGame.AI.Unity` 提供 `OwnerThreadDispatchQueue` 和 `UnityOwnerThreadDispatcher`。这是独立、显式的调度基础件，不自动发现或安装到宿主。055 交付时未接生产；057 由龙眠镇 Game 显式安装并注入 Session，见[生产接线契约](PieGame生产Session所有者调度.md)。这不新增第三条 Kernel 权威提交入口，也不改变既有 `UnityKernelBridge`、Kernel、场景移动或日间事务。

```csharp
// 在 Unity 主线程取得纯托管接口，worker 只保留此接口。
IOwnerThreadDispatcher dispatcher = host.Dispatcher;
// preparation 已由显式 provider 产生；原 token 由调用者继续传给真实桥。
Receipt receipt = await dispatcher.InvokeAsync(() => bridge.Commit(preparation, originalToken));
```

保证的是传入的**同步块**在所有者线程执行；不是让 `await` 后的调用方永久切换线程。不要在块中返回一个异步任务后声称整个异步过程受控；不要在主线程 `.Wait()` 依赖本队列派发的任务。

## 最小契约

- `InvokeAsync<T>(Func<T>)` 所有调用均 FIFO 入队，即使调用者就在 owner 线程，也不 inline 执行。
- 托管队列构造时明确 owner 线程 ID。`InvokeAsync`、`PendingCount` 可跨线程；`Drain`、`Close` 仅 owner 调用。错误线程在取出工作前拒绝。
- 队列锁仅保护登记、领取、关闭；执行同步块时不持队列锁。`Drain` 禁止重入，默认最多执行 16 项；单项异常只使其 Task fault，不阻断后续项。
- TCS 使用 `RunContinuationsAsynchronously`，不保证 continuation 的 Unity 线程归属。
- `Close` 幂等，原子拒新并清空待执行项，随后取消这些 Task；不会覆盖当前正在执行块的成功回执。块内可关闭队列，剩余项取消，当前结果保留。
- 关闭后 `InvokeAsync` 同步抛 `ObjectDisposedException`；无效 owner / limit、null 委托明确拒绝。`PendingCount` 不含当前执行项。
- Unity 宿主在 Awake 创建队列，Update 调 `Drain(16)`，OnDestroy 调 `Close`。禁用只暂停派发，销毁才取消排队工作；后台不持有 MonoBehaviour。
- 不接管用户取消语义。调用方仍在同步块中将原 token 交给桥 Commit / Confirm / Transact；已提交再取消仍是 committed，不回滚。销毁取消派发 Task 不制造内核回执。

## 生产接线及尚未迁移的边界

`DragonsleepSession` 保留 Semaphore gate 跨提供方 await；057 对注入 dispatcher 的 Unity 宿主，将请求冻结、回复处理、世界提交、trace、待确认图样及完成事件放进 owner 同步块，夜间原同步推进块也同样派发。提供方等待在块外，不能只把最后的 Commit 放入调度器。默认 null dispatcher 的 .NET / CLI 路径仍保留原线程行为。

055 的真实 Session 线程刻画测试仍锁定默认 null 路径：等待 provider 时 EnterPlace 被 gate 拒绝；异步完成事件不自动回到专用发起线程。057 另以 8 项 .NET 手动泵送和 3 项真实场景 TCS 测试验证注入路径，不把默认路径测试改写为已修复。Game / HUD 的 Busy 防读不能因此撤销。

`DsPlanRun` 自己的异步续体尚未整体迁移；同步 ConfirmCraft 仍要求 Game 在主线程调用。造物的候选身份、原始取消令牌与待槽覆盖策略仍待单独治理，不能因图样准备已在 owner 就宣称确认授权完善。调度关闭不得回退 worker 写入；关闭后最终通知可能无法派发，但原 gate 必须释放。

## 验证范围

独立 8 项 PlayMode 使用真实 UnityKernelBridge、字符串端口和 TCS / PendingCount 屏障，检查 worker 入队、同步 / 异步 provider、端口与 Completed 线程、显式唯一写、stale / duplicate、提交前后取消、销毁、错误线程、重入、异常隔离及限额。另有 2 项 .NET Session 特征测试。

上述独立基础件测试不加载完整游戏提交链；生产接线由 057 的独立 Session / 场景测试及 Player 回调审计承担。不提供自动提交、Unity 严格回放、线上 provider、网络重试或无限常驻队列。宿主仍应控制工作量，快速返回同步块，并处理关闭和单项错误。
