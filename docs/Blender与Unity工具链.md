# Blender Skills、Blender MCP 与 Unity CLI

安装与验证日期：2026-11-07。

## 已安装内容

上游：[arjun988/blender-skills](https://github.com/arjun988/blender-skills)。固定提交 `904f095d8f36a6e72adaebb08d07f884a074743d`，MIT 许可。

- 项目技能目录：`.agents/skills/`。94 个独立技能、175 份参考文件，共 269 个上游文件；与固定提交逐字节核对一致。
- 共享参考：`.agents/skills/references/`。各技能自己的 `references/` 也完整保留。
- 来源和哈希：`.agents/skills/blender-skills-source.json`。
- 许可副本：`.agents/skills/LICENSE.blender-skills`。
- 使用官方 skill-installer 的路径/符号链接检查和复制函数，从已固定的 Git checkout 安装。下载归档等待无响应后使用此本地 checkout；未改写上游技能正文。
- 安装为项目级技能，无需向用户级目录再复制一遍；下一轮对话可发现本项目技能。安装后首次生效不等于已经执行过其中的建模流程。

这套库是给 Agent 的生产方法和检查清单。实际 Blender 操作通过 MCP，Unity 的查询、导入设置、场景和验证通过现有工具完成。技能本身不提供游戏内 NPC 决策或 AI 接入实现。

## 当前实测状态

| 项目 | 验证结果 |
| --- | --- |
| 技能发现基础检查 | 94 个 `SKILL.md`；94 个唯一名称；YAML 可解析，name/description 齐全 |
| 来源一致性 | 269 个上游文件逐字节及 SHA-256 核对 |
| Blender | 已打开本机 Blender，MCP 报告 **5.2.1 LTS** |
| Blender MCP | 既有插件 **1.8 / protocol 15** 与服务端匹配；能读取场景；无需安装第二套连接 |
| Blender 场景查询 | 实际返回默认 Cube、Camera、Light；未修改或保存当前场景 |
| Unity CLI | 实测 **1.0.0-beta.15**，能发现当前工程 Pipeline 命令 |
| Unity 工程查询 | 返回本工程 `unity/Assets`、**6000.3.23f1**、`compiling=false`、**Town_URP** |
| Blender→FBX→Unity 完整资产交付 | 本轮未执行；后续第一个资产任务单独做导出/导入/视觉验证 |

本机已具备可用的 MCP for Blender；上游附带 `.mcp.json` 使用 Windows 的 `cmd /c uvx ...`，没有复制进本项目。当前工具和上游旧版示例命名不同，以会话中实际工具 schema 为准。例如当前服务提供 `get_addon_status`、`get_scene_info`、`execute_blender_code` 与 `look`；不要机械套用旧截图工具名称。

## 按任务选择技能

| 工作 | 建议读取顺序 |
| --- | --- |
| 现有道具改尺寸/枢轴/拆分 | `prop-artist` → `asset-optimization` → `unity-export` |
| 模块建筑、场景组件 | `blender-director` → `environment-artist` / `scene-assembly` → `collision-proxy` → `unity-export` |
| 模型减面和 LOD | `asset-optimization` → `retopology` / `lod-pipeline` → `qa-review` |
| 人物骨架、权重、动作 | `rigging` → `animation` → `unity-export`；同时沿用 Unity 人形重定向与实际动作验证 |
| 贴图、材质与烘焙 | `uv-workflow` → `materials` / `texture-workflow` → `unity-export` |
| 参考图制作 | `blender-director` → 引用图分析清单 → 对应建模技能 → `qa-review` |

风格按项目和当前任务选择，安装风格类技能不代表项目确定了某种美术。每次只读需要的技能，不必把94份技能全部加载。

## 本项目资产链路

1. **任务规格**：先核实现有资产、用途、预算、碰撞/导航和动画要求。对 AI Native Demo 优先处理实际可交互部件和可执行动作。
2. **Blender MCP**：读取当前文件和场景，保留源资产；改造使用副本或独立 Collection。原始下载和已有 `.blend` 不直接覆盖。
3. **源与导出**：可编辑源索引放 `art/source/`；大型源文件沿用外置盘存储并登记来源。普通交付优先 FBX + 独立贴图，导出时明确单位、轴向、枢轴、骨架与动画片段。
4. **Unity 导入**：官方来源/授权完整的生产资产进入 `Assets/Town.Content/`；当前授权未核实的外盘资源继续在本机 `Assets/LocalThirdParty/` 中验证。保持原始与改造产物的来源关系，更新素材台账。
5. **引擎验收**：Unity CLI 定向本工程，检查一米尺度、脚底/枢轴、法线、URP 材质、碰撞与 LOD；角色检查 Avatar/动画/脚滑，最后看实际镜头。通过哪一层就只报告哪一层。

Unity 自带 FBX 路线适用于当前工程；上游推荐静态 GLB 是通用建议，不据此自动安装新导入插件。URP 的材质、透明和光照仍需在 Unity 中验收，不能用 Blender 渲染结果替代。

## 复查命令

在项目根目录运行：

```bash
unity --version
unity command --project-path "$PWD/unity" --format json
unity command script eval --code 'return UnityEngine.Application.unityVersion;' --project-path "$PWD/unity" --format json
```

Blender 可通过当前会话的 MCP 查询 `get_addon_status`、`get_scene_info`。如果提示无法连接，先确认 Blender 已打开、现有插件服务在运行；不需要把本仓库的 Windows 配置复制到 macOS。

技能安装和上述只读检查没有调用生成模型或收费服务。后续如果任务需要外部生成服务，仍按项目授权与成本规范执行。
