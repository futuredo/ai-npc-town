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

065 增加显式行动提示版本，未增加SDK入口：`ActAsync(text, ct, transaction, actionPromptVersion)` 第四参数默认 `ds-action-prompt-v1`。当前 Game 自由行动明确传 `ds-action-prompt-v2`，其它未指定入口仍保持旧指令。v2 必须同时注入事务；不能仅因传入事务或任务枚举是 Adjudicate 就自动升级，见[提示版本契约](自由行动提示版本与分类治理.md)。

066 增加第五参数 `actionPerceptionVersion`，默认 `ds-action-perception-v1`；Game自由行动明确传v2，且须同时选择提示v2及事务。旧组合不增加空历史字段，也不向计划 / 其它机制自动传历史。新感知把最近四条核实的亲历行动及作用域说明冻结进TaskSnapshot；这是一条已有SDK入口的感知扩展，不是第四入口，见[066日志](开发日志/2026-11-12_066_亲历行动记忆与连续玩法对照.md)。

`Town.Core` 只声明 `IDsActionTransaction`、请求 / 准备接口、不可变评估及单次 `DsActionWrite`。新 `Town.PieGame` 无引擎程序集引用 Core 与 Kernel；`dotnet/Town.PieGame` 通配编译同一份源码。Unity 只通过 `DsActionKernelBridge` 包装现有 `UnityKernelBridge` 的所有者线程和生命周期；scene / day / action 共用同一个桥、同一个 `DsKernelStatePort`，没有第二份权威世界。

## 冻结与纯候选

1. owner 上 `BeginCall` 完成现有裁剪感知和提示后，`KernelActionTransaction.Begin` 对 packet 做私有 `FreezeCopy`，确定编码为 UTF-8。binding 包含 `Adjudicate`、`player`、Kernel 协议和宿主规则版本。
2. 编码覆盖 schema、instructions、各 section 文本 / 不可信标记、冻结任务视图 JSON 与原玩家输入；不含完整世界快照。公开 trace 的 packet 后续改动不能修改私有副本。
3. Provider 适配器调用前重新核对收到的 payload、request 和私有 packet 指纹。只传 Begin 时的同一个私有 packet，不读取 live World 或重新组装。错字节或 binding 直接失败，不 fallback。
4. 正常 `PrepareAsync` 由真实桥 / .NET adapter 的 `Task.Run` 执行。`ActionTaskHandler` 只从 Kernel 的 before JSON 恢复独立世界，沿用 snapshot 的 seed、roll counter、spent 与预算，调用新执行器的 `Prepare`；不 Commit、不访问 live Session、trace 或事件。
5. 坏 JSON 对应 `FormatException`；结构 / 引用失败对应 `HostRejection`。规则拒绝不等于没有后果：已经掷骰、耗时或跨时段换岗的候选仍交 Kernel 提交。

提示版本由不可变任务快照固定，感知 JSON 不加版本字段。v1 binding / 编码仍保持上述原字节；v2 通过 `ActionPromptCodec.BindingFor` 在 binding 与编码里追加提示版本，指令正文也参加原有指纹。OpenAI 安全重建沿冻结版本，不信任公开可变段落。Kernel 同版 v2 严格回放可验证，跨提示版本指纹不符即拒绝；旧 `DsReplayFrame` 未记录提示版本，不能用允许提示漂移的执行回放冒充 v2 严格验证。

感知版本与提示版本独立保存；只有感知v2额外编码 / 绑定其版本，并在安全感知JSON加入 `recent_player_actions`、`memory_scope`。Session在提交能力核验后、提交完成通知前投影私有记录，核对本地epoch防止重开回灌；`committed/no_change`拒绝与骰败也如实保留。记录不采集玩家陈述、模型narration、完整Effects或居民私有信任，对象名称来自当次冻结视角；历史不能授权当前持有、位置或效果。最近四条FIFO会遗忘旧项，不递归把旧记忆再次当当前观察。Kernel仍只裁决候选提交，不负责宿主记忆语义。

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

061接入时全量离线 .NET **423/423**（原357、当时Town362 / Kernel61）；Unity **89/89**（原83、当时游戏70 / 独立19）。默认90 / 27局全部核心指标相同，原黄金未重录，规则与冻结案例不变。测试覆盖纯候选跨线程一致、完整状态 / 回执及去耗时trace对照、跨时段巡逻单次写入、全部失败映射、Restart / ABA、真实UI在途重开和私有回放正反例。

061本轮没有真实模型请求、未读取密钥或预算文件。之后062–066的真实研究另行授权、另列费用与失败，不能把历史“零调用”套到后续。066最新工程门禁为 **464项.NET、91项PlayMode、18次实际Player**，默认90 / 27与历史黄金仍保持；记忆两臂96次真实请求的停止 / 续跑及有限玩法结果另见[报告](亲历行动记忆与连续玩法有限验证.md)。结论不扩大为全部任务迁移、成熟SDK、真人玩法或性能验收；回放一致、合法提交与意图理解也不能互相替代。
