using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 调用方明确选择的计划条件，不从自然语言或模型自报意图猜测。只限制计划中的 confide；
    /// 不承诺阻止玩家另行对话、出示物品或居民自行传播已知消息。
    /// </summary>
    public sealed class DsPlanConstraints
    {
        public const string Version = "ds-plan-constraints-v1";
        readonly HashSet<string> _allowed;
        public static DsPlanConstraints Unrestricted { get; } = new DsPlanConstraints(null);
        public static DsPlanConstraints KeepPrivate { get; } = new DsPlanConstraints(Array.Empty<string>());
        public bool HasRestrictions => _allowed != null;

        DsPlanConstraints(IEnumerable<string> allowed)
        {
            if (allowed == null) return;
            var ids = allowed.ToArray();
            if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 64))
                throw new ArgumentException("托付对象 ID 无效", nameof(allowed));
            _allowed = new HashSet<string>(ids, StringComparer.Ordinal);
        }

        public static DsPlanConstraints Only(params string[] actorIds) =>
            new DsPlanConstraints(actorIds ?? throw new ArgumentNullException(nameof(actorIds)));

        public bool Allows(DsPlanStep step) => !HasRestrictions || step.Op != "act" || step.Verb != "confide" || _allowed.Contains(step.Target);

        public string Check(DsPlan plan, IDictionary<string, string> selection)
        {
            var violation = plan?.ActiveSteps(selection).FirstOrDefault(s => !Allows(s));
            return violation == null ? null : "步骤 " + violation.Id + " 的托付对象不符合玩家明确选择；不会移动或执行该步骤。";
        }

        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["version"] = Version,
            ["allowed_confidants"] = _allowed?.OrderBy(id => id, StringComparer.Ordinal).Cast<object>().ToList(),
            ["scope"] = "只限制本次计划及重规划中的 confide，不限制其他输入或居民流言；不得自行换用未授权对象",
        };
    }
}
