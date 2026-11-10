# PieGame AI：Unity 主线程桥契约

## 范围

`PieGame.AI.Unity` 仅依赖 `PieGame.AI.Kernel` 和 Unity 引擎，`autoReferenced: false`。宿主显式添加 `UnityKernelBridge`、初始化端口并注册 handler；桥本身没有自动发现宿主或联网行为。051 在龙眠镇 Game 显式安装零耗时[场景移动适配器](PieGame首条场景事务接入.md)，053 同一宿主适配器增加[日间世界＋spent 原子入口](PieGame日间原子事务接入.md)；两个接入均不修改通用桥或纯内核，不是桥自行接管玩法。

本轮只做主线程事务入口、后台准备和可关闭的完成队列。**不提供自动提交、通用调度、网络提供方、Unity 严格回放或预算策略。** 内核自身的严格回放测试仍独立保留，不能把普通准备冒充严格回放。

## 最小 API

```csharp
bridge.Initialize(port, "host-rules-1");
bridge.Register("proposal", detachedHandler);
Request request = bridge.Begin("request-1", "proposal", frozenPayload);
Preparation candidate = await bridge.PrepareAsync(request, explicitProvider, callerCancellation);
// 调用方须保留 Unity 同步上下文；没有上下文的 worker 必须自行回主线程。
Receipt receipt = bridge.Commit(candidate, callerCancellation);
if (receipt.Code == "confirmation_required")
{
    // 先展示候选并取得玩家同意；签发 token 本身不代表玩家已经同意。
    string token = bridge.IssueConfirmation(candidate);
    receipt = bridge.Confirm(candidate, token, callerCancellation);
}
```

另有 `Capture()`、`CaptureState()`、`Digest`、`Transact(id, stamp, after, cancellation)`、`Reset(state)` 和 `Completed` 事件。没有公开 Kernel 属性、`RunAndCommit`、`confirmed=true` 或任意主线程委托执行器。

## 主线程和事务所有权

- `Awake` 记录实际 Unity 线程 ID，不以首次 API 调用者推断。所有公开桥入口（包括准备启动、事件订阅）必须在该线程调用；错误线程先抛 `bridge_wrong_thread`，不触碰端口或 Unity 对象。
- Capture、Begin、Commit、Confirm、IssueConfirmation、Transact、Reset 的端口访问都在主线程。端口回调不得重入桥。
- 原子性继续由内核同一提交锁保证；所有外部事件也须经同一桥的 Transact。宿主绕写端口或多个内核共享端口，不在保障范围内。
- `IStatePort.Write` 必须完整替换成功，或抛异常且无副作用。桥不回滚违约端口的“先写一半再抛错”。

## 准备与派发

`PrepareAsync` 用 `Task.Run` 调用内核普通准备，因此同步完成的 provider 和 handler 也在 worker。它们只能使用冻结载荷和隔离字符串，不得访问 Unity 对象、live port 或桥的同步入口。

worker 只持有纯托管 mailbox、内核及本次参数，不持有 MonoBehaviour；完成后只入队。`Update` 每帧最多派发 16 项，只设置 `TaskCompletionSource<Preparation>`，**不提交、不签发确认、不发布 Completed**。TCS 使用 `RunContinuationsAsynchronously`，但这不保证没有 Unity 上下文的消费者 continuation 在主线程。

禁用组件会暂停派发；它不是销毁，也不会自动取消。宿主不能在主线程同步 `.Wait()` 一个依赖 Update 完成的准备任务。

## 取消与销毁

| 边界 | 语义 |
| --- | --- |
| 准备前用户已取消 | 内核生成 `cancelled`；没有写入 |
| provider 忽略用户取消后迟到 | 内核生成 `late_after_cancel`；不解析候选、不写入 |
| 准备完成后才取消 | Preparation 保留原始用户 token，显式 Commit 在锁内拒绝 |
| 已越过取消裁决并成功写入 | 保持 `committed`，原 Receipt 可观察 `CancellationAfterCommit`，不回滚 |
| 等待中或已排队时销毁桥 | OnDestroy 原子关闭队列，并立即取消全部未完成公开任务；不等 provider 配合 |
| 销毁后 provider 才返回 | 关闭的 mailbox 丢弃结果；无写入、无事件、不能重开 Unity 对象 |

内核收到原始用户 token。只有私有 provider 包装器在调用期间组合“用户 + 生命周期” token，结束时释放组合源，不断开 Preparation／Receipt 对用户后续取消的观察。销毁取消公开任务不是一个虚构的内核取消回执；已经完成的任务与已提交事实不会被重标。

Reset 仍由真实内核执行，同态重置也递增代际。旧候选／状态戳之后经显式提交得到 `stale_session`；桥不把 Reset 包装成隐式 provider 重试或自动提交。

## 确认、事件与隐私

IssueConfirmation、Confirm 直接使用真实内核和同一个 Preparation。错误 token、另一候选、世界变化、Reset 和重复使用仍由内核判定；原子写入无副作用失败后可按内核语义重试。

Completed 仅由显式 Commit／Confirm／Transact 产生，包含内核安全回执且在主线程、内核锁外发布。逐个隔离订阅者异常，不让“写入成功”因为观察者抛错而看起来失败。观察者应快速返回；桥不记录异常原文或 payload。

冻结载荷的裁剪、完整提示与 Schema 打包、私有录制保存都由宿主负责。桥不会自动识别秘密，也不提供线上提供方、费用账本或玩家授权 UI。

## 验证与尚未完成

独立测试程序集 `PieGame.AI.Unity.Tests.PlayMode` 仅引用桥、内核和 Unity runners；字符串端口记录真实线程 ID，可控 TCS 强制建立等待、入队、取消与销毁顺序。最终执行成绩以开发日志为准，不把静态接口检查当编译或运行通过。

049 当时没有 Town 接入；051 接玩家进入地点，053 增加清晨→正午 / 正午→黄昏，黄昏、夜间、模型和计划仍未迁移。仍不能声称：全游戏已经共享该提交器、两个完整真实宿主都已迁移、Unity 严格回放完成、WebGL 无线程支持、后台常驻派发、持久化录制协议或成熟发布 SDK。宿主仍应控制会话长度并适时 Reset；内核去重记录不是无限后台服务。
