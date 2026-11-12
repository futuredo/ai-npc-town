using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Town.Core.Dragonsleep;

internal static partial class ActionKernelResearch
{
    internal const string FocusTransferSuite = "ds-action-current-intent-transfer-1";
    internal const string FocusTransferGate = "每臂48次完整；observe12/wait8/confide4/lure2全部意图及规定后果通过；"
        + "交付后重复show及购买后重复buy各2次均实际适用并严格通过；show→confide槽位控制8步全部通过；"
        + "意图不低于生产v2，不变量全通过。"
        + "未过不晋升；通过仅获得单独生产接线验收资格，不在研究运行中自动修改Game。";

    static MemoryChain[] FocusTransferChains()
    {
        var known = MemoryChains();
        var variants = new[]
        {
            new[] { "我把随身那串熏鱼送到小烬嘴边，让它现在吃掉。", "仍喂先前那一串；没有换成新的食物。",
                "手里不再拿吃的，我此刻蹲远些，耐心低声引小家伙过来。", "此刻请小烬随我走，和我保持同行。" },
            new[] { "这份随身契约原件现在交由梅伦收存核验。", "虽然尚未收回保管的原件，我现在仍试着向梅伦出示那一页。",
                "暂不请任何人帮忙；我眼下要做的是环顾周围。", "现在把护送小烬的打算告诉梅伦，请她同行相助。" },
            new[] { "点用我随身蓝晶路灯的照明效果，照亮这处墓穴。", "不换灯也不换地点，此刻再施展一次先前的照明。",
                "此刻先用眼睛查看周围，稍后才翻找；不要再次催动路灯。", "现在才仔细翻找墓穴各处，耐心搜寻。" },
            new[] { "请玛尔妲收下三枚银币，将摊上熏鱼卖给我。", "我刚买下的仍是那串鱼：现在再向玛尔妲购买它一次。",
                "不再掏钱也不翻找，我现在只是打量眼前摊位。", "事情先放一放，此刻留在原处等一阵，不做其他事。" }
        };
        var chains = known.Select((source, index) => new MemoryChain
        {
            Id = "new_" + source.Id, RepeatKind = source.Id, Inputs = variants[index],
            Verbs = source.Verbs.ToArray(), Targets = source.Targets.ToArray(), Items = source.Items.ToArray(),
            Setup = source.Setup, Goal = source.Goal
        }).ToList();
        chains.Add(new MemoryChain
        {
            Id = "role_switch", RepeatKind = "none",
            Inputs = new[] { "我现在只把契约展开给梅伦过目，看完仍由我收着。",
                "此刻不是交东西：我要向梅伦说明护送小烬的计划，并请她加入。",
                "先别继续托付、交付或搜索；目前我只看看这里的人和环境。", "我暂时不走，留在这里等候片刻。" },
            Verbs = new[] { "show", "confide", "observe", "wait" },
            Targets = new[] { "maren", "maren", "", "" }, Items = new[] { "pact_page", "", "", "" },
            Setup = s =>
            {
                known[1].Setup(s);
                s.World.Actors["maren"].Trust = .8f;
            },
            Goal = w => w.Items["pact_page"].Holder == "player" && w.Flags.ContainsKey("ally:maren")
        });
        chains.Add(new MemoryChain
        {
            Id = "idle_contrast", RepeatKind = "none",
            Inputs = new[] { "不是搜东西，我现在只观察广场。", "不买不卖，先在此地等一小会儿。",
                "之后才决定去哪；眼下仅留意周围，不开始搜寻。", "不再查看或购买，当前选择是原地等候。" },
            Verbs = new[] { "observe", "wait", "observe", "wait" },
            Targets = new[] { "", "", "", "" }, Items = new[] { "", "", "", "" }, Setup = _ => { },
            Goal = w => w.Coins == 12 && w.Items["smoked_fish"].Holder == "marta" && w.RollCounter == 0
        });
        return chains.ToArray();
    }

    internal static bool TransferRepeatPrefix(string kind, DsWorldSnapshot before, DsWorldSnapshot after,
        AiTrace trace, bool safeIntent)
    {
        if (!safeIntent || trace.FailureCode != null || trace.KernelCode != "committed" || trace.Receipt?.Applied != true
            || trace.Verdict.Kind != VerdictKind.Accepted) return false;
        var previous = before.Restore();
        var current = after.Restore();
        switch (kind)
        {
            case "feeding": return trace.Proposal.Str("verb") == "feed" && previous.Items["smoked_fish"].Holder == "player"
                && current.Items["smoked_fish"].Holder == "consumed";
            case "custody": return trace.Proposal.Str("verb") == "give" && previous.Items["pact_page"].Holder == "player"
                && current.Items["pact_page"].Holder == "maren" && current.Flags.ContainsKey("pact_custody:maren");
            case "illumination": return trace.Proposal.Str("verb") == "use"
                && !previous.Flags.ContainsKey("used:crafted_1:catacombs:")
                && current.Flags.ContainsKey("used:crafted_1:catacombs:") && current.Flags.ContainsKey("lit:catacombs");
            case "purchase_control": return trace.Proposal.Str("verb") == "buy"
                && previous.Items["smoked_fish"].Holder == "marta" && current.Items["smoked_fish"].Holder == "player"
                && previous.Coins - current.Coins == 3;
            default: return false;
        }
    }

    static void AnnotateFocusTransfer(Dictionary<string, object> row, MemoryChain chain, int step,
        DsWorldSnapshot before, DsWorldSnapshot after, AiTrace trace, bool safeIntent, bool repeatPrefixVerified)
    {
        row["repeat_kind"] = chain.RepeatKind;
        row["expected_target"] = chain.Targets[step];
        row["expected_item"] = chain.Items[step];
        row["slot_control"] = chain.Id == "role_switch";
        row["repeat_prefix_verified"] = repeatPrefixVerified;
        row["successful_intent"] = TransferSuccessfulIntent(trace, safeIntent);
        row["focused_effect_match"] = (bool)row["focused_effect_match"] && (bool)row["successful_intent"];
        row["repeat_probe_applicable"] = (bool)row["repeat_probe_applicable"] && repeatPrefixVerified;
        if (!(bool)row["repeat_probe_applicable"]) row["repeat_probe_passed"] = null;
        bool noCost = trace.Verdict.Kind == VerdictKind.Rejected && !trace.Verdict.Roll.HasValue && trace.Receipt?.TimeCost == 0;
        row["rejected_without_side_effects"] = !noCost || before.Json == after.Json
            && trace.Changes.Count == 0 && trace.Receipt?.Applied == false;
        row["invariants_passed"] = (bool)row["invariants_passed"] && (bool)row["rejected_without_side_effects"];
        if (chain.Id == "role_switch" && step == 0)
            row["focused_effect_match"] = (bool)row["successful_intent"]
                && trace.Verdict.Roll.HasValue && trace.Receipt?.TimeCost == 2
                && after.Restore().Items["pact_page"].Holder == "player";
    }

    internal static bool TransferSuccessfulIntent(AiTrace trace, bool safeIntent) => safeIntent
        && trace.FailureCode == null && trace.KernelCode == "committed";

    internal static void CompleteFocusTransferSummary(string dir, List<Dictionary<string, object>> rows)
    {
        string path = Path.Combine(dir, "summary.json");
        var summary = JObject.Parse(File.ReadAllText(path));
        summary["suite"] = FocusTransferSuite;
        summary["scope"] = "候选冻结后独立新措辞与两个新控制链；机制已知、两个固定新种子，不是普遍泛化或真人验收。";
        foreach (var arm in summary["arms"].OfType<JObject>())
        {
            var subset = rows.Where(r => (string)r["action_prompt_version"] == (string)arm["version"]).ToArray();
            arm["intent"] = subset.Count(r => (bool)r["successful_intent"]);
            var repeat = new[] { "custody", "purchase_control" }.Select(kind => new
            {
                kind,
                total = subset.Count(r => (string)r["repeat_kind"] == kind && (int)r["step"] == 2),
                applicable = subset.Count(r => (string)r["repeat_kind"] == kind && (bool)r["repeat_probe_applicable"]),
                passed = subset.Count(r => (string)r["repeat_kind"] == kind && r["repeat_probe_passed"] is bool ok && ok)
            }).ToArray();
            arm["repeatGates"] = JArray.FromObject(repeat);
            var totals = new Dictionary<string, int> { ["observe"] = 12, ["wait"] = 8, ["confide"] = 4, ["lure"] = 2 };
            bool exactProbes = arm["probes"].All(p => (int)p["total"] == totals[(string)p["verb"]]
                && (int)p["passed"] == (int)p["total"]);
            bool slots = subset.Where(r => (bool)r["slot_control"]).All(r => (bool)r["focused_effect_match"]);
            arm["slotControlPassed"] = slots && subset.Count(r => (bool)r["slot_control"]) == 8;
            arm["criticalGatePassed"] = subset.Length == 48 && exactProbes && (bool)arm["slotControlPassed"]
                && repeat.All(r => r.total == 2 && r.applicable == 2 && r.passed == 2)
                && subset.All(r => (bool)r["invariants_passed"] && r["provider_error"] == null);
        }
        var production = summary["arms"].Single(a => (string)a["version"] == DsActionPrompt.Current);
        var candidate = summary["arms"].Single(a => (string)a["version"] == DsActionPrompt.Focused);
        bool complete = (string)summary["status"] == "completed" && rows.Count == 96
            && (int)production["requested"] == 48 && (int)candidate["requested"] == 48
            && rows.All(r => (bool)r["invariants_passed"] && r["provider_error"] == null);
        summary["candidateEligible"] = complete && (bool)candidate["criticalGatePassed"]
            && (int)candidate["intent"] >= (int)production["intent"];
        summary["promotionPolicy"] = FocusTransferGate;
        File.WriteAllText(path, summary.ToString(Formatting.Indented));
    }
}
