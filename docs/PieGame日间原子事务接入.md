# PieGame AI：日间原子事务接入

日期：2026-11-10。第二条真实入口是玩家按“结束这一时段”时的 **Dawn→Noon / Noon→Dusk**，不提供可随意重放的裸 PhaseStart。黄昏、深夜、AI 耗时与计划中的等待仍使用原管线。

## 世界与时间同一提交

旧日间逻辑先 Advance、清零 spent，再提交换岗；不能只把换岗塞入 SDK 后声称整个推进可拒绝。因此新 `Session.EndDayPhase(IDsDayPhaseTransaction)` 在同步 Session gate 内：

1. 拒绝非日间、终局、待确认造物或 Busy，不预写世界和时间。
2. 拍含世界、spent、预算的规范快照；仅在独立候选上 Advance、清零 spent、执行一次 PhaseStart，保留旧 Version / RollCounter 语义。
3. adapter 在所有者线程精确比较 live JSON / digest 与 before，由同一个 Kernel.Transact 短锁调用受控 `DsDayPhaseWrite.Apply()`，一次复制世界并更新 Session spent。
4. SDK 成功才产生已提交宿主回执和换岗事件；拒绝不 fallback、不补写、不清空真实时间。能力在返回或异常后关闭，不允许保存后晚到使用。

`Town.Core` 只声明接口与受控能力，不引用 Kernel 或 Unity；具体端口仍在 `Town.Unity/DsSceneRuleTransaction`。`Commits` 是本 adapter 总提交数，`DayCommits` 只计日间推进；普通模型、夜间、计划等待不刷新这些计数。LastReceipt 仅对应接缝事务，不能当作任意动作的最新回执。

## 兼容与局限

- 游戏世界本体身份不替换，spent 与世界同时到位；零/非零 spent、巡逻抓捕与未抓捕、黄昏无岗都需同旧完整 JSON 对照。
- 新入口无 await，不将 async 夜间续体硬送主线程桥；已有 FinalDawn 只评估和展示结局，不再推进时间或误入日间拒绝。
- 默认 Core 的 EndPhaseAsync、计划内部与模型提交仍保持旧路径。此处不声称所有 live write 已统一，也不承诺绕过 Session 直接改公开 World 的安全。
- 正常内置集合 CopyInto 不包含业务回调，但不声称 OOM / 系统故障强异常保证。恶意 backend 假报成功、部分写后抛异常属于宿主违反端口契约，不以重试或 fallback 隐藏。
- Reset 沿用首入口独立 scope 与代际失效，计数清零；Unity 严格回放仍未提供。

## 验证入口

- `DragonsleepDayPhaseTransactionTests`：17 个参数化用例，8 组旧/新完整 JSON 对照，拒绝、异常、能力撤销、busy、pendingCraft、终局、非日间。
- `DragonsleepKernelDayPhaseTests`：实际主场景按钮、非零 spent、换岗一次、SDK / 宿主摘要、错误线程、stale、重开与旧跨夜等待；不使用在线提供方。
- 显式 Player `--dragonsleep-journey --dragonsleep-journey-day --dragonsleep-journey-output <新绝对JSON>`：点击真实“行 / 观察 / 发送”后耗时 2，再由地图按钮推进两次。普通启动不执行，不能与其他验证模式混用。

实际结果见 [053 日志](开发日志/2026-11-10_053_PieGame日间世界与时间原子提交.md)；测试源码存在不代表运行通过。首条入口的独立限制见[场景接缝](PieGame首条场景事务接入.md)，通用线程与取消语义见[桥契约](PieGameUnity桥契约.md)。
