# PieGame AI：首条场景事务接入

日期：2026-11-10。本轮把龙眠镇**玩家实体进入地点**这一条同步、零耗时事务接到 `PieGame.AI.Unity`。不是全 runtime 迁移，也没有替换模型、计划、造物或夜间提交链路。

## 调用路径与唯一写入

```text
Game.UpdatePlace（Unity 主线程）
  → Session.EnterPlace（取得会话 gate）
  → DsExecutor.PrepareRule（隔离候选，原规则只执行一次）
  → DsExecutor.Commit（原有去重、过期、失败检查）
  → DsSceneRuleTransaction
       → bridge.CaptureState（读取当前权威 Session 快照）
       → before.Json / Hash 精确核对
       → bridge.Transact
            → Kernel 短锁：比较 → port.Write → 登记去重
            → 受控 DsSceneRuleWrite.Apply 一次
  → 宿主生成原格式 DsExecutionReceipt / DsRuleEvent
```

端口没有影子世界。候选反序列化和规则执行在提交之前完成；写入能力只覆盖预先准备好的 `DsWorld`，保持 live World 对象身份，不操作 Transform、动画、UI 或任意委托。SDK 成功后执行器**不再补写一次**；SDK 拒绝也不回退旧路径。

## 硬边界

- `Town.Core` 不依赖 PieGame 或 Unity，只定义窄接口 `IDsSceneRuleTransaction`；adapter 在 `Town.Unity`，该程序集显式引用 Kernel 和 Unity 桥。
- 默认不传 adapter 的 Core、CLI、回放与计划执行路径不变。场景 Game 的实体位置回写才传入 adapter。
- 必须是 `enter:` 规则候选，且 before / after 的 `Spent`、`PhaseBudget` 相同，`TimeCost=0`；其它有变化的事务传入 adapter 直接拒绝。
- 会话 gate 必须包住同步调用，scope 内没有 await。端口 Read 不再申请同一 gate；adapter 的私有 scope 验证端口不能在普通调用期间外访问。
- adapter 在进入 scope 前检查创建线程；错线程不能执行宿主写入。原规则候选的计算本身仍归调用方和 Session 所有。
- `DsSceneRuleWrite` 只能由执行器创建；成功至多 Apply 一次，backend 返回或抛错的 finally 都立即关闭能力。保存能力后晚调用无效。
- `status == committed` 必须与能力实际 `Applied` 一致；假报成功或写完又报拒绝属于契约错误，不能用 fallback 掩盖。

## 原语义如何保留

执行器仍先按 `SessionGen + RequestId` 与原 identity 判定 duplicate / request_conflict，再检查完整快照 stale，最后处理候选失败。`no_change` 仍登记宿主去重、`Applied=false`，不调用 SDK。只在真实 changed 时让 SDK 再做 compare/write；旧回执的键序、时间、版本和差异生成方式不变。

`LastReceipt` 是本条接缝的 SDK 回执，不替代所有宿主回执；主线其它事务不会刷新它。不能把历史 LastReceipt 当成最近任意游戏动作的结果。

## 重开

Game 同时要求 `!Busy && !Session.Busy`，先重建 Session，再调用 adapter 独立 reset scope。此时桥的 Reset 只核对其输入恰好是当前新局 JSON，端口不再次覆盖世界；内核代际和去重记录真正更新。scope 退出后，旧局标识不能通过原记录恢复旧世界，新局允许合法复用请求 ID。

普通 Kernel 状态戳不直接暴露给场景调用方；它只在本次同步 scope 内创建、使用、丢弃。没有公开“持久 stamp + 任意写入”入口。

## 保障的边界，而非全局承诺

本条写入依赖 Session gate、SDK 同一短锁和受控无业务回调的 CopyInto；不是两个提交器先后写世界。预校验后复制内置数据不包含可恢复业务异常，但不声称操作系统故障或内存耗尽下的强异常安全。

`DsWorld` 仍公开可变。051 当时除本入口外均由旧会话执行；053 另接入[日间世界＋spent 原子推进](PieGame日间原子事务接入.md)，不是放开任意 PhaseStart。AI、Craft、计划内部移动 / 耗时及黄昏 / 夜间仍未迁移。绕过会话直接改字段、恶意宿主回调、多个 kernel 争写同一端口不在保证内；不声称所有 live write 已统一、全游戏提交都在主线程，或 Unity 严格回放已完成。

桥 Completed 在内核锁外发布，但本次 adapter scope 和 Session gate 尚未退出。观察者不得重入会话、桥或直接写世界；实际 Game 不安装这种监听器。

## 证据路径

- Core 测试：旧 / 新完整 JSON 和规则事件逐字一致、身份保持、零耗时、去重不重复写、stale 不 fallback、能力结束后失效、非移动拒绝、假成功报错。
- 真实主场景 PlayMode：普通移动、巡逻抓捕、受惊、黄昏无岗、错误线程、锁内 stale、重开 ID、仅 Session 忙、计划内部仍未迁移。
- 显式 Player `--dragonsleep-journey-scene`：真正点击地图去工坊、回集市；两次 SDK 提交与宿主前后摘要一致，零游戏时间、零模型请求。不混入原完整短局的 hash 或伪装成全管线迁移。

最终执行成绩见开发日志；接口静态存在不代表已编译或跑通。通用桥边界见 [Unity 桥契约](PieGameUnity桥契约.md)，纯内核边界见 [内核契约](PieGameAI内核验证契约.md)。
