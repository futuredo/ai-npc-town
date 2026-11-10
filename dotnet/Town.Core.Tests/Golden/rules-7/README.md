# rules-7 黄金基线

日期：2026-11-10。规则版本：`dragonsleep-rules-7`。本目录与上一级 rules-6 原件并存，表示显式规则升级，不表示新旧行为完全相同。

本说明依据已见文件及 `dotnet/Town.Core.Tests/DragonsleepGoldenTests.cs` 整理；本次只做文件/字段/哈希核对，未运行构建、测试、Unity 或真实 API。下列回放成功/拒绝是当前测试应承担的断言，不冒充本次执行结果。

## 工件与责任

| 文件 | 当前责任 | 原字节 SHA-256 |
| --- | --- | --- |
| `perceptions.json` | 原 `PlanCases.Frozen()` 14 例的计划感知、五位居民的对话/自主计划感知、完整提示、原始计划 JSON 与类型化计划记录哈希 | `c1977ab4c652975b874d22437bf5158c062198382fc0100d1adc7b4c79e341cc` |
| `episode.json` | 种子 7932 的稳定 timeline、计划执行记录、完整终局世界哈希，以及结局/轨迹数/规则事件数 | `23f40a3b1e331676850c8a955061512fb170c1ff3b45c2670d89885ce0207c84` |
| `plans.replay.jsonl` | 14 条同版本 `dragonsleep-replay-v1` 自足计划帧；严格回放正例及篡改提示指纹负例 | `dc9ac1565653da3f6ef3f3b41c08bf4f65cce1a8d47e5c2589c56908aa735958` |

- 冻结案例指纹仍为 `bfac3c38bc8b20fd156211217eda55385a4f7875abe37f1075fcb8026ea66147`；测试直接链接 `dotnet/Town.Research/PlanCases.cs`，不得改场景、输入或断言来迁就新结果。
- 当前源文件原字节 SHA-256 为 `4554ae1f6ccc06ea939d040b01cc9c4e32d2acd3b1b51bd48e60f43ff7e52b67`，与新旧研究 source-manifest 记录一致。案例指纹只覆盖案例 ID、起点名、输入和断言说明，不能替代源文件完整性核对。
- 感知逐字段差分：14 例每例**仅 `plan_perception` / `prompt` 改变**；五位居民 `dialogue:*` / `agent:*` 的 10 项，以及 `proposal` / `plan_record` 的 2 项均与原件相同。不是全部感知与提示继续沿用旧哈希。
- 7932 逐字段差分：**仅 `timeline` 改变**，从 `56b6b8718e35dc64b6888877c503367bdc3d02ded211923cd70c9f7e217442f9` 变为 `fa394946e5889c356ab970b490d67a4182fac6bba6e53c8e3c56c781c2b428d3`；`run`、`world`、归巢·余怒、29 条 AI 轨迹、3 条规则事件均相同。timeline 保留规则版本、提示指纹和回执，仅排除总耗时/阶段耗时；摘要文件不含 timeline 展开，无法由其单独枚举底层全部字节差异。
- 新帧均为模拟计划响应、`before == after`；其 before 内容及 raw 响应与对应旧首案相同，新提示指纹与本目录感知工件一致。这是新版本录制，不是修改旧录制版本号来伪装兼容。

## rules-6 历史原件不得覆盖

上一级三个原件原字节保持如下：

| 文件 | 原字节 SHA-256 |
| --- | --- |
| `../perceptions.json` | `94cd06ee5f02d19aa5fee1e1604323d84a199173addb4cffe767880300cd06f6` |
| `../episode.json` | `2b9ed4ddd12f3654dd2e7b2baeb36993c394f0ee25b7c2e63882c32ba31a38b8` |
| `../recorded-plan-calls.jsonl` | `815b8abd9dba89492876ebca98b7090ff1ba3055720acbe89f0951c8425f922e` |

旧录制仍为 rules-6、14 行，并与历史研究 `20261109-092725-991-plan-offline/plan-calls.jsonl` 的 14 条 `first:` 原行字节相同。

责任迁移不是删除原保护：原冻结感知/提示检查改由本目录正向黄金承接，并增加新旧非目标字段差分；原整局检查改由本目录种子 7932 黄金承接，并保留旧原件哈希；原“rules-6 全部严格回放”改为历史原件保全及跨版本拒绝，本目录另立同版本严格回放正例。

`DsReplayFrame.VerifyAsync()` 与 `VerifyExecutionAsync()` **都必须先检查相同规则版本**。后者允许提示变化，不允许跨规则版本。因此旧 rules-6 帧在当前 rules-7 的两个入口均应 false，不能写成“旧 rules-6 在当前成功回放”。旧帧再改错提示仍为 false，不足以隔离验证提示门禁；本目录同版本帧的提示漂移负例才承担该责任。

当前测试方法分别为：

- `rules7冻结起点的计划与全部居民感知_提示与提案字节固定`。
- `rules7固定种子完整离线局_全部轨迹回执与终局哈希固定`。
- `重构前已落盘的rules6录制_当前版本严格拒绝_历史原件保持不变`。
- `rules6黄金原件保留_版本升级不覆盖历史预期`。
- `rules7冻结起点的新录制严格回放_提示漂移被拒绝`。
- `规则升级差分_冻结集仅计划感知提示与整局轨迹变更_其余预期不漂移`。

## 离线研究不能替代黄金或真实模型验收

详见[规则 7 黄金迁移与离线对照](../../../../docs/规则7黄金迁移与离线对照.md)，包含报告精确目录、原字节哈希、逐局行号/请求 ID 和缺失字段边界。

现有本机 frozen/holdout 报告均为 `live=false / offline-baseline`：90 / 27 局。低信任 `perturb_precondition` 的 10 / 3 局都实际公开要求原件保管，并在实际步骤中落下 applied 回执；重规划可执行不等于执行成功。实际 give、梅伦盟友和团聚字段为 9/10、3/3；frozen seed 5 两次 give Dice 失败、之后 Stale 并放弃，结局未竟。旧 rules-6 对应 10 / 3 局则全卡在确认前、无接受请求、执行步骤 0，不能描述成已执行后补救失败。

episode 只有回执摘要，缺少实际动作完整 verdict/稳定原因码/效果明细及完整终局快照；团聚使用落盘 `homecoming_achieved`（源码来自 `Whelp.WithMother`）核对，不仅凭结局名推断。holdout 首案预先断言仍是 8/10，且其整局 1～3 种子与 frozen 重叠。以上不是模型能力提升，也不是 M3 整体通过。

后续需实际运行黄金时，可使用原有离线测试入口（本次未执行）：

```sh
dotnet test dotnet/Town.sln --configuration Release --filter FullyQualifiedName~DragonsleepGoldenTests
```

不提供自动接受快照开关。任何预期修改都应先审查规则版本、允许差分、原件保全与证据来源，不得重录旧原件掩盖漂移。
