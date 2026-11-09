using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 多步计划：有序步骤，每步绑定一个现有动词（或移动/等待），并带有可选的关键取舍。
    /// 计划本身不改变世界；只有玩家确认后，每一步才单独经过执行器提交。
    /// </summary>
    public sealed class DsPlan
    {
        public const int MaxSteps = 14;
        public const int MaxChoices = 3;
        public static readonly string[] Ops = { "act", "go", "wait" };

        public string Goal = "";
        public string Summary = "";
        public readonly List<DsPlanChoice> Choices = new List<DsPlanChoice>();
        public readonly List<DsPlanStep> Steps = new List<DsPlanStep>();

        /// <summary>未指定的取舍取第一个选项。</summary>
        public Dictionary<string, string> DefaultSelection() =>
            Choices.ToDictionary(c => c.Id, c => c.Options[0].Id);

        public List<DsPlanStep> ActiveSteps(IDictionary<string, string> selection) =>
            Steps.Where(s => s.Applies(selection)).ToList();

        /// <summary>从模型提案解析；返回结构错误，null 表示通过。</summary>
        public static string TryParse(Dictionary<string, object> p, out DsPlan plan)
        {
            plan = null;
            if (p == null) return "计划不是对象";
            var keys = new HashSet<string> { "goal", "summary", "choices", "steps" };
            if (p.Keys.Any(k => !keys.Contains(k))) return "计划包含未声明的字段";
            string goal = p.Str("goal"), summary = p.Str("summary");
            if (goal == null || summary == null || goal.Length > 200 || summary.Length > 300) return "goal/summary 缺失或过长";
            if (!(p.TryGetValue("choices", out var cv) && cv is List<object> choices)) return "choices 必须是数组";
            if (!(p.TryGetValue("steps", out var sv) && sv is List<object> steps)) return "steps 必须是数组";
            if (steps.Count == 0) return "计划没有任何步骤";
            if (steps.Count > MaxSteps) return "步骤过多（最多 " + MaxSteps + " 步）";
            if (choices.Count > MaxChoices) return "取舍过多（最多 " + MaxChoices + " 个）";
            var result = new DsPlan { Goal = goal, Summary = summary };
            foreach (var c in choices)
            {
                if (!(c is Dictionary<string, object> cd)) return "choices 元素必须是对象";
                if (cd.Keys.Any(k => k != "id" && k != "question" && k != "options")) return "choice 包含未声明的字段";
                string id = cd.Str("id"), q = cd.Str("question");
                if (!IdOk(id) || q == null || q.Length > 120) return "choice 的 id/question 无效";
                if (!(cd.TryGetValue("options", out var ov) && ov is List<object> options) || options.Count < 2 || options.Count > 3)
                    return "每个取舍需要 2–3 个选项";
                var choice = new DsPlanChoice { Id = id, Question = q };
                foreach (var o in options)
                {
                    if (!(o is Dictionary<string, object> od) || od.Keys.Any(k => k != "id" && k != "label")) return "option 结构无效";
                    string oid = od.Str("id"), label = od.Str("label");
                    if (!IdOk(oid) || label == null || label.Length > 60) return "option 的 id/label 无效";
                    if (choice.Options.Any(x => x.Id == oid)) return "同一取舍里选项重复：" + oid;
                    choice.Options.Add(new DsPlanOption { Id = oid, Label = label });
                }
                if (result.Choices.Any(x => x.Id == id)) return "取舍 id 重复：" + id;
                result.Choices.Add(choice);
            }
            var stepKeys = new HashSet<string> { "id", "op", "verb", "target", "item", "place", "until", "laws", "cites", "when", "why" };
            foreach (var s in steps)
            {
                if (!(s is Dictionary<string, object> sd)) return "steps 元素必须是对象";
                if (sd.Keys.Any(k => !stepKeys.Contains(k))) return "step 包含未声明的字段";
                var step = new DsPlanStep
                {
                    Id = sd.Str("id"), Op = sd.Str("op"), Verb = sd.Str("verb") ?? "", Target = sd.Str("target") ?? "",
                    Item = sd.Str("item") ?? "", Place = sd.Str("place") ?? "", Until = sd.Str("until") ?? "",
                    When = sd.Str("when") ?? "", Why = sd.Str("why") ?? "",
                };
                var laws = sd.StrList("laws"); var cites = sd.StrList("cites");
                if (laws == null || cites == null || laws.Count > 6 || cites.Count > 8) return "step 的 laws/cites 无效";
                step.Laws.AddRange(laws); step.Cites.AddRange(cites);
                if (!IdOk(step.Id) || step.Op == null) return "step 缺少 id/op";
                if (new[] { step.Verb, step.Target, step.Item, step.Place, step.Until, step.When }.Any(x => x.Length > 64) || step.Why.Length > 120)
                    return "step 字段过长：" + step.Id;
                if (result.Steps.Any(x => x.Id == step.Id)) return "步骤 id 重复：" + step.Id;
                result.Steps.Add(step);
            }
            plan = result;
            return null;
        }

        static bool IdOk(string id) => !string.IsNullOrEmpty(id) && id.Length <= 32;

        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["goal"] = Goal, ["summary"] = Summary,
            ["choices"] = Choices.Select(c => (object)new Dictionary<string, object>
            {
                ["id"] = c.Id, ["question"] = c.Question,
                ["options"] = c.Options.Select(o => (object)new Dictionary<string, object> { ["id"] = o.Id, ["label"] = o.Label }).ToList()
            }).ToList(),
            ["steps"] = Steps.Select(s => (object)s.ToRecord()).ToList(),
        };

        /// <summary>给重规划上下文用的一行摘要。</summary>
        public string Brief() => string.Join("；", Steps.Select(s => s.Brief()));
    }

    public sealed class DsPlanChoice
    {
        public string Id;
        public string Question;
        public readonly List<DsPlanOption> Options = new List<DsPlanOption>();
    }

    public sealed class DsPlanOption
    {
        public string Id;
        public string Label;
    }

    public sealed class DsPlanStep
    {
        public string Id, Op, Verb = "", Target = "", Item = "", Place = "", Until = "", When = "", Why = "";
        public readonly List<string> Laws = new List<string>();
        public readonly List<string> Cites = new List<string>();

        public bool Applies(IDictionary<string, string> selection)
        {
            if (string.IsNullOrEmpty(When)) return true;
            int eq = When.IndexOf('=');
            if (eq <= 0) return false;
            return selection != null && selection.TryGetValue(When.Substring(0, eq), out var chosen) && chosen == When.Substring(eq + 1);
        }

        /// <summary>转成与语义判定完全相同格式的结构化提案；地点为执行时的玩家所在地。</summary>
        public Dictionary<string, object> ToProposal(string currentPlace) => new Dictionary<string, object>
        {
            ["verb"] = Verb, ["target"] = Target, ["item"] = Item, ["place"] = currentPlace,
            ["laws"] = Laws.Cast<object>().ToList(), ["difficulty"] = 0.5,
            ["narration"] = string.IsNullOrEmpty(Why) ? "" : "按计划：" + Why,
        };

        public string Brief()
        {
            string where = Place == "" ? "" : "@" + Place;
            switch (Op)
            {
                case "act": return Id + " " + Verb + (Target == "" ? "" : " " + Target) + (Item == "" ? "" : " " + Item)
                    + where + (When == "" ? "" : " [" + When + "]");
                case "wait": return Id + " wait" + where + " until " + Until + (When == "" ? "" : " [" + When + "]");
                default: return Id + " go" + where + (When == "" ? "" : " [" + When + "]");
            }
        }

        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["id"] = Id, ["op"] = Op, ["verb"] = Verb, ["target"] = Target, ["item"] = Item, ["place"] = Place,
            ["until"] = Until, ["laws"] = Laws.Cast<object>().ToList(), ["cites"] = Cites.Cast<object>().ToList(),
            ["when"] = When, ["why"] = Why,
        };
    }

    /// <summary>一个取舍组合下的试运行结果。</summary>
    public sealed class DsPlanBranch
    {
        public Dictionary<string, string> Selection = new Dictionary<string, string>();
        public bool Feasible;
        public string FailedStep;
        public string Reason = "";
        public DsReason ReasonCode;
        public bool ReachesGoal;
        public string EndClock = "";
        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["selection"] = Selection.ToDictionary(x => x.Key, x => (object)x.Value), ["feasible"] = Feasible,
            ["failed_step"] = FailedStep, ["reason"] = Reason, ["reaches_goal"] = ReachesGoal, ["end_clock"] = EndClock,
        };
    }

    /// <summary>计划的整体校验报告。Code 为 null 表示结构与引用通过（是否可行看 Branches）。</summary>
    public sealed class DsPlanReport
    {
        public DsPlan Plan;
        public string Code;
        public readonly List<string> Issues = new List<string>();
        /// <summary>引用了玩家未知事实/实体的次数（被拦截，绝不提交）。</summary>
        public readonly List<string> InvalidReferences = new List<string>();
        public readonly List<DsPlanBranch> Branches = new List<DsPlanBranch>();
        public DsPlanBranch Default => Branches.FirstOrDefault();
        public bool Executable => Code == null && Default != null && Default.Feasible;
        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["code"] = Code, ["executable"] = Executable, ["issues"] = Issues.Cast<object>().ToList(),
            ["invalid_references"] = InvalidReferences.Cast<object>().ToList(),
            ["branches"] = Branches.Select(b => (object)b.ToRecord()).ToList(), ["plan"] = Plan?.ToRecord(),
        };
    }

    /// <summary>玩家此刻掌握的信息：计划感知与引用校验共用，保证模型能引用的正好是它看得到的。</summary>
    public static class DsPlayerKnowledge
    {
        public static HashSet<string> Facts(DsWorld w)
        {
            var set = new HashSet<string>(w.PlayerKnows.Where(x => !x.StartsWith("law:")));
            foreach (var law in w.Laws.Keys.Where(w.KnowsLaw)) set.Add(law);
            set.Add("obs_whelp"); set.Add("obs_here"); set.Add("obs_deadline");
            return set;
        }

        /// <summary>玩家知道的物件：随身、眼前、公开出售、工坊常备材料，以及听说过下落的契约页。</summary>
        public static Dictionary<string, string> Items(DsWorld w)
        {
            var result = new Dictionary<string, string>();
            foreach (var i in w.Items.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                if (i.Holder == "consumed") continue;
                string where = null;
                if (i.Holder == "player") where = "随身";
                else if (i.Holder == "place:" + w.PlayerPlace) where = "就在眼前";
                else if (w.Actors.TryGetValue(i.Holder, out var seller) && i.Tags.Any(t => t.StartsWith("price:")))
                    where = seller.Name + "出售，" + i.Tags.First(t => t.StartsWith("price:")).Substring(6) + " 银币";
                else if (i.Holder == "place:workshop") where = "附魔工坊的常备材料";
                else if (i.Tags.Contains("pact") && w.PlayerKnows.Contains("b_maren_page")) where = "据说藏在墓穴圣物龛";
                if (where != null) result[i.Id] = i.Name + "（" + where + "）";
            }
            return result;
        }

        /// <summary>只有看得见小烬时才知道它的情绪（跟着你、或就在眼前）。</summary>
        public static string WhelpMood(DsWorld w)
        {
            var wh = w.Whelp;
            bool seen = wh.CapturedBy == null && wh.Place == w.PlayerPlace && (wh.Following || !wh.Hidden || w.Flags.ContainsKey("whelp_hidden_by_player"));
            if (!seen) return "看不到";
            return wh.Fear >= 0.85f ? "吓坏了" : wh.Fear >= 0.5f ? "警惕" : "平静";
        }

        /// <summary>玩家所知的雏龙状况；不泄露玩家没看到的抓捕或转移。</summary>
        public static string WhelpStatus(DsWorld w, out string code)
        {
            var wh = w.Whelp;
            if (wh.WithMother) { code = "with_mother"; return "小烬已经回到母亲身边"; }
            if (DsGuards.WhelpWithPlayer(w))
            {
                bool cloaked = wh.Hidden && w.Flags.ContainsKey("whelp_hidden_by_player");
                code = cloaked ? "following_cloaked" : "following";
                return cloaked ? "小烬裹在你的斗篷里跟着你" : "小烬跟在你脚边，没有遮盖";
            }
            if (wh.CapturedBy == "osric" && (w.Flags.ContainsKey("seen_by_guards") || w.PlayerPlace == "castle" || w.Flags.ContainsKey("rescue_failed")))
            { code = "caged_castle"; return "小烬被卫兵押进了王城的冷铁笼"; }
            if (w.Flags.TryGetValue("whelp_left_at", out var left) && wh.CapturedBy == null && wh.Place == left)
            { code = "left_at:" + left; return "你把小烬留在了" + w.Places[left]; }
            if (w.Flags.TryGetValue("whelp_left_at", out var left2))
            { code = "left_at:" + left2; return "你把小烬留在了" + w.Places[left2] + "（之后没再见过）"; }
            if (w.Flags.ContainsKey("whelp_fled")) { code = "fled_docks"; return "小烬受惊逃回了码头旧船屋"; }
            if (wh.Place == w.PlayerPlace && wh.CapturedBy == null && !wh.Hidden)
            { code = "visible_here"; return "小烬就在眼前，警惕地看着你"; }
            if (w.PlayerKnows.Contains("b_pip_whelp_place")) { code = "hidden_docks"; return "据皮普说，小烬藏在码头旧船屋里"; }
            code = "rumor";
            return "下落不明；镇上传言码头那边夜里冒烟";
        }
    }

    /// <summary>
    /// 计划整体校验：结构 → 引用（只允许玩家已知）→ 在私有试运行副本上用真实规则逐步执行。
    /// 试运行假设每次掷骰都成功、不被卫兵看见，只检查前置条件能否依次满足；不改动正式世界。
    /// </summary>
    public static class DsPlanValidator
    {
        // 兼容现有调用者；时刻协议只由 DsMoment 解析和格式化。
        public static bool TryClock(string text, out int day, out DayPhase phase)
        {
            bool parsed = DsMoment.TryParse(text, out var moment);
            day = moment.Day;
            phase = moment.Phase;
            return parsed;
        }

        public static int TimeIndex(int day, DayPhase phase) => new DsMoment(day, phase).Index;

        public static DsPlanReport Validate(DsWorldSnapshot before, DsPlan plan)
        {
            var report = new DsPlanReport { Plan = plan };
            var w = before.Restore();
            CheckStructure(w, plan, report);
            if (report.Code != null) return report;
            CheckReferences(w, plan, report);
            if (report.Code != null) return report;
            foreach (var selection in Selections(plan))
                report.Branches.Add(Simulate(before, plan.ActiveSteps(selection), selection));
            return report;
        }

        static void CheckStructure(DsWorld w, DsPlan plan, DsPlanReport report)
        {
            foreach (var s in plan.Steps)
            {
                if (!DsPlan.Ops.Contains(s.Op)) Issue(report, "schema_invalid", s.Id + "：未知操作 " + s.Op);
                else if (s.Op == "act" && (!ContextBuilder.AdjudicateVerbs.Contains(s.Verb) || s.Verb == "unsupported"))
                    Issue(report, "schema_invalid", s.Id + "：动词不在白名单 " + s.Verb);
                else if (s.Op == "go" && s.Place == "") Issue(report, "schema_invalid", s.Id + "：go 需要地点");
                else if (s.Op == "wait")
                {
                    if (!TryClock(s.Until, out _, out var ph)) Issue(report, "schema_invalid", s.Id + "：until 需要形如“第3天·黄昏”");
                    else if (ph == DayPhase.Night) Issue(report, "schema_invalid", s.Id + "：深夜由居民行动，不能作为等待终点");
                }
                if (s.When != "")
                {
                    int eq = s.When.IndexOf('=');
                    var choice = eq > 0 ? plan.Choices.FirstOrDefault(c => c.Id == s.When.Substring(0, eq)) : null;
                    if (choice == null || choice.Options.All(o => o.Id != s.When.Substring(eq + 1)))
                        Issue(report, "schema_invalid", s.Id + "：when 引用了不存在的取舍 " + s.When);
                }
            }
        }

        /// <summary>引用只能来自玩家已知：事实/规律、在世居民、已知物件、地点。卫兵岗位等未知真值无法被引用。</summary>
        static void CheckReferences(DsWorld w, DsPlan plan, DsPlanReport report)
        {
            var facts = DsPlayerKnowledge.Facts(w);
            var items = DsPlayerKnowledge.Items(w);
            foreach (var s in plan.Steps)
            {
                foreach (var c in s.Cites.Where(c => !facts.Contains(c))) Invalid(report, s.Id + " 引用了玩家不知道的事实：" + c);
                foreach (var l in s.Laws.Where(l => !w.KnowsLaw(l))) Invalid(report, s.Id + " 引用了玩家不知道的规律：" + l);
                if (s.Place != "" && !w.Places.ContainsKey(s.Place)) Invalid(report, s.Id + " 引用了不存在的地点：" + s.Place);
                if (s.Item != "" && !items.ContainsKey(s.Item)) Invalid(report, s.Id + " 引用了玩家不知道的物件：" + s.Item);
                if (s.Target != "" && s.Target != "whelp" && !w.Actors.ContainsKey(s.Target) && !items.ContainsKey(s.Target))
                    Invalid(report, s.Id + " 引用了未知对象：" + s.Target);
            }
        }

        static IEnumerable<Dictionary<string, string>> Selections(DsPlan plan)
        {
            var def = plan.DefaultSelection();
            yield return def;
            foreach (var c in plan.Choices)
                foreach (var o in c.Options.Skip(1))
                    yield return new Dictionary<string, string>(def) { [c.Id] = o.Id };
        }

        /// <summary>
        /// 在试运行副本上依次执行剩余步骤。供首次校验和执行中途“世界已变”后的复核共用。
        /// </summary>
        public static DsPlanBranch Simulate(DsWorldSnapshot from, IList<DsPlanStep> steps, Dictionary<string, string> selection = null)
        {
            var branch = new DsPlanBranch { Selection = selection ?? new Dictionary<string, string>() };
            var w = from.Restore();
            w.ForcedRoll = 0.9999; w.DryRun = true;
            int spent = from.Spent, budget = from.PhaseBudget;
            foreach (var step in steps)
            {
                string fail = SimulateStep(w, ref spent, budget, step, out var reasonCode);
                if (fail != null)
                {
                    branch.FailedStep = step.Id; branch.Reason = fail; branch.EndClock = w.ClockText;
                    branch.ReasonCode = reasonCode;
                    branch.ReachesGoal = w.Whelp.WithMother;
                    return branch;
                }
            }
            branch.Feasible = true;
            branch.ReachesGoal = w.Whelp.WithMother;
            branch.EndClock = w.ClockText;
            branch.Reason = branch.ReachesGoal ? "按计划可在终局时与母亲团聚（假设掷骰都成功）" : "步骤可依次执行，但计划结束时尚未团聚";
            return branch;
        }

        static string SimulateStep(DsWorld w, ref int spent, int budget, DsPlanStep step, out DsReason reasonCode)
        {
            reasonCode = new DsReason("deadline_passed");
            if (w.Whelp.WithMother)
            { reasonCode = new DsReason("goal_already_reached"); return "小烬已经与母亲团聚，后续步骤多余"; }
            if (w.FinalDawn) return "第三个黎明已至，来不及执行";
            // 与实际执行一致：时间推进到深夜时先过完这一夜（居民行动在试运行中视为未知、不模拟）。
            if (w.Phase == DayPhase.Night) { spent = 0; PassNight(w); if (w.FinalDawn) return w.Whelp.WithMother ? null : "第三个黎明已至，来不及执行"; }
            var t = new AiTrace();
            if (step.Place != "" && step.Place != w.PlayerPlace)
            {
                DsRules.EnterPlace(w, step.Place, t);
                if (t.FailureCode != null || t.Verdict.Kind != VerdictKind.Accepted)
                { reasonCode = t.Verdict.ReasonCode; return "前往" + Name(w, step.Place) + "失败：" + t.Verdict.Reason; }
            }
            switch (step.Op)
            {
                case "go": return null;
                case "act":
                {
                    var act = new AiTrace();
                    DsRules.Adjudicate(w, step.ToProposal(w.PlayerPlace), act);
                    reasonCode = act.Verdict.ReasonCode;
                    if (act.FailureCode != null) return act.Verdict.Reason;
                    if (act.Verdict.Kind == VerdictKind.Rejected || (act.Verdict.Kind == VerdictKind.Partial && step.Verb == "confide"))
                        return act.Verdict.Reason;
                    int cost = DsExecutor.TimeCost(AiTaskKind.Adjudicate, step.Verb, w, act);
                    DsExecutor.ApplyTime(w, ref spent, budget, cost, act);
                    return null;
                }
                default:
                {
                    DsMoment.TryParse(step.Until, out var until);
                    int target = until.Index;
                    reasonCode = new DsReason("wait_target_passed");
                    if (target < w.TimeIndex) return "等待的终点 " + step.Until + " 已经过去";
                    while (w.TimeIndex < target && !w.FinalDawn)
                    {
                        spent = 0;
                        bool night = w.Advance();
                        DsRules.PhaseStart(w, t);
                        if (night) PassNight(w);
                    }
                    return null;
                }
            }
        }

        static void PassNight(DsWorld w)
        {
            var t = new AiTrace();
            w.Advance();
            DsRules.PhaseStart(w, t);
            if (w.FinalDawn)
                DsRules.Direct(w, new Dictionary<string, object> { ["event"] = "mother_descends", ["intensity"] = 1.0, ["reason"] = "试运行" }, t);
        }

        static string Name(DsWorld w, string place) => w.Places.TryGetValue(place, out var n) ? n : place;

        static void Issue(DsPlanReport r, string code, string text) { r.Code = r.Code ?? code; r.Issues.Add(text); }
        static void Invalid(DsPlanReport r, string text) { r.Code = r.Code ?? "invalid_reference"; r.InvalidReferences.Add(text); r.Issues.Add(text); }
    }
}
