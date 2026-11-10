# rules-8 黄金基线

2026-11-10；`dragonsleep-rules-8`。**本次仅整理既有磁盘工件与测试责任，不执行测试、不重录 fixture。** 这是规则升版本，不冒充 rules-6/7 旧兼容；完整治理、离线与真实失败证据见 [规则8治理与有限复验](../../../../docs/规则8治理与有限复验.md)。

## 工件、原件保全与回放责任

下表均为原文件字节 SHA-256；路径相对本目录。

| 文件 | SHA-256 |
| --- | --- |
| `perceptions.json` | `aaec5438d50c4d10f709d00b00cef65ab1058c41d827aad6460e8ae5e20ca567` |
| `episode.json` | `0aa55087b9491eb77f4bc0f7bef36b3f02795ea08d49485add19b36fdefcb3a4` |
| `plans.replay.jsonl` | `3c8c88a9e15aeb5810abbe7251a453375e115f7ffd863cfe9d1a7f59d618ec58` |
| `../rules-7/perceptions.json` | `c1977ab4c652975b874d22437bf5158c062198382fc0100d1adc7b4c79e341cc` |
| `../rules-7/episode.json` | `23f40a3b1e331676850c8a955061512fb170c1ff3b45c2670d89885ce0207c84` |
| `../rules-7/plans.replay.jsonl` | `dc9ac1565653da3f6ef3f3b41c08bf4f65cce1a8d47e5c2589c56908aa735958` |
| `../perceptions.json`（rules-6） | `94cd06ee5f02d19aa5fee1e1604323d84a199173addb4cffe767880300cd06f6` |
| `../episode.json`（rules-6） | `2b9ed4ddd12f3654dd2e7b2baeb36993c394f0ee25b7c2e63882c32ba31a38b8` |
| `../recorded-plan-calls.jsonl`（rules-6） | `815b8abd9dba89492876ebca98b7090ff1ba3055720acbe89f0951c8425f922e` |

`DragonsleepGoldenTests.cs` 承担以下断言：

- 新感知固定原冻结14例的计划、五位居民对话/自主计划视角、提示、原始提案与类型化计划记录；案例指纹仍为 `bfac3c38bc8b20fd156211217eda55385a4f7875abe37f1075fcb8026ea66147`，不提供自动接受快照开关。
- 新 episode 固定 seed7932 稳定轨迹、run、终局世界与计数；timeline 只排除总耗时/阶段耗时，保留版本、提示与回执。
- rules-6旧14条首案及 rules-7旧14帧保持原件哈希，当前 `VerifyAsync()` / `VerifyExecutionAsync()` **双入口拒绝跨规则版本**。后者可放宽提示，不放宽规则版本。
- rules-8新14帧 `VerifyAsync()` 正向成功；改错 `PromptHash` 或增加 `After` 世界金币均应 false。同版本负例才隔离验证提示/终态门禁；旧帧改错提示仍 false 不承担此责任。

新14帧都是模拟 Plan、`before==after`；与 rules-7相比全部版本字段变化，仅第5/6帧另变提示指纹与 response 输入统计，所有 before/after 及 raw 响应相同。不是改旧文件版本号来宣称旧回放兼容。以上成功/拒绝为测试责任，本次没有调用回放入口。

## 对 rules-7 的14例逐项磁盘差分

“无”指该例所有哈希字段相同；其余两例也只改下列字段。

| 案例 | 改变的哈希字段 |
| --- | --- |
| p01_goal_fresh | 无 |
| p02_goal_informed | 无 |
| p03_bonded | 无 |
| p04_cloaked_wait | 无 |
| p05_caged_rescue | `plan_perception`、`prompt` |
| p06_caged_pact | `plan_perception`、`prompt` |
| p07_helper_maren | 无 |
| p08_helper_pip | 无 |
| p09_helper_none | 无 |
| p10_late_market | 无 |
| p11_left_workshop | 无 |
| p12_injection | 无 |
| p13_unknown_guard | 无 |
| p14_partial_goal | 无 |

全部14例的 `dialogue:*` / `agent:*` 共10项及 `proposal` / `plan_record` 均相同；不是14例都改了计划感知/提示。测试允许这两字段变化的范围宽于当前磁盘实际差分。

seed7932 **仅 timeline 改变**：`fa394946e5889c356ab970b490d67a4182fac6bba6e53c8e3c56c781c2b428d3` → `d8bfada59976e1860ce4bde28bd420577b5c73e89020c1b7d4021d3fb2323125`。`run=a80a3a1f4fc84efa4dd5b6fc4c325eeb812c4b90c1e2bcf269f4ecbe90d5d3cd`、`world=a76eccca1fef8753cd0b5562a95740835361e0fa8559bdb2cea1ec5df1d8e888`、归巢·余怒、29条AI轨迹、3条规则事件均同。

## 不能外推的范围

默认离线90/27局的 episodes 与 rules-7既有对照相同，但输入 tokens / 提示协议变化，不能称整个报告全 equal。真实批次另为27次 Plan、9个非fresh夹具局，实际目标6/9（Solo2、Maren3、Pip1），不是提高至旧批7/9；费用**估算 USD 0.00582574**。无真实 schema_invalid 或重复 laws 样本，不能冒称新详细 issues 已被模型消费或去重防线经真实对抗验证。

研究 manifest 与当前源码有1项差异：`DsRules.Core.cs` 从循环 Distinct 改为读取 laws 时先 Distinct；正常无重复样本语义相同，源字节不相同。SDK/OpenAI源与 Session 哈希仍同。详见主文 manifest 段；不覆盖旧报告，不以 Golden/离线/预演代替 Unity 或完整玩法验收。
