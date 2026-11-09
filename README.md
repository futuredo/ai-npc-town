# 龙眠镇 · AI 原生游戏实验

一个三天后母龙将降临的奇幻小镇。玩家通过自然语言调查、谈判、行动和织造；五名居民各自行动，导演调整公共事件，结果落到同一份世界状态。

当前主场景为 `unity/Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity`。六机制已接入真实模型研究入口，普通 Play 仍默认离线。模型效果、失败样本、对照实验和验收边界见[六机制实测报告](docs/龙眠镇_AI六机制实测报告_2026-11-08.md)；[美术评审](docs/龙眠镇_美术方向与AI验证评审.md)保留上一轮视觉工作。

- [当前设计与架构 v6](docs/龙眠镇_世界观玩法与AI架构_v6.md)
- [当前进度](docs/项目进度.md)
- [运行与费用授权](docs/OpenAI接入与成本规范.md)
- [本轮开发日志](docs/开发日志/2026-11-08_026_六机制真实实验与执行回执.md)

以下保留月光工坊 / 未竟之镇的历史资源与方案入口，其“当前”描述以相应日志日期为准。

## 历史方案与资源入口

- **[资源利用清单](docs/资源利用清单.md)**：十九组逐包列出已在局内、技术储备、待适配/转换与后续用途；[全包目录 CSV](art/local-asset-catalog-2026-11-08.csv) 可检索。已购源包保留，不把暂未采用当废料。
- **[当前 Demo：月光驿站局内接入](docs/月光驿站局内接入.md)**：正常复用 KINGDOM 完整室外场景、AFS 整组工作站和已购 UI；三灯实际护送、补光继续、结果和重开。见[日志 020](docs/开发日志/2026-11-08_020_现成场景与资源落局.md)。
- **[第三人称 3C 重做](docs/开发日志/2026-11-08_021_第三人称3C重做.md)**：玩家直接移动、近景透视、镜头碰撞、走近互动；默认收起大面板。当前 Village 已安装，旧观察切片保留。
- **[首个执行切片：月光工坊](docs/月光工坊首个执行切片.md)**：候选校验、角色条件、玩家确认、两种真实路线、光路遮挡/稳定供光、回执与无网络回放。当前为显式人工样本，**不解析任意文本、不冒充真实 AI**；见[日志 019](docs/开发日志/2026-11-07_019_月光工坊执行闭环.md)。
- [Blender Skills 与 Unity 工具链](docs/Blender与Unity工具链.md)：项目级94技能、Blender MCP、Unity CLI和资产导入验收。

- [增量素材筛选与玩法建议](docs/增量素材筛选与玩法建议.md)：此前 17 组快照、两包样板、十段日常动作/60 组重定向采样、暂缓项和缺口清单；当前利用情况看上方资源清单。
- [首轮本地素材筛选](docs/本地素材筛选与接入清单.md)：此前六组素材、十五对象样板与问题记录；保留原始事实，不代表外盘现在只有六组。
- [URP 迁移记录](docs/开发日志/2026-11-07_016_URP管线迁移.md)：当前工程已使用 URP `17.3.0`，不是仅安装包；默认及全部质量档、材质、截图工具与 PlayMode 已验证。

- **[待确认：Demo 详细设计 v5](docs/未竟之镇_Demo详细设计_v5.md)**：整合全部首版玩法与固定规则，配套[AI 接入与实验规格](docs/AI接入与实验规格_v5.md)、[实施与轻量素材](docs/实施与轻量素材_v5.md)。一张小地图、三位纸偶角色、少量光学组件，0 必购起步。**尚未确认、尚未实现**；确认后按[history 清单](docs/history/README.md)归档旧方案，当前不提前移动。
- [项目进度](docs/项目进度.md)：已实现、验证证据、尚未完成的体验。
- [论文详细解读：AI 原生游戏综述与路线图](docs/论文解读_AI原生游戏综述与路线图.md)：原生定义、G/N 分类、六种机制、53 个案例、技术路线图与证据局限；与项目方案分开阅读。
- [AI 原生引擎接入 Demo 计划](docs/AI原生互动验证计划.md)：当前实施主线；已有离线候选执行接缝和素材短局，**真实模型与六类完整接入未实现**。
- [故事与玩法 v4](docs/未竟之镇_故事与玩法_v4.md)：奇幻世界、角色、完整故事与扩展内容库；不作为首版全量制作清单，**尚未实现**。
- [核心体验与推进计划](docs/核心体验与推进计划.md)：好玩在哪里、先做哪段完整体验、用什么证据决定继续扩展。
- [工程骨架说明](docs/工程骨架说明.md) · [研究验证计划](docs/研究验证计划.md) · [开发日志](docs/开发日志/README.md)。
- [白龙月灯素材筛选](docs/素材筛选_白龙月灯_v4.md)：Unity 商店/Fab 候选、故事用途、改造量与价格；按后续 URP、可转换 UE 素材筛选，尚未购买或导入。配套[素材交付规格](docs/素材与准备清单.md)与[候选 CSV](art/asset-candidates-v4.csv)。旧 v3 素材需求暂停。

以上旧计划暂保留，作为 v5 确认前的方向与历史参考，不再平行扩写。v5 只收敛设计与实验，不放宽现行 `action=none`、信息视图或 API 预算；确认设计也不等于授权付费运行。旧商品候选不是新轻量方案的必购清单。

旧版现代小镇、《传声筒》和《回音镇》的设定文档已退役并删除。v3 的失物/维修/拜师主线也已退役；[v3 文档](docs/未竟之镇_完整设计与后续计划_v3.md)仅保留为当前代码与历史实验的参考，不再指导新剧情制作。

## 当前可运行内容

### 当前整合 Demo：现成村落里的借光护送

打开 `unity/Assets/LocalThirdParty/Gameplay/Moonlight_Village.unity` 后 Play。这是已购 KINGDOM `DemoExterior` 的独立玩法副本，保留完整环境，实际路线在原生地形上。当前使用全屏第三人称透视和可操控的读者；平时只显示任务与附近交互提示，交谈时才展开已购皮肤面板。附魔工作站采用 AFS 成组 Prefab。

走近芬恩按 E → 选择离线方法、听条件 → 确认借光出发 → 跟随观察芬恩和三灯的真实路线 → 补光继续 → 全员抵达或二次确认提前收束 → 重开比较。面板阅读时暂停护送并阻止玩家误走，关闭后恢复原暂停状态；这不是战斗 RPG，也尚未实现自由造灯。

| 当前 Village 操作 | 作用 |
| --- | --- |
| WASD / Shift | 镜头相对移动 / 快走；按实际位移切换动作 |
| 按住鼠标右键 / 滚轮 / Home | 环绕 / 拉近拉远 / 重置跟随视角 |
| E / Esc | 对附近且视线可达的对象互动 / 关闭交互面板 |

玩家有实体碰撞与重力；镜头遇障即时收近、离开后平滑退远。当前行走片段只支持有限快走，未伪称完整跑步、脚部 IK 或手柄手感已完成。

**最新验证：115/115 .NET、19/19 Unity PlayMode。** 新增三项 3C 测试覆盖移动/动作、实体与镜头碰撞、距离/视线交互、面板与新局复位；保留上下路和实际 UI 流程回归。结果 `.local/3c-rebuild-20261108/final-test-result.json`。020 的 5/5 Python 工具测试与 16 项 PlayMode 保留历史证据，未改成当时已有 19 项。另保留 `Moonlight_Rescue.unity` 隔离短局。商业场景仅本机保存，不随 Git 分发。

**明确未完成**：真实模型/开放语义与自主多角色、工作台所有道具独立交互、新短局完整磁盘存档、声音/完整动作、性能调优和真人/人工画质验收。梅芙/瑟恩目前是待机视觉，不是三位 AI；输入文字只记录，人工样本不解析文字。不能把运行通过说成高品质成品。

### 新月光工坊：离线执行框架

打开 `unity/Assets/Scenes/Moonlight_Workshop.unity` 后 Play。选择人工候选、检查芬恩的条件、确认预留月焰：跟随引路实际走下路，双束符文验光稳定后走上路。切光、遮光、暂停、撤回与重开均可试；书签为**内存录制/回放，不是磁盘存档**。

输入文本会记录，但**不驱动样本选择**。当前只有一位角色和固定两方法，小月亮是自动引路代理，非开放造物；没有接入真实模型或自主多角色。此场景保留自制技术几何和 IMGUI 作回归。11/07 当时 **112 项 .NET、10 项 Unity PlayMode** 通过，历史证据在 `.local/asset-screening-20261107/playmode-final2-result.json`，不覆盖当时记录。

### 旧规则灰盒：回归基线

Unity Hub 打开 `unity/`，打开 `Assets/Scenes/Town_Graybox.unity` 后 Play。**默认离线，不读取 API Key、不发模型请求**；现在仍是灰盒场景与开发用 IMGUI，不是正式美术/UI。

渲染环境：Unity `6000.3.23f1` + `com.unity.render-pipelines.universal@17.3.0`。默认及全部 6 个质量档使用 `Assets/Settings/Rendering/Town_URP.asset`，Renderer 为同目录 `Town_URP_Renderer.asset`；新建灰盒/本地预览材质与截图工具也已适配 URP。迁移后 **100/100 .NET、6/6 Unity PlayMode** 通过；新增测试检查实际运行管线与质量档，而非只检查包名。

本机素材预览仍在 `Assets/LocalThirdParty/Review/AssetReview.unity`，没有替换灰盒角色或改动其 NavMesh。商业样板与派生贴图仅本地使用、未随 Git 分发，许可与逐材质视觉验收仍待核实。渲染截图与审计在 `.local/urp-migration-20261107/`。

新增人物预览另存为 `Assets/LocalThirdParty/Review/Incremental/IncrementalCharacters.unity`，包含四位 Fantasy Horde 人物与一个备选 Arashi，以及五个干净适配 Prefab 和共享待机控制器。**不是新玩法场景**；旧样板不覆盖。本轮审计/动作采样/截图在 `.local/asset-increment-20261107/`，详情见[日志 017](docs/开发日志/2026-11-07_017_增量素材与动作验证.md)。

1. 先到钟楼见霍勒斯，拿书签、了解落墨三规，解锁石板。
2. 走近居民打听消息，记进读者手记；选择如实转述、说成亲眼所见、添油加醋，或保密。
3. 到石亭找符文锤、到废稿井看幽光；自己还锤或请芬恩亲手交还，影响信任和拜师。
4. 在钟楼读取石板：**至少两位居民相信，且至少两个独立源头**才够落墨。玩家自己的相信不计入。
5. 18:00 墨钟落墨，查看第七章正文、结局与文字溯源；回钟楼拆开最后一页。

**旧灰盒仍是规则与工程基线，不是 v4 奇幻冒险或已验证的 AI 原生样板。** 新入口已有有限借光护送、龙远景和人工条件确认；完整龙背/月灯世界、缝影女巫/鹿骨骑士、定页/借影/月焰、开放语义与自主角色仍未完成。旧灰盒识破/带人见证等也未齐备；不能把按钮规则或方案文字当成模型成果。

| 操作 | 作用 |
| --- | --- |
| WASD / Shift | 镜头相对移动 / 加速 |
| 鼠标左键点地 | 完整路径检查后移动；不可达会提示 |
| 鼠标右键拖动 / 滚轮 / Home | 环绕 / 缩放 / 重置跟随镜头 |
| E | 附近的默认调查或交谈；具体话题与传话在面板中选择 |
| F2 / F3 | 暂停继续 / 1x、4x 速度 |
| F5 | 重开本局，清空本局进度 |
| F6 / F9 | 本地单槽存档 / 读档；读档后暂停，按 F2 继续 |
| F1 / Tab | 真值调试面板（剧透，默认关）/ 切换调试居民 |

存档：`Application.persistentDataPath/unfinished-town/chapter7.json`，另有上一份 `.bak`。包含本局操作、对话结果与角色坐标；读档不重调模型。规则版本不兼容会明确拒绝，不悄悄迁移。本机明文存储，不是云存档或加密存储。

## 验证命令

```bash
dotnet test dotnet/Town.sln --configuration Release
dotnet run --project dotnet/Town.Sandbox --configuration Release -- best
dotnet run --project dotnet/Town.Sandbox --configuration Release -- rumor
```

测试和演示不需要密钥、模型服务或收费请求。依赖预先还原后可离线运行；同一个项目不要并发编译多个 Sandbox 进程。

Unity PlayMode 自动测试（先关闭这个工程的其他 Editor 实例；不要加 `-quit`）：

```bash
/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath "$PWD/unity" -runTests -testPlatform PlayMode -testResults "$PWD/.unity-foundation-tests.xml" -logFile "$PWD/.unity-foundation-tests.log"
```

若工程已打开，优先在该 Editor 的 Test Runner 中运行 `Town.Tests.PlayMode` 下的五组测试（`TownPlayModeTests`、`MoonWorkshopPlayModeTests`、`MoonRescuePlayModeTests`、`MoonVillagePlayModeTests`、`MoonThreeCPlayModeTests`），不要为测试再启动第二份。也可使用 Unity CLI 的 `test run --mode playmode --filter Town.Tests.PlayMode --async-tests true`，再用 `test status` 获取结果；命令都显式传入 `--project-path "$PWD/unity"`。新素材场景测试需要本机商业资源齐备，不是纯代码仓库即有的 CI 资源。

## Unity CLI

本机已安装 Unity CLI `1.0.0-beta.15`，工程已安装 `com.unity.pipeline@0.10.0-exp.1`，实际连接与只读 C# 查询已验证。打开工程后在仓库根目录执行：

```bash
unity --version
unity command --project-path "$PWD/unity" --format json
unity command script eval --code 'return UnityEngine.Application.unityVersion;' --project-path "$PWD/unity" --format json
```

每次明确指定工程，避免误操作其他 Unity 项目。命令以 `unity command` 实际发现的列表为准；无头 Editor 可连接但未必列在 `unity status` 中。Pipeline 是本地开发控制工具，不需要模型密钥；本轮未修改全局 MCP 配置。

## 目录与边界

```text
unity/Assets/Town.Core/       纯 C# 规则、认知、对话隔离、剧情与存档
unity/Assets/Town.Unity/      导航、玩家控制、镜头、表现与灰盒面板
unity/Assets/Town.Editor/     灰盒生成与截图工具（重建会覆盖生成的场景）
unity/Assets/Town.Content/    正式素材目标目录，当前待导入
dotnet/                      链接复用 Core 的测试、Sandbox 与独立 OpenAI 验证入口
docs/                        当前设计、工程说明、研究方案与开发日志
art/asset-manifest.json      素材来源、许可证与导入状态
```

- 世界修改走 `WorldState.Commit`，认知写入走带来源的 `KnowledgeStore.Learn`；模型只能拿当前角色的 `NpcPerceptionView`。
- 当前模型接口限对话，`action=none`。台词不是已执行动作；日程、传播和落墨由规则裁决。
- [OpenAI 接入与成本规范](docs/OpenAI接入与成本规范.md)：密钥只放被忽略的 `.env.local`，默认模型 `gpt-6-luna`，真实验证必须另获授权。
- 单人直接在 `main` 开发；不强推、不重写已推送提交，见 [协作规范](docs/分支与协作规范.md) 和 [AGENTS.md](AGENTS.md)。
