# 071 missing_output 响应形态可观测性

日期：2026-11-16　分支：`main`　任务：AI 验证推进套件任务二

## 目的与范围

本轮针对 rules-8 四机制诊断中第 82 次请求的 `missing_output` 停止，补充离线可验证的 Responses 响应形态诊断。范围仅包括 `Town.OpenAI`、研究器和离线测试；没有发起真实请求，没有修改 Core 规则、Unity runtime、PlanCases、旧黄金、商业素材、预算账本或用户草稿。

## 历史证据结论

第 82 次记录位于 `.local/four-mechanisms/live-requests.jsonl`，只有请求序号、时间、任务类型、角色、payload 哈希和空输入；`.local/four-mechanisms/STOPPED` 只有 `missing_output`。本地没有 response id、原始响应体或当时保存的响应形态字段。由于请求使用 `store=false`，不能从当前证据区分零段、多段或其他缺字段形态，也不能为了复原而重发请求。已知此前记录的 usage 为输入 670、输出 144 tokens；该历史失败没有可复原的真实费用明细。

## 关键设计

- `OpenAiResponseShape` 只保存安全元数据：status、`incomplete_details` 的 reason 与键名、usage token 数字、output item 类型序列、message content 类型序列、reasoning/refusal 数量，以及每段 `output_text` 的 UTF-16 长度、UTF-8 字节数和 UTF-8 SHA-256。
- 非字符串 `output_text` 只记录 token 类型，不读取或哈希其内容；不保存原始响应、台词、请求体、请求头或密钥。
- 仅对 `missing_output`、`incomplete_response`、`refusal` 触发私有研究目录事件；成功的恰好一段字符串输出不保留诊断摘要。研究器统一写入当前实验目录的 `response-shapes.jsonl`，不进入公开 trace、HUD、模型输入或回放字段。
- 多段文本不拼接，仍严格要求恰好一段字符串 `output_text`；失败码、usage 估算、无重试和原有成功解析保持不变。非完成响应的单段截断文本继续保留原有 `RawText` 行为，多段截断不再拼接。

## 实际验证

| 验证 | 结果 |
| --- | --- |
| 离线形态夹具 | 覆盖零段、两段、非字符串、只有 reasoning、refusal、单段 incomplete、多段 incomplete |
| 成功回归 | 原成功 `RawText` 逐字节语义保持；成功不触发响应形态摘要；公开 `AiTrace.ToRecord()` 不含诊断字段 |
| 隐私回归 | 摘要序列化不含测试密钥、Authorization/Bearer、请求头、拒答文本或完整响应 |
| .NET 全量 | Kernel 61/61，Town.Core.Tests 441/441，合计 **502/502**，0失败、0跳过 |
| Research 构建 | `dotnet build dotnet/Town.Research/Town.Research.csproj --configuration Release --no-restore`，0 warning、0 error |
| 真实请求 | **0**；本轮无真实 usage，因此费用估算为未知，不报告为零 |

本轮没有修改 Core 或 Unity runtime，不需要重复 Unity PlayMode；Town.OpenAI 共享源码通过 .NET 内存 HTTP 替身完成离线验证。`git diff --check` 无输出。

## 限制与下一步

该诊断只能帮助未来失败批次判断响应形态，不能恢复第 82 次已经丢失的原始 Responses 体，也不能证明服务端保存策略或任意模型语义。真实研究入口均只在显式 live 时挂接私有诊断，离线运行不产生摘要文件。下一步按既定顺序进入任务三：评估自由行动提示 v3 的生产集成门槛；若预注册晋升门槛未达，按要求回退 v2 并停止等待决定。
