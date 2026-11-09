# rules-6 重构黄金基线

建立于 2026-11-09，行为源提交 `f3b445a`。不联网、不读取密钥，不提供自动接受新哈希的开关。

- `perceptions.json`：冻结集 14 例，每例计划感知、五名居民各自对话/自主计划感知、完整提示、提供方原始计划 JSON、类型化计划重新序列化 JSON 的 SHA-256。测试链接原始 `PlanCases.cs`，不复制或修改冻结场景。
- `episode.json`：种子 7932，从新局规划并逐步执行，显式确认/重规划，继续世界演化到终局。按发生顺序保存全部 AI 轨迹及规则事件后取哈希；轨迹仅排除 `total_ms` 和各阶段 `ms`，其他字段、提示指纹、回执、键序全部保留。另固定计划执行记录与完整终局世界哈希。该局含 29 条 AI 轨迹、3 条规则事件，结局“归巢·余怒”。
- `recorded-plan-calls.jsonl`：从修改任何运行源码之前的 `20261109-092725-991-plan-offline/plan-calls.jsonl` 原字节提取 14 条 `first:` 录制。规划不改变世界，因此录制的 before 同时也是 after（测试核对原回执 after_hash）。用录制的提示分段、指令、`ds_plan_v1` 协议和原始回复构造严格回放帧；每条另验证错误提示指纹被拒绝。文件哈希也固定，禁止同轮重录掩盖漂移。

旧 `.replay.json` 是 rules-2/3，不能作为 rules-6 严格回放成功证据；未篡改它们的版本或快照。

运行：`dotnet test dotnet/Town.sln --configuration Release --filter FullyQualifiedName~DragonsleepGoldenTests`。
本机完整基线与研究 summary 另存仓库忽略的 `.local/refactor-baseline/`。本目录只存离线游戏数据，不含账户信息。
