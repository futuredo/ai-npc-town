# 龙眠镇 · AI 原生游戏实验

一个三天后母龙将降临的奇幻小镇。玩家通过自然语言调查、谈判、行动和织造；五名居民各自行动，导演调整公共事件，结果落到同一份世界状态。

**当前唯一玩法主线是《龙眠镇》v6。** v3 规则灰盒、月光工坊及月光驿站的代码、场景和专属验证已退役，不再作为运行入口或回归负担；历史方案和日志仍可查阅。

## 从这里开始

- [当前设计与 AI 架构 v6](docs/龙眠镇_世界观玩法与AI架构_v6.md)
- [项目进度与未通过项](docs/项目进度.md) · [工程骨架与源码阅读顺序](docs/工程骨架说明.md)
- [雏龙归巢切片计划](docs/雏龙归巢切片计划.md) · [M3 闭环复验](docs/归巢规划闭环复验_2026-11-09.md)
- [运行与费用授权](docs/OpenAI接入与成本规范.md) · [开发日志](docs/开发日志/README.md)
- [历史资料索引](docs/history/README.md)

六机制已有真实模型研究入口，不等于全部目标已验收。M3 完整护送为 33/40，同版离线基线 34/40；低信任求助和否定约束仍未通过。M4 计划交互已接入主场景并通过离线逐步按钮走到团聚，完整演出与真人小测仍待做，见 [044 日志](docs/开发日志/2026-11-09_044_M4归巢计划界面与异步收尾.md)。

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
```

依赖已还原后可加 `--no-restore`，避免包还原访问网络。CI 只跑离线测试。

现行场景表现回归为 `Town.Tests.PlayMode.DragonsleepPresentationTests`。关闭**本工程**其他 Editor 实例后，可运行：

```bash
unity test "$PWD/unity" --editor-version 6000.3.23f1 \
  --mode PlayMode --filter Town.Tests.PlayMode.DragonsleepPresentationTests \
  --output "$PWD/.local/cleanup-20261109/playmode.xml" --timeout 600 --format json
```

若工程已打开，优先在该 Editor 的 Test Runner 中执行，不为同一工程启动第二份。计划 UI 专项为 `Town.Tests.PlayMode.DragonsleepPlanPresentationTests`（13 项），将上方 filter 改为 `Dragonsleep` 可连同既有 4 项一起执行。本轮共 17/17；编译与自动回归不能代替真人手感、美术、设备性能或完整玩法验收。

局内按 **P** 或底栏“归巢计划”：写目标 → 提出 → 选取舍 → 确认 → 每次执行一步；失败后重新规划仍需再确认。取消规划会等待提供方收尾，不会接纳迟到草稿；放弃计划不撤销已经提交的后果。

## 目录与职责

```text
unity/Assets/
  Town.Core/Dragonsleep/      纯 C# 世界、规则、合法感知、AI 管线、计划与回放
  Town.OpenAI/               在线提供方、结构化输出、预算和密钥读取边界
  Town.Unity/Dragonsleep/     场景输入、角色表现、镜头、织言镜 HUD
  Town.Unity/Shared/          跨场景共享资源（保留原 GUID 的 UI 主题）
  Town.Editor/               场景构建、授权注入与素材/截图工具
  Town.Tests.PlayMode/       现行 Unity 集成测试
  PieGame.AI.Kernel/          独立可复用内核验证，尚未接入龙眠镇
  Town.Content/              预留内容目录，尚非已实现的数据驱动内容库
  LocalThirdParty/           本机商业素材及派生场景，不随 Git 分发
dotnet/                      共享源码编译工程、离线测试、Town.Research CLI
docs/                        现行设计/架构/报告、开发日志、history 历史资料
art/                         素材来源、许可证、导入与筛选审计
tools/                       素材索引/审计脚本及其测试
```

`dotnet/Town.Core`、`Town.OpenAI`、`PieGame.AI.Kernel` 直接编译 Unity 目录下的同一份源码，**不是三份重复实现**。六个 .NET 工程分别承担共享库、测试和研究宿主；两个旧 CLI 已删除。

AI 只提出方案：角色感知先冻结，结果经结构/引用/规则校验，候选世界裁决后才提交。对话仍为 `action=none`；造物必须确认图样，多步计划确认后逐步执行。PieGame 目前仅有独立维修调度 ToyHost 验证，不能称为已发布 SDK 或已接管游戏。

## 素材与协作边界

- [资源利用清单](docs/资源利用清单.md) 区分当前入口与旧素材使用快照；[全包目录](art/local-asset-catalog-2026-11-08.csv) 可检索，不把未采用的已购源包当垃圾。
- [Blender 与 Unity 工具链](docs/Blender与Unity工具链.md) 记录项目级技能、导出与导入验收。
- `.local/` 不全是缓存，包含当前实验与回放证据；`.local/openai/budget-*.json` 不得删除或重置。密钥仅保存在被忽略的 `.env.local`。
- 不清除玩家历史存档，不擅自迁移为龙眠镇存档；保留素材许可与来源记录。
- 单人直接在 `main` 开发；多会话按明确路径分工，不覆盖或暂存其他任务改动，见 [协作规范](docs/分支与协作规范.md) 和 [AGENTS.md](AGENTS.md)。
