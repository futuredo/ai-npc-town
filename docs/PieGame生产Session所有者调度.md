# PieGame：生产 Session 所有者调度

## 本轮真正接入的范围

`DragonsleepGame.Awake` 显式添加现有 `UnityOwnerThreadDispatcher`，只取一次纯托管接口，通过 `DsOwnerThreadDispatcher` 注入 Core 的 `IDsOwnerThreadDispatcher`。Core 不引用 Unity 或 PieGame 程序集；SDK 内核、既有桥及调度器源码不变。

`DragonsleepSession` 构造器末尾增加可选 `ownerDispatcher = null`。CLI / .NET 默认 null 直接执行原同步代码；Unity 主场景明确注入，不靠自动捕获 SynchronizationContext 猜所有者。

```text
先取得原 Session gate
→ owner 同步冻结：世界 / 请求摘要、ID、trace、感知、提示、前半阶段
→ 块外调用并等待 provider，只使用已冻结 packet
→ owner 同步终结：取消 / 过期、解析、候选、待确认图样或提交、阶段、通知
→ 释放原 Session gate
```

普通 provider 异常仍传播，但在可用 owner 上完成一次 trace 通知；前置拒绝、取消、空回复和解析失败也不遗漏收尾。图样准备与确认 / 放弃是两个原有通知时点，不能把它们错误合并成一次。

夜间只将原来的同步推进、spent 清零、换岗、居民顺序冻结、每次 await 后检查与黎明推进派发到 owner；居民逐个请求、GM 顺序、规则 ID 和原执行器不改。没有把夜间替换成日间 SDK 候选事务。

## 不变与未迁移边界

- 串行 Semaphore gate 仍跨 provider await 持有；等待期间的移动拒绝语义不变，不把它当成 Kernel 短提交锁。
- 所有者队列关闭后，任务取消或以明确关闭异常结束；原 gate 必须释放，绝不 fallback 到 worker 写世界或补发通知。关闭后不保证完成通知还能发送。
- 同步 `ConfirmCraft`、`EnterPlace`、Restart 等仍由调用方负责在线程上正确调用；本轮 Game 的确认 / 放弃在 Unity 主线程。没有新增“所有同步 API 都支持 worker”的承诺。
- `DsPlanRun` 的 Adopt、结果列表更新、部分同步移动和世界读取仍有未迁续体；Game 外层 await 回主线程不代表 Plan 内部全程 owner。Busy 防护继续保留。
- pendingCraft 的原取消、候选授权和覆盖语义没有借本次接线悄悄改造，须另立任务。
- Kernel 权威 compare/write 仍只有场景移动、日间世界＋spent 两个限域入口。Session 线程所有权接线不是第三条 Kernel 提交入口，也不是全游戏统一 runtime。
- 不新增联网、自动重试、确认授权、自动提交或 Unity 严格回放。

## 可证范围

8 项 .NET 生产 Session 测试用手动 owner 队列和 TCS，分别证明冻结前、回包后未派发时无越线程操作、gate 竞争后才冻结、协作取消、普通异常、关闭无补写、造物两次通知与夜间顺序。默认 null 入口与新入口按完整快照、回执和事件序列逐字对照。

3 项真实主场景 PlayMode 使用真正延迟的离线提供方，由 worker 释放回复：行动全阶段、图样准备 / 放弃 / 确认在主线程；销毁已排队后半段不执行、不补通知、gate 释放。不是只用同步 RuleProvider 证明异步线程正确。

实际 Player 短局另统计 Session 的 StageChanged / TraceCompleted 回调线程；**只覆盖这两种 Session 通知，不覆盖 Plan 自己的所有读写或所有同步规则事件**。普通运行不创建验证器；离线快速按钮运行不代表真实网络、OS 像素命中、真人体验或性能验收。最终执行记录见 [057 日志](开发日志/2026-11-10_057_PieGame生产Session所有者调度.md)。
