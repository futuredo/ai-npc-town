# PieGame：计划所有权与图样授权

## 计划异步编排

生产 Game 已注入 Session 的所有者调度器；`DsPlanRun` 现在复用同一 `Session.Owner`，不另建调度线程或第二份世界。

```text
owner 准备：绑定世界实例 / SessionGen / 操作代次，冻结当前步骤、上下文和条件
→ 块外 await 受控 Session 调用
→ owner 消费：复核操作身份与取消，采用回执，更新 Results / NextIndex / Replans / Adopt
```

- 移动、跨夜前后复核、等待循环、失败分类及完成判据在 owner 同步块内。提供方等待不在同步块内。
- Session 的原串行 gate 仍跨等待持有；计划自身额外禁止同一个 run 并发消费同一步。
- Session 真正开始请求、提交阶段通知返回后，以及跨夜下一次世界推进前，重新验证原 run 身份。不能只在回包后发现旧局，却已经向新世界提交。
- Restart / Restore 即使恢复相同 JSON、相同请求编号，也因世界实例身份不同使旧 run 失效；Abandon 更新操作代次并取消在途调用。
- 取消发生在实际提交前时不提交；已经提交的移动、夜间或行动不回滚。取消后不能把未采用的结果补记为成功，也不自动重试。
- 同步 `Accept`、`Abandon`、`MeetsObjective`、`ToRecord` 及公开集合的读取，仍由调用者保证在 owner 上进行；它们不自动派发，也不是任意线程安全 API。
- 队列关闭不回退 worker 写世界或计划列表，原 Session gate 仍释放。有主异常时收尾异常不覆盖主异常；没有主异常时，完成监听异常仍可传播。完成标记在通知前设置，避免异常导致重复通知。

默认 `ownerDispatcher = null` 的 CLI / .NET 保留同步宿主模式。规则、模型输入、提示键序、正常回执及黄金工件不因本次所有权接线重录。

## 图样确认是本机能力，不是模型字段

Session 仅通过 `GetCraftProposal(expectedTrace)` 返回与**本次 trace 引用**匹配的 `DsCraftProposal`。句柄没有公开构造器，冻结描述只读；授权以当前待槽句柄的引用身份匹配，不接受同编号、同 JSON、同摘要重建。

1. 已有待确认图样时，共用前置检查拒绝新 Craft，拒绝发生在调用提供方之前；不覆盖旧案。
2. Game 严格会话拒绝裸 `ConfirmCraft(bool)`。HUD 的描述、确认、取消及关闭都绑定所显示的同一个句柄。
3. 错误、旧、跨会话或重复句柄不消费当前候选、不改 trace、不通知；旧卡片也不能关闭新卡片。
4. 有效决定先领取待槽，恰好一次处理。原请求 token 已取消时生成 `cancelled` 终态，零材料、零耗时、没有执行器提交；取消本身不通过后台回调改槽。
5. 若在准备的 Commit 通知中取消，尚未入槽就拒绝，不产生可领取的 pending。准备完成后再取消，则由下一次 owner 确认 / 放弃惰性收尾。
6. 正常确认仍用原执行器的完整世界摘要检查，正确句柄不等于过期世界也能提交。提交完成后才取消不回滚已经发生的结果。
7. 图样准备通知一次，有效终态决定再通知一次；不能把这两个生命周期时点合并成按 RequestId 只通知一次。

受信任 headless 工具默认保留旧 bool 兼容入口，转到同一个当前句柄与 token 检查；Game 明确开启 `requireBoundCraftConfirmation`，自己和 HUD 不提供取最新槽位的 bool 适配器。

句柄、原 token 和本机身份不进入世界、模型输入或回放 JSON。`dragonsleep-replay-v1` 的正常 pending / declined / confirmed 回放保持；它没有记录取消操作，因此新增的 cancelled 决定明确拒绝严格重放，不伪装成 declined。

## 没有扩大到的范围

- Kernel 权威 compare/write 仍仅场景移动和日间世界＋spent 两条；计划 owner 接线和图样宿主授权不计新的 Kernel 事务入口。
- 没有把造物候选改成 Kernel Preparation / 确认令牌，也没有新增通用在线提供方或 Unity 严格回放。
- 暴露的可变世界和计划集合仍是受信任宿主接口；绕过 Session / owner 任意写入不获得线程或事务保证。
- 队列销毁不能强行终止不合作的提供方；只保证不在关闭后补写。主线程不得阻塞等待需要 Update 派发的任务。
- 本轮验证默认离线，不为证明线程和候选身份而调用真实付费模型。
