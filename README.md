# AI NPC 小镇

研究课题：面向小镇社会交互的 AI NPC 记忆与行为系统。文档见 [docs/](docs/)。

当前入口：[《未竟之镇》完整设计与后续计划 v3](docs/未竟之镇_完整设计与后续计划_v3.md) · [玩法设计 v2](docs/玩法设计_回音镇_v2.md) · [素材清单 v3](docs/素材清单_未竟之镇_v3.md) · [素材交付规格](docs/素材与准备清单.md) · [工程骨架说明](docs/工程骨架说明.md) · [开发日志](docs/开发日志/README.md)。

## 目录

```
docs/                     策划大纲、实现大纲、排期
unity/                    Unity 6000.3.23f1 工程
  Assets/Town.Core/       纯 C# 核心（世界事实 / 角色认知 / 动作 / 模型请求），不依赖 UnityEngine
  Assets/Town.Unity/      表现层 MonoBehaviour
  Assets/Town.Editor/     编辑器工具：灰盒场景生成、命令行截图
  Assets/Town.Content/    正式素材与配置收纳目录（当前待导入）
  Assets/Scenes/          Town_Graybox.unity
dotnet/                   复用 Town.Core 源码的 dotnet 工程，用于命令行编译和单元测试
  Town.Sandbox/           完全离线的一整天演示（best / rumor 两条路线）
art/                      素材清单、来源与制作约定
```

## 常用命令

跑核心单元测试（不需要打开 Unity）：

```bash
dotnet test dotnet/Town.sln --configuration Release
dotnet run --project dotnet/Town.Sandbox --configuration Release -- best
dotnet run --project dotnet/Town.Sandbox --configuration Release -- rumor
```

重新生成灰盒场景（Unity 编辑器必须关闭；编辑器里也可以用菜单「Town/生成灰盒场景」）：

```bash
/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -quit -projectPath unity -executeMethod Town.Editor.GrayboxSceneBuilder.BuildFromCommandLine -logFile .unity-build.log
```

## 运行

用 Unity Hub 打开 `unity/` → 打开 `Assets/Scenes/Town_Graybox.unity` → Play。

《回音镇：誓钟响起之前》灰盒版，默认离线、无 API 费用。右侧是信使面板：

1. 点地面走到居民身边，用「问：……」打听消息，回答会记进**信使手账**并注明听谁说的。
2. 在手账里点选一条，再对身边的人选择「如实转述 / 说成亲眼所见 / 添油加醋」；不说就是保密。
3. 去旧哨塔石亭查看箱子、去旧井探头看看；去钟楼读**回音石板**（每条说法几人信、几个源头）。
4. 请芬恩亲手把符文锤还给布伦、向布伦承诺、向芬恩道歉；打听两人的心事，替他们牵线。
5. 18:00 誓钟敲响：被 ≥2 人相信且来自 ≥2 个源头的说法显化，面板显示结局、小报和流言溯源。

开发按钮可快进 30 / 60 分钟。玩法规则见 [玩法设计 v2](docs/玩法设计_回音镇_v2.md)，本次实现与验证见 [开发日志 003](docs/开发日志/2026-11-05_003_回音镇核心玩法.md)。

| 按键 | 作用 |
| --- | --- |
| 鼠标左键 | 点地面移动 |
| F1 | 开关真值调试面板（会剧透，默认关闭） |
| F2 | 暂停 / 继续时钟 |
| F3 | 1x / 4x 速度 |
| F5 | 重开本局（局代际 +1） |
| Tab | 调试面板切换 NPC |

## 约定

- OpenAI 已提供低成本验证入口，见 [OpenAI 接入与成本规范](docs/OpenAI接入与成本规范.md)。默认离线；显式 `--live` 才调用 API，单轮最多 3 次，模型固定为 `gpt-6-luna`。

- 分支流程见 [分支与协作规范](docs/分支与协作规范.md)：单人阶段直接在 `main` 开发，提交前本地跑测试。
- GitHub Actions 自动运行核心逻辑测试；该检查不代替 Unity 编辑器和实际玩法验收。

- 世界事实只能通过 `WorldState.Commit` 修改；MonoBehaviour 不直接改状态。
- 角色认知只能通过 `KnowledgeStore.Learn` 写入，并且必须带来源。
- 提示组装只能使用 `NpcPerceptionView`，拿不到世界真值。
- 模型 API Key 优先放在根目录 `.env.local`（已被 Git 忽略），不进入仓库或 Unity 资源。项目级约定见 [AGENTS.md](AGENTS.md)。
