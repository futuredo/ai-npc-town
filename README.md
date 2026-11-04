# AI NPC 小镇

研究课题：面向小镇社会交互的 AI NPC 记忆与行为系统。文档见 [docs/](docs/)。

## 目录

```
docs/                     策划大纲、实现大纲、排期
unity/                    Unity 6000.3.23f1 工程
  Assets/Town.Core/       纯 C# 核心（世界事实 / 角色认知 / 动作 / 模型请求），不依赖 UnityEngine
  Assets/Town.Unity/      表现层 MonoBehaviour
  Assets/Town.Editor/     编辑器工具：灰盒场景生成、命令行截图
  Assets/Scenes/          Town_Graybox.unity
dotnet/                   复用 Town.Core 源码的 dotnet 工程，用于命令行编译和单元测试
```

## 常用命令

跑核心单元测试（不需要打开 Unity）：

```bash
dotnet test dotnet/Town.sln
```

重新生成灰盒场景（Unity 编辑器必须关闭；编辑器里也可以用菜单「Town/生成灰盒场景」）：

```bash
/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -quit -projectPath unity -executeMethod Town.Editor.GrayboxSceneBuilder.BuildFromCommandLine -logFile .unity-build.log
```

## 运行

用 Unity Hub 打开 `unity/` → 打开 `Assets/Scenes/Town_Graybox.unity` → Play。

| 按键 | 作用 |
| --- | --- |
| 鼠标左键 | 点地面移动 |
| F1 | 开关调试面板（世界事实 + 选中 NPC 的认知与来源） |
| F2 | 暂停 / 继续时钟 |
| F3 | 1x / 4x 速度 |
| F5 | 重开本局（局代际 +1） |
| Tab | 调试面板切换 NPC |

## 约定

- 分支流程见 [分支与协作规范](docs/分支与协作规范.md)：`main` 保存阶段基线，`develop` 集成日常开发，功能通过独立分支和 PR 合入。
- GitHub Actions 自动运行核心逻辑测试；该检查不代替 Unity 编辑器和实际玩法验收。

- 世界事实只能通过 `WorldState.Commit` 修改；MonoBehaviour 不直接改状态。
- 角色认知只能通过 `KnowledgeStore.Learn` 写入，并且必须带来源。
- 提示组装只能使用 `NpcPerceptionView`，拿不到世界真值。
- 模型 API Key 只放在本地环境变量或 `.env`（已被 git 忽略），不进仓库。
