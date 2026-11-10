# 龙眠镇 · AI 原生游戏实验

一个三天后母龙将降临的奇幻小镇。玩家通过自然语言调查、谈判、行动和织造；五名居民各自行动，导演调整公共事件，结果落到同一份世界状态。

**当前唯一玩法主线是《龙眠镇》v6。** v3 规则灰盒、月光工坊及月光驿站的代码、场景和专属验证已退役，不再作为运行入口或回归负担；历史方案和日志仍可查阅。

## 从这里开始

- [当前设计与 AI 架构 v6](docs/龙眠镇_世界观玩法与AI架构_v6.md)
- [项目进度与未通过项](docs/项目进度.md) · [工程骨架与源码阅读顺序](docs/工程骨架说明.md)
- [雏龙归巢切片计划](docs/雏龙归巢切片计划.md) · [M3 闭环复验](docs/归巢规划闭环复验_2026-11-09.md)
- [运行与费用授权](docs/OpenAI接入与成本规范.md) · [开发日志](docs/开发日志/README.md)
- [本机 macOS 试玩构建、启动冒烟与无人值守完整短局](docs/本机试玩与验证.md)
- [历史资料索引](docs/history/README.md)

六机制已有真实模型研究入口，不等于全部目标已验收。历史 M3 完整护送为 33/40，同版离线基线 34/40；低信任模型求助和通用自然语言否定约束仍未验收。047 补显式托付允许名单，确认、执行和重规划都会复核，不把“只向某人”伪装成必须求助或全局保密；当时 .NET 211 项、PlayMode 32 项通过，见 [047 日志](docs/开发日志/2026-11-10_047_M3明确托付条件与执行复核.md)。实际 macOS 包已有按钮完整短局、真实失败补救、结局与重开证据；灰盒美术和真人理解仍待验。

当前已升级 `dragonsleep-rules-7`：低信任梅伦可通过交出契约原件核验后再次托付，重复出示不再刷收益。离线低信任组实际补救归巢 9/10、3/3，235 项 .NET、36 项主线 PlayMode 与实际 Player 社会补救前缀两分辨率通过；旧 rules-6 黄金原件保留、当前拒绝跨版回放，详见 [048 日志](docs/开发日志/2026-11-10_048_规则七原件保管与低信任补救.md)。这不是新版真实模型成绩，也不等于 M3 整体验收通过。

050 继续修复基线忽略当前可见人员的问题，低信任离线组归巢改为 10/10，规则、提示与黄金未改。用户授权后新增 **82 次真实规划请求，估算 USD 0.02112381**：冻结首案 13/14 可执行，27 局实际团聚 25/27、低信任补救 3/3；但自然语言“不告诉任何人”三局都实际托付皮普，**M3 整体仍未通过**。范围、费用及反例见[真实有限批报告](docs/规则7真实模型有限批验证.md)与[050 日志](docs/开发日志/2026-11-10_050_当前观察优先与真实规划有限复验.md)。

052 新增显式 v2 合作目标：**独行 / 必须梅伦 / 必须皮普**，同时约束托付和契约原件交付，并以实际盟友和团聚判完成；v1 允许名单仍保持原义。本轮独立夹具真实验证 **22 次规划、估算 USD 0.00536897，实际目标 7/9**，不是自由文本理解或全新局成绩；皮普两局已结盟但护送失败，详见[有限证据](docs/合作意图v2有限验证.md)与[052 日志](docs/开发日志/2026-11-10_052_合作意图正向目标与有限真实复验.md)。

054 修正最后黄昏救出后绕回工坊的错误顺序，改为先到龙骨岗再做最后遮盖；规则、骰序与黄金不变。新的有限真实批仍为 **22 次规划、估算 USD 0.004982、实际目标 7/9**，不把条件性合法路线或成功夹具写成模型成功率提高。末段正常种子按钮夹具双分辨率通过，原两局风险与错误草稿继续保留，见[末段复验](docs/末段救援有限复验.md)和[054 日志](docs/开发日志/2026-11-10_054_最后黄昏救援与有限失败复验.md)。

## 打开与运行

环境：Unity `6000.3.23f1`、URP `17.3.0`、.NET 8。

1. Unity Hub 打开本仓库 `unity/`。
2. 打开 `Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity`，进入 Play。Build Settings 也只保留该场景。
3. 普通 Play 默认使用离线 `RuleProvider`，不读密钥、不发模型请求。真实调用必须从显式授权入口开启，见费用规范。

**主场景及商业素材在被 Git 忽略的 `LocalThirdParty/` 中。** 本机可以运行，不代表新机器只拉源码就能恢复完整画面；需要合法拥有相应原素材，按 [素材台账](art/asset-manifest.json) 与 [工具链](docs/Blender与Unity工具链.md) 恢复。`Town / Dragonsleep / Build Town Scene` 是重建玩法副本的工具，会重建生成节点，不是无副作用的查看命令。

## 离线验证

在仓库根目录执行：

```bash
# 核心、提供方和独立内核测试；不需要密钥或真实模型服务
dotnet test dotnet/Town.sln --configuration Release

# 当前规划研究的离线基线；只有显式添加 --live 才联网
dotnet run --project dotnet/Town.Research --configuration Release -- --plan --seeds=10

# 显式 v2 三种合作目标的独立夹具；默认离线，不并入原冻结集
dotnet run --project dotnet/Town.Research --configuration Release -- --plan-intent --seeds=3
```

依赖已还原后可加 `--no-restore`，避免包还原访问网络。CI 只跑离线测试。

现行场景表现回归为 `Town.Tests.PlayMode.DragonsleepPresentationTests`。关闭**本工程**其他 Editor 实例后，可运行：

```bash
unity test "$PWD/unity" --editor-version 6000.3.23f1 \
  --mode PlayMode --filter Town.Tests.PlayMode.DragonsleepPresentationTests \
  --output "$PWD/.local/cleanup-20261109/playmode.xml" --timeout 600 --format json
```

若工程已打开，优先在该 Editor 的 Test Runner 中执行，不为同一工程启动第二份。计划 UI 为 `DragonsleepPlanPresentationTests`（13 项），实体为 `DragonsleepStagePresentationTests`（9 项），短局 / 重开 / 社会 / 合作 / SDK 前缀为 `DragonsleepJourneyProbeTests`（6 项），托付条件 4 项、合作意图 3 项、原件补救 3 项、SDK 场景 7 项、SDK 日间 4 项；连同既有表现 4 项共 **53 项游戏回归**。独立桥 11 项另计，联合 filter 为 `Dragonsleep;PieGame.AI.Unity.Tests.PlayMode`，共 **64 项**。自动导航按钮回归不能代替鼠标命中、真人手感、美术、设备性能或全部玩法验收。

局内按 **P** 或底栏“归巢计划”：写目标 → 提出 → 选取舍 → 确认 → 每次执行一步；失败后重新规划仍需再确认。取消规划会等待提供方收尾，不会接纳迟到草稿；放弃计划不撤销已经提交的后果。

“合作条件”默认未限定；前三个受限项仍是 v1 仅托付允许名单，新增“独行 / 必须梅伦 / 必须皮普”才是 v2 正向目标与原件交付限制。条件随提出冻结，改下拉草稿须重新提出；两种协议都不拦截另行输入或居民流言，详见 [M3 条件与社会补救边界](docs/M3托付条件与社会补救边界.md)。

## 目录与职责

```text
unity/Assets/
  Town.Core/Dragonsleep/      纯 C# 世界、规则、合法感知、AI 管线、计划与回放
  Town.OpenAI/               在线提供方、结构化输出、预算和密钥读取边界
  Town.Unity/Dragonsleep/     场景输入、角色表现、镜头、织言镜 HUD
  Town.Unity/Shared/          跨场景共享资源（保留原 GUID 的 UI 主题）
  Town.Editor/               场景构建、授权注入与素材/截图工具
  Town.Tests.PlayMode/       现行 Unity 集成测试
  PieGame.AI.Kernel/          可复用内核，场景移动及日间推进经显式宿主接缝复用
  PieGame.AI.Unity/           主线程与生命周期桥，显式准备 / 提交，非全游戏迁移
  PieGame.AI.Unity.Tests.PlayMode/ 独立桥契约测试，不引用 Town
  Town.Content/              预留内容目录，尚非已实现的数据驱动内容库
  LocalThirdParty/           本机商业素材及派生场景，不随 Git 分发
dotnet/                      共享源码编译工程、离线测试、Town.Research CLI
docs/                        现行设计/架构/报告、开发日志、history 历史资料
art/                         素材来源、许可证、导入与筛选审计
tools/                       素材索引/审计脚本及其测试
```

`dotnet/Town.Core`、`Town.OpenAI`、`PieGame.AI.Kernel` 直接编译 Unity 目录下的同一份源码，**不是三份重复实现**。六个 .NET 工程分别承担共享库、测试和研究宿主；两个旧 CLI 已删除。

AI 只提出方案：角色感知先冻结，结果经结构/引用/规则校验，候选世界裁决后才提交。对话仍为 `action=none`；造物必须确认图样，多步计划确认后逐步执行。PieGame 已有 ToyHost、Unity 主线程 / 生命周期桥，实际接入**玩家场景移动、清晨→正午 / 正午→黄昏**；日间将世界与 spent 一起 compare/write，不先推进时钟再补交。**291 项 .NET、64 项联合 PlayMode**通过，同一实际 macOS 包 12 次分模式运行通过。模型、计划、黄昏 / 夜间与造物仍走原管线，不代表全游戏迁移或成熟发布 SDK；见[首入口](docs/PieGame首条场景事务接入.md)、[日间契约](docs/PieGame日间原子事务接入.md)、[桥契约](docs/PieGameUnity桥契约.md)与[053 日志](docs/开发日志/2026-11-10_053_PieGame日间世界与时间原子提交.md)。

## 素材与协作边界

- [资源利用清单](docs/资源利用清单.md) 区分当前入口与旧素材使用快照；[全包目录](art/local-asset-catalog-2026-11-08.csv) 可检索，不把未采用的已购源包当垃圾。
- [Blender 与 Unity 工具链](docs/Blender与Unity工具链.md) 记录项目级技能、导出与导入验收。
- `.local/` 不全是缓存，包含当前实验与回放证据；`.local/openai/budget-*.json` 不得删除或重置。密钥仅保存在被忽略的 `.env.local`。
- 不清除玩家历史存档，不擅自迁移为龙眠镇存档；保留素材许可与来源记录。
- 单人直接在 `main` 开发；多会话按明确路径分工，不覆盖或暂存其他任务改动，见 [协作规范](docs/分支与协作规范.md) 和 [AGENTS.md](AGENTS.md)。
