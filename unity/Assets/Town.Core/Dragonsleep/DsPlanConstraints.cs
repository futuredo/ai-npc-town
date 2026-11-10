using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 调用方明确选择的计划条件，不从自然语言或模型自报意图猜测。
    /// v1 只限制 confide；v2 另约束契约交付及实际合作目标，均不承诺阻止其他输入或居民流言。
    /// </summary>
    public sealed class DsPlanConstraints
    {
        public const string Version = "ds-plan-constraints-v1";
        public const string CooperationVersion = "ds-plan-constraints-v2";
        readonly HashSet<string> _allowed;
        readonly bool _cooperation;
        public string RequiredHelper { get; }
        public bool HasCooperationIntent => _cooperation;
        public static DsPlanConstraints Unrestricted { get; } = new DsPlanConstraints(null);
        public static DsPlanConstraints KeepPrivate { get; } = new DsPlanConstraints(Array.Empty<string>());
        public static DsPlanConstraints Solo { get; } = new DsPlanConstraints(Array.Empty<string>(), true);
        public bool HasRestrictions => _allowed != null;

        DsPlanConstraints(IEnumerable<string> allowed, bool cooperation = false, string requiredHelper = null)
        {
            _cooperation = cooperation;
            RequiredHelper = requiredHelper;
            if (allowed == null) return;
            var ids = allowed.ToArray();
            if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 64))
                throw new ArgumentException("托付对象 ID 无效", nameof(allowed));
            _allowed = new HashSet<string>(ids, StringComparer.Ordinal);
        }

        public static DsPlanConstraints Only(params string[] actorIds) =>
            new DsPlanConstraints(actorIds ?? throw new ArgumentNullException(nameof(actorIds)));

        public static DsPlanConstraints WithHelper(string actorId)
        {
            if (actorId != "maren" && actorId != "pip") throw new ArgumentException("未支持的合作对象", nameof(actorId));
            return new DsPlanConstraints(new[] { actorId }, true, actorId);
        }

        public bool Allows(DsPlanStep step)
        {
            if (!HasRestrictions || step.Op != "act") return true;
            if (step.Verb == "confide") return _allowed.Contains(step.Target);
            // 当前只有梅伦的合作规则需要原件保管；请皮普与独行均不授权额外交付原件。
            return !_cooperation || step.Verb != "give" || step.Item != "pact_page" ||
                (RequiredHelper == "maren" && step.Target == "maren");
        }

        public string Check(DsPlan plan, IDictionary<string, string> selection, bool helperAlreadyAllied = false)
        {
            var steps = plan?.ActiveSteps(selection);
            var violation = steps?.FirstOrDefault(s => !Allows(s));
            if (violation != null)
                return "步骤 " + violation.Id + (_cooperation ? " 的托付或原件交付不符合合作条件" : " 的托付对象不符合玩家明确选择") +
                    "；不会移动或执行该步骤。";
            if (steps != null && RequiredHelper != null && !helperAlreadyAllied &&
                !steps.Any(s => s.Op == "act" && s.Verb == "confide" && s.Target == RequiredHelper))
                return "合作目标尚未达成：计划必须包含向指定居民托付并求得帮助，不能改为独行或只在摘要中声称完成。";
            return null;
        }

        /// <summary>只检查玩家已获得的公开盟友承诺，不读取 NPC 私有信任或未来是否愿意。</summary>
        public bool CooperationAchieved(DsWorld world) => RequiredHelper == null || world.Flags.ContainsKey("ally:" + RequiredHelper);

        public Dictionary<string, object> ToRecord()
        {
            var record = new Dictionary<string, object>
            {
                ["version"] = _cooperation ? CooperationVersion : Version,
                ["allowed_confidants"] = _allowed?.OrderBy(id => id, StringComparer.Ordinal).Cast<object>().ToList(),
                ["scope"] = _cooperation ? "本次计划与重规划必须遵守合作目标、托付名单和原件交付名单；不限制其他输入或居民流言"
                    : "只限制本次计划及重规划中的 confide，不限制其他输入或居民流言；不得自行换用未授权对象",
            };
            if (_cooperation)
            {
                record["required_helper"] = RequiredHelper;
                record["allowed_pact_custodians"] = RequiredHelper == "maren" ? new object[] { "maren" } : Array.Empty<object>();
                record["completion"] = RequiredHelper == null ? "独行：不得托付居民，也不得把契约原件交给居民"
                    : "指定居民实际答应帮助才算合作达成；只是 confide 失败或摘要声称已同意不算完成";
            }
            return record;
        }
    }
}
