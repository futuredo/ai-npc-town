# 057：PieGame 生产 Session 所有者调度

日期：2026-11-10。与 rules-8 规则治理分别提交；本轮复用 055 的调度基础件，真正注入 Unity 主场景 Session。

## 目的与关键决定

此前真实 TCS 线程刻画证明：Game 外层 await 回主线程，不能修复 Session 内部已在 worker 读写世界、trace 和图样待槽。只搬 Commit 会留下前半段和异常路径。本轮以窄接口把单次七阶段管线拆为 owner 冻结、块外 provider 等待、owner 终结；保留原串行 gate 跨 await，不新增后台框架、自动提交或 Kernel 权威入口。

Core 新增 `IDsOwnerThreadDispatcher`，Session 构造器末尾可选注入；默认 null 保持研究 / .NET 线程行为。Game 实际添加现有 Unity dispatcher，adapter 只持纯托管接口。规则、提示、Kernel 与既有桥不因本 SDK 接线再变；规则8有意变化另记 056。

夜间收拢原同步推进块及 await 后检查，居民 / GM 顺序、规则事件及原执行器保持。造物待槽和首次通知在 owner，Game 主线程确认 / 放弃的第二次通知保留；未改原造物授权或覆盖语义。`DsPlanRun` 上层编排尚未全迁，不能宣称全部世界访问已经 owner。

## 验证与实际修正

- 新增 8 项 .NET 确定性 Session 回归：手动 owner pump、TCS 强制异步、首次冻结 / 回包派发、gate 竞争、取消、异常、队列关闭、造物两次通知、跨夜顺序；旧 null dispatcher 行为与完整快照 / 回执 / 事件顺序对照。
- 新增 3 项真实场景 PlayMode：行动 worker 回包后全阶段 / 完成事件在主线程，图样准备和确认 / 放弃分别通知，已排队后半段销毁后没有 worker 补写或补通知，gate 释放。关闭可取消，也可因收尾再次投递已关闭队列而明确 Fault；不强求所有情形都 IsCanceled。
- 联合 .NET **319/319**（Town 258、Kernel 61），证据 `.local/rules8-owner/final/*.trx`；主线规则8安全与新黄金责任另计，不冒称全部是 SDK 新增。
- Unity 首轮 **71/76**：旧表现测试只等两帧、跨夜测试在提供方进入前断言调用数、表现哨兵在请求尚未冻结前置空世界。保留原功能断言，改用有界的操作完成 / provider Entered 条件，不放宽产品行为或删除旧测试。
- 修正后联合 Unity **76/76**（游戏 57、独立桥 11、独立调度器 8），实际 XML 为 Passed、失败 0，证据 `.local/rules8-owner/playmode-verified.xml`。新增线程测试本身也全部通过。
- SDK 独立提交前再次完整复验：`.local/session-owner/dotnet/*.trx` 共 **319/319**（Town 258、Kernel 61）；`.local/session-owner/playmode.xml` 实际 **Passed，76/76，失败 0**。生产源码未再变化，实际包沿用同一已验证候选，不重复构建或虚报新增 Player 次数。

- 实际联合包 `Dragonsleep-macOS-local-20261110-130427-d0dc7676.app`，BuildReport Succeeded、0 错误 / 57 警告、3,117,834,991 bytes；14 次独立分模式运行均退出 0、`passed=true`、运行错误 0。摘要 `.local/rules8-owner/player-*.json`，构建日志同目录 `build.log`。
- Player 验证器新增纯托管计数器订阅 Session StageChanged / TraceCompleted，不在回调中访问 Unity 对象。默认 / 受限完整局各 450 次、社会前缀各 90 次、三种合作 300 / 330 / 315 次、日间模式各 15 次、末段模式各 150 次，错误线程回调全部 0。场景移动没有该管线调用，计数 0 是不适用，不能冒称异步覆盖。真正 worker 延迟覆盖来自上述 TCS PlayMode，而不是同步离线 Player。
- 这些回调不包括所有同步规则事件或 Plan 自身编排读写；不外推全部 runtime 已 owner。默认、社会、场景、合作、日间、末段的各自世界 hash 均与上一同模式验证一致。
- Unity 自动生成的四份设置已先备份 `.local/rules8-owner/generated-build-settings/`，再仅恢复本次生成差异，逐字节确认与 HEAD 一致，不提交缓存。新包 14 次通过后只清理被替代的旧 `120116-f047938c.app`，记录 `.local/rules8-owner/old-package-cleanup.json`；保留旧构建摘要、日志、回放、素材和玩家存档。

## 能力边界与下一步

场景移动与日间仍是两条 Kernel 权威提交入口；本轮是生产调度接线，不增加第三条事务计数。完整 Plan 续体、造物原始取消令牌 / 候选确认记录与待槽策略、Unity 严格回放仍未完成。保持 Busy 防读，不在主线程阻塞等待依赖 Update 的任务，不把 queue 关闭改成 worker fallback。

本 SDK 验证全部离线，没有额外真实 API；主线同轮 27 次真实 Plan、估算 USD 0.00582574 和 6/9 未过成绩单列 056。契约见[生产 Session 所有者调度](../PieGame生产Session所有者调度.md)。
