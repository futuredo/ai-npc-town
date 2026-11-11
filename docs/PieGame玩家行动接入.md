# PieGame 玩家自由行动接入

本轮基线 `3dde617`，规则仍为 `dragonsleep-rules-8`，内核仍为 `piegame-kernel-1`。仅当 Unity Game 为 `Session.ActAsync(text, ct, transaction)` 显式传入事务时，玩家自由行动走真实 `Kernel.Begin → PrepareAsync → Commit`。默认提供方仍是离线 `RuleProvider`；接入内核不会自动联网。

## 入口与依赖

| 入口 | 当前提交路径 |
| --- | --- |
| Game 输入框“行” → `ActAsync`，显式注入事务 | 本轮第三条内核入口 |
| 不传事务的 `ActAsync`，包括 headless / 研究 CLI / 黄金 | 原 Session 与执行器路径 |
| `RunStepAsync` / `RunPlanStepAsync` 的 FixedProvider | 原路径，不随 Adjudicate 枚举自动迁移 |
| `RunWithProviderAsync` 研究扰动 | 原路径 |
| Dialogue / Craft / AgentPlan / Direct / Plan / `EndPhaseAsync` | 原路径，不属于本轮提交权威迁移 |

因此同一个 `Adjudicate` 有两种路径，选择依据是明确入口及注入，不是任务枚举全局开关。

`Town.Core` 只声明 `IDsActionTransaction`、请求 / 准备接口、不可变评估及单次 `DsActionWrite`。新 `Town.PieGame` 无引擎程序集引用 Core 与 Kernel；`dotnet/Town.PieGame` 通配编译同一份源码。Unity 只通过 `DsActionKernelBridge` 包装现有 `UnityKernelBridge` 的所有者线程和生命周期；scene / day / action 共用同一个桥、同一个 `DsKernelStatePort`，没有第二份权威世界。

## 冻结与纯候选

1. owner 上 `BeginCall` 完成现有裁剪感知和提示后，`KernelActionTransaction.Begin` 对 packet 做私有 `FreezeCopy`，确定编码为 UTF-8。binding 包含 `Adjudicate`、`player`、Kernel 协议和宿主规则版本。
2. 编码覆盖 schema、instructions、各 section 文本 / 不可信标记、冻结任务视图 JSON 与原玩家输入；不含完整世界快照。公开 trace 的 packet 后续改动不能修改私有副本。
3. Provider 适配器调用前重新核对收到的 payload、request 和私有 packet 指纹。只传 Begin 时的同一个私有 packet，不读取 live World 或重新组装。错字节或 binding 直接失败，不 fallback。
4. 正常 `PrepareAsync` 由真实桥 / .NET adapter 的 `Task.Run` 执行。`ActionTaskHandler` 只从 Kernel 的 before JSON 恢复独立世界，沿用 snapshot 的 seed、roll counter、spent 与预算，调用新执行器的 `Prepare`；不 Commit、不访问 live Session、trace 或事件。
5. 坏 JSON 对应 `FormatException`；结构 / 引用失败对应 `HostRejection`。规则拒绝不等于没有后果：已经掷骰、耗时或跨时段换岗的候选仍交 Kernel 提交。

评估旁路按请求隔离，标量和集合复制；`WorldChange` 每次取出深复制。Session 只在 owner 投影它，handler 不写公开 trace。隐私测试对玩家看不到的居民 / 雏龙位置做差分，安全 payload 字节保持相同。

## 唯一提交权威及保留职责

可直接核对这些代码位置（行号以本次提交为准）：

- `DragonsleepSession.cs:479–480`：仅旧路径保存宿主 `RequestHash`，新路径不做回复前 `stale_context` 整世界预检。
- `:429–435`：新路径启动 Kernel 准备，owner 启动桥的 Task，块外等待，随后进入独立 `FinishActionTransaction`。
- `:556–558`：仅保留 `superseded`，这是“同角色已有更新请求”的宿主策略，不是第二套状态摘要 / 代际检查。
- `:586–615`：调用事务 Commit、关闭写能力、投影实际回执；**不调用 `DsExecutor.Commit`**，不读取其 `_committed`。
- `DsKernelStatePort.Write → DsActionWrite.Apply(state) → Session.ApplyActionCandidate`：Kernel 提交锁内一次覆盖候选世界及 spent；进入锁前已恢复候选，锁内不解析 JSON、不另做世界规则裁决。

去重、冲突、代际、整状态摘要、取消均以 Kernel 回执为准。错误线程在访问端口前抛错；拒绝时不补走旧执行器。能力在返回或抛错后关闭，重复 / 迟到使用拒绝。`DsWorldSnapshot.CopyInto` 保持 live World 对象身份；宿主直接绕写、进程崩溃或 OOM 的强异常保证不在本轮范围。

**此入口的双重生命周期记账已移除，但整个宿主并没有因此删除旧管线。** 旧场景 / 日间接缝仍保留原宿主检查；模型裁决、展示、七阶段与旧回执投影仍是游戏职责。额外快照用于回执展示不参与第二次允许 / 拒绝裁决。

## 回执与费用

| Kernel 原码 | trace / 宿主处理 |
| --- | --- |
| `committed` | 实际提交；世界无差异时旧宿主 receipt 仍为 `no_change` |
| `duplicate` | 不再写入，公开说明已提交；保留原码 |
| `stale_state` / `stale_session` / `request_conflict` | 同名 FailureCode，拒绝迟到或冲突，不改成旧 `stale_context` |
| `cancelled` / `late_after_cancel` | 同名取消 / 迟到丢弃，不回滚先前已完成提交 |
| `parse_failed` | FailureCode `parse_error`，Kernel 原码另存 |
| `host_rejected` | 保留评估中的 `schema_invalid` / `unknown_reference` 等原失败；无候选写入 |
| `provider_error` | 优先保留原 ProviderResult.Error，例如预算拒绝、超时；空文本为 `empty_response` |

`AiTrace.KernelCode` 仅在内核路径追加序列化字段 `kernel_code`；默认路径没有该字段。ProviderResult 每请求独立复制，保留原错误、状态、incomplete 原因及 nullable tokens。费用副本只有 input / output usage 完整时才保留原估算；缺 usage 即 `null`，不把离线默认零当作已知费用。未改在线价格、预算策略或原提供方结果。

## Restart、Restore 与等待

Session 的 Restart 和空闲 Restore 触发 `StateReplaced`，同一 bridge 执行真正 `Reset`；即使 Restore 得到相同 JSON，Kernel 代际仍不同。等待中 Restore 保持 busy 拒绝。

Game 仅在自由行动已实际 Begin 的在途窗口显示“再来一局”，不取消旧 token 来代替代际治理。新局立即建立，旧请求真正结束前 Busy 仍保留；有效旧 candidate 到达 Kernel.Commit 得 `stale_session`，Game 不呈现旧 HUD、不续跑旧夜间。失败 preparation 按 Kernel 原有 candidate-null 优先级保留原错误，不要求它伪装成 stale。

等待期间 Session gate 仍占用：场景移动、日间推进、额外输入被挡。计划 / 夜间的忙时重启限制不放宽。世界替换事件在销毁时解绑，避免保留已销毁桥。

## 私有回放与内存

每次 `ready` 准备在宿主本地私有列表保存不可变 `ReplayFrame`；不是公开 trace / HUD / 模型字段，也不是证明历史已经 Commit。`.NET ActionKernelAdapter.PrepareReplayAsync` 直接调用真实 Kernel 的严格预检和 RecordedProvider，测试原文 / 指纹 / rules / before / 独立 trustedDigest 篡改均拒绝，另覆盖 after 篡改。可信摘要须在记录之外独立保管，不能拿待验证帧自己的新摘要冒充信任锚。

两套回放并存：`DsReplayFrame` 覆盖旧宿主链；本轮 `ReplayFrame` 覆盖自由行动内核候选。未合并格式，也未声称 Unity bridge 新增严格 Replay 入口。本地私有帧还保留 raw，当前未持久化为发布格式。

内核 `_requests` 的 before 一直保留到 Reset；未修改 Kernel 或做删除 / 淘汰优化。私有录制列表随适配器生命周期保留，不因Reset自动删除。实际量测与代码成本见 [061 日志](开发日志/2026-11-11_061_玩家行动模型请求接入PieGame内核.md)。before UTF-8 字节不是托管堆 / RSS：还有 UTF-16 字符串、字典、候选、提示及私有回放开销，不能只据其大小断言内存达标。

## 验证与结论边界

全量离线 .NET **423/423**（原 357、当前 Town362 / Kernel61）；Unity **89/89**（原 83、当前游戏70 / 独立19）。默认 90 / 27 局全部核心指标相同，原黄金未重录，规则与冻结案例不变。新测试覆盖纯候选跨线程一致、完整状态 / 回执及去耗时 trace 对照、跨时段巡逻单次写入、全部失败映射、Restart / ABA、真实 UI 在途重开和私有回放正反例。

本轮没有真实模型请求、未读取密钥或预算文件。新 Player 以实际构建与分模式摘要判断，不能拿新模式替代旧模式；最终数值在 061 中记录。结论只到 **“第三条入口验证通过”**，不扩大为全部任务迁移、成熟 SDK、真人玩法或性能验收。
