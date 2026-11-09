# AI 原生框架实现参考：只借鉴关键接缝

核对日期：2026-11-07。用户允许寻找 GitHub 参考，但明确**当前框架进度优先**；本轮不继续扩展调研，不安装、克隆、运行这些项目，也不复制第三方源码。下面是公开代码定点阅读，不是整仓安全审计或本机运行结论。

| 项目 / 固定版本 | 借鉴点 | 本项目不直接照搬的部分 |
| --- | --- | --- |
| [Concordia](https://github.com/google-deepmind/concordia/tree/4e4aec94b76b937c6d491cc4fc414ab92fd080c5)，Apache-2.0 | `concordia/typing/entity.py` 的 ActionSpec 与“意图”；`environment/engines/sequential.py` 分开观察、行动、裁定与逐步记录 | 一步可能包含多次模型请求；叙事 GM 的裁定不能覆盖本项目物理、权限与资源事实，也不能得到所有私有认知 |
| [CivAgent](https://github.com/fuxiAIlab/CivAgent/tree/bd72fa0cd53ea681cee423360cb568115cb618e0)，MPL-2.0 | `civagent/action_space/skills.py` 的有限技能/参数目录，决策影响实际游戏动作而非只写台词 | 不照搬全套策略游戏技能，不复制 MPL 文件；未完成全部执行校验源码审计 |
| [TinyTroupe](https://github.com/microsoft/TinyTroupe/tree/7ce8be378a37ee73f9745d6781452712318afa04)，MIT | `tinytroupe/control.py` 的录制/缓存痕迹；`experimentation/in_place_experiment_runner.py` 的实验组织 | 不把模拟人格当真人，不把其缓存标识当密码学签名；Python/Notebook 和多轮调用不适合直接成为本 Unity 运行依赖 |
| [LLMUnity](https://github.com/UndreamAI/LLMUnity/tree/2c30b44020c7899153bbca02ec0faae73916c6ec)，Apache-2.0 | `Samples~/SimpleInteraction/SimpleInteraction.cs` 展示 Unity 侧请求与回调接缝，可作未来本地推理适配评估 | 示例主要是聊天，不能证明语义方法已改变规则；本轮未下载本地模型、验证硬件或更换默认模型 |

另列候选 [Voyager](https://github.com/MineDojo/Voyager/tree/55e45a880755d0c8c66ca7fb5fe7962ac8974f89)（MIT）：本轮只核对仓库与路径，**未完成行动/评审器源码审阅**。生成执行代码和多轮自动重试不适合直接越过当前权限与费用边界，不作为本轮已采用方案。

## 本轮实际采用的是模式，不是依赖

1. **意图不等于结果**：候选经过校验与确认，Unity 实际执行后才能产生回执。
2. **有限能力、有参数的契约**：不把任意文本、反射或生成代码作为世界修改接口。当前两种方法是最小执行样本，不是开放语义已经完成。
3. **逐步可观测与无网络回放**：请求、候选、确认、姿态、结果分开记录；复现实验时不重新抽样模型。

当前没有采用全知 GM、每帧自主调用、自动长链重试或“聊天适配器即 AI 原生”的方案。真实理解/协商与机制贡献仍需另做模型实验；[首个执行切片](月光工坊首个执行切片.md)只交付其可验证基础。
