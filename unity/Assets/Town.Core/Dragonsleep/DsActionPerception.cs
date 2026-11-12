using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    /// <summary>玩家行动感知版本；历史记录不是当前物权、位置或执行权限。</summary>
    public static class DsActionPerception
    {
        public const string Legacy = "ds-action-perception-v1";
        public const string Current = "ds-action-perception-v2";

        internal static void Validate(string version)
        {
            if (version != Legacy && version != Current) throw new ArgumentException("unsupported_action_perception_version");
        }

        internal static string Instructions =>
            "recent_player_actions 是玩家亲历的最近四次规则裁决，不是玩家陈述或模型叙述。" +
            "其中的 item/item_name 可用来识别刚才提到的同一物件，但历史记录不证明现在仍持有、仍在原处或仍可使用。" +
            "例如当时已消耗的食物，玩家明确再喂同一份时仍提 feed 与该历史物件ID，由规则拒绝；不要凭记忆补回物品。" +
            "当下可用资源以当前 inventory/things_here/for_sale 为准；历史 target 不证明居民或雏龙现在在场。" +
            "若目标需要没有的资源，不要擅自重复消耗或改做另一件事；只理解本次明确的当前动作。";
    }

    // 不接收公开历史字典，不扫描 World、Trace 列表、完整差异、玩家文字或模型 narration。
    internal sealed class DsPlayerActionMemory
    {
        readonly Queue<string> _entries = new Queue<string>();
        internal void Clear() => _entries.Clear();

        internal List<object> Capture() => _entries.Select(text => MiniJson.Parse(text)).ToList();

        internal void Append(TaskPerceptionView source, DsActionEvaluation evaluation, string requestId, string clock,
            Dictionary<string, object> proposal)
        {
            if (evaluation == null || evaluation.FailureCode != null || source == null) return;
            var view = source.ToDictionary();
            var objects = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in new[] { "inventory", "things_here", "for_sale", "people_here" })
            {
                if (!view.TryGetValue(key, out var values) || !(values is List<object> list)) continue;
                foreach (string text in list.OfType<string>())
                {
                    int split = text.IndexOf('：');
                    if (split <= 0 || split > 64) continue;
                    string name = text.Substring(split + 1);
                    int suffix = name.IndexOfAny(new[] { '[', '（' });
                    if (suffix >= 0) name = name.Substring(0, suffix);
                    name = name.Trim();
                    objects[text.Substring(0, split)] = name.Length <= 48 ? name : name.Substring(0, 48);
                }
            }
            string item = proposal.Str("item") ?? "", target = proposal.Str("target") ?? "";
            string itemName = objects.TryGetValue(item, out var nameValue) ? nameValue : "";
            if (itemName.Length == 0) item = "";
            if (!objects.ContainsKey(target)) target = "";
            string outcome = "no_transfer_observed";
            // 只报告本次持有物的明确消耗/交付，不序列化其它 holder、私有数值或完整 Effects。
            if (item.Length > 0)
            {
                var change = evaluation.Changes.FirstOrDefault(c => c.Path == "items." + item + ".holder" && c.Before == "player");
                if (change?.After == "consumed") outcome = "consumed_by_this_action";
                else if (change != null && target.Length > 0 && change.After == target) outcome = "handed_over_by_this_action";
                else if (evaluation.Changes.Any(c => c.Path == "items." + item + ".holder" && c.After == "player"))
                    outcome = "acquired_by_this_action";
            }
            var entry = new Dictionary<string, object>
            {
                ["request_id"] = requestId, ["at"] = clock, ["verb"] = proposal.Str("verb"),
                ["target"] = target, ["item"] = item, ["item_name"] = itemName,
                ["result"] = evaluation.Kind.ToString(), ["item_outcome"] = outcome
            };
            _entries.Enqueue(MiniJson.Write(entry));
            while (_entries.Count > 4) _entries.Dequeue();
        }
    }
}
