using System;
using System.Collections.Generic;
using System.Linq;
using Town.Core.Dragonsleep;
using UnityEngine;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class LoomLensHud
    {
        TextField _planGoal;
        DropdownField _planDisclosure;
        int _planDisclosureIndex;
        Label _planStatus, _planBranch;
        Button _planPropose, _planConfirm, _planStep, _planReplan, _planCancel, _planAbandon;
        readonly Dictionary<string, string> _planSelection = new Dictionary<string, string>();
        readonly List<Button> _planOptions = new List<Button>();
        string _planDraft = "我想把小烬安全送回母亲身边";
        string _planRequest;
        bool _planPanelOpen;

        void BuildPlanEntry()
        {
            var entry = SmallLink("P  归巢计划", ShowPlan);
            entry.name = "openPlan"; entry.style.width = 140;
            _shortcuts.ElementAt(0).Insert(0, entry);
        }

        public void ShowPlan()
        {
            if (Game.Busy && !Game.PlanBusy) { Toast("请等待当前操作结束。"); return; }
            if (Game.Ending != null || (!Game.Busy && Game.Session.HasPendingCraft)) return;
            BuildPlanPanel();
        }

        public void RefreshPlan()
        {
            if (!_planPanelOpen || !ModalOpen || Game.Ending != null) return;
            if (Game.Busy) { RefreshPlanControls(); return; }
            BuildPlanPanel();
        }

        public void PresentPlanTrace(AiTrace trace)
        {
            // 计划面板已有自己的等待态；收尾时直接呈现七阶段最终事实，避免收尾停止逐颗揭示后停在第零阶段。
            AddRow(trace); Select(trace, animate: false);
        }

        public void FinishPlanOperation()
        {
            if (!isActiveAndEnabled || _stripLine == null) return;
            if (_reveal != null) { StopCoroutine(_reveal); _reveal = null; }
            _wasBusy = false;
            _stripLine.text = Game.PlanNotice;
            if (!_planPanelOpen && Game.Ending == null && !string.IsNullOrEmpty(Game.PlanNotice)) Toast(Game.PlanNotice);
        }

        void BuildPlanPanel()
        {
            var card = Card(980); card.name = "planPanel";
            _planPanelOpen = true;
            FlatPanel(card); Border(card, Edge, 1); card.style.alignItems = Align.Stretch;
            card.style.paddingLeft = 30; card.style.paddingRight = 30;
            var head = Row(); head.style.justifyContent = Justify.SpaceBetween; head.style.alignItems = Align.Center;
            head.Add(L("归巢计划", 28, Paper, true));
            var close = SmallLink("收起", CloseModal); close.name = "closePlan"; head.Add(close); card.Add(head);
            card.Add(Wrap(L("先写目标，再确认取舍。计划只是建议；每一步都由你单独执行。", 14, Muted)));
            _planGoal = new TextField { name = "planGoal", value = _planDraft, maxLength = 300, multiline = true };
            StyleInput(_planGoal); Border(_planGoal, Edge, 1);
            _planGoal.style.minHeight = 58; _planGoal.style.marginTop = 14;
            _planGoal.RegisterValueChangedCallback(e => { _planDraft = e.newValue; RefreshPlanControls(); });
            card.Add(_planGoal);
            _planDisclosure = new DropdownField("合作条件", new List<string> { "未限定", "不向居民托付（仅限制托付）", "只向梅伦托付（非必须同行）",
                "只向皮普托付（非必须帮忙）", "独行，不托付也不交原件", "必须请梅伦同行", "必须请皮普帮忙" },
                _planDisclosureIndex) { name = "planDisclosure" };
            _planDisclosure.style.marginTop = 10;
            _planDisclosure.RegisterValueChangedCallback(_ => _planDisclosureIndex = _planDisclosure.index);
            card.Add(_planDisclosure);
            card.Add(Wrap(L("条件随本次目标冻结；必须求助以实际答应为准，独行也禁止交出原件。" +
                "不阻止另行对话或居民流言，也不限制其他输入；改条件需重新提出目标。", 12, Muted)));
            var row = Row(); row.style.flexWrap = UnityEngine.UIElements.Wrap.Wrap;
            _planPropose = PlanButton("提出归巢目标", "proposePlan", () => { _ = Game.ProposePlanAsync(_planDraft, DraftConstraints()); }, true);
            _planCancel = PlanButton("取消规划请求", "cancelPlanRequest", Game.CancelPlanRequest);
            row.Add(_planPropose); row.Add(_planCancel); card.Add(row);
            _planStatus = Wrap(L("", 15, Gold)); _planStatus.name = "planStatus";
            _planStatus.style.marginTop = 12; card.Add(_planStatus);
            var list = new ScrollView { name = "planContents", style = { flexGrow = 1, minHeight = 100, maxHeight = 300, marginTop = 10 } };
            card.Add(list);
            _planOptions.Clear();
            // 忙时仅展示冻结在控件中的内容，不读取后台正在修改的计划集合。
            if (!Game.Busy) RenderPlanContents(list);
            _planBranch = Wrap(L("", 14, Muted)); _planBranch.name = "planBranch"; card.Add(_planBranch);
            var actions = Row(); actions.style.flexWrap = UnityEngine.UIElements.Wrap.Wrap;
            _planConfirm = PlanButton("确认取舍，暂不执行", "confirmPlan", () => Game.AcceptPlan(_planSelection), true);
            _planStep = PlanButton("执行下一步", "executePlanStep", () => { _ = Game.ExecutePlanStepAsync(); }, true);
            _planReplan = PlanButton("按当前情况重新规划", "replanPlan", () => { _ = Game.ReplanAsync(); });
            _planAbandon = PlanButton("放弃剩余计划", "abandonPlan", Game.AbandonPlan);
            actions.Add(_planConfirm); actions.Add(_planStep); actions.Add(_planReplan); actions.Add(_planAbandon); card.Add(actions);
            RefreshPlanControls();
        }

        Button PlanButton(string text, string name, Action click, bool primary = false)
        {
            var button = SkinButton(text, click, null, 200, 40); button.name = name;
            DressButton(button, primary); button.style.marginTop = 10; button.style.marginRight = 8;
            return button;
        }

        DsPlanConstraints DraftConstraints() => _planDisclosureIndex == 1 ? DsPlanConstraints.KeepPrivate :
            _planDisclosureIndex == 2 ? DsPlanConstraints.Only("maren") : _planDisclosureIndex == 3 ? DsPlanConstraints.Only("pip") :
            _planDisclosureIndex == 4 ? DsPlanConstraints.Solo : _planDisclosureIndex == 5 ? DsPlanConstraints.WithHelper("maren") :
            _planDisclosureIndex == 6 ? DsPlanConstraints.WithHelper("pip") :
            DsPlanConstraints.Unrestricted;

        void RenderPlanContents(VisualElement contents)
        {
            var run = Game.ActivePlan;
            var trace = run?.LatestPlanTrace;
            var plan = trace?.PlanReport?.Plan;
            if (trace?.RequestId != _planRequest)
            {
                _planRequest = trace?.RequestId; _planSelection.Clear();
                if (plan != null) foreach (var selection in plan.DefaultSelection()) _planSelection.Add(selection.Key, selection.Value);
            }
            if (plan == null)
            {
                contents.Add(Wrap(L("还没有可执行的计划。输入你想怎样送小烬归巢，再提出目标。", 16, Paper)));
                if (run != null) RenderPlanHistory(contents, run.Results);
                RenderRecentReceipts(contents);
                return;
            }
            contents.Add(Wrap(L(plan.Summary, 17, Paper)));
            if (run.Constraints.HasRestrictions)
            {
                var allowed = (List<object>)run.Constraints.ToRecord()["allowed_confidants"];
                string label = allowed.Count == 0 ? "不向居民托付" : "只向" + string.Join("、", allowed.Select(id => Game.ActorName((string)id))) + "托付";
                if (run.Constraints.HasCooperationIntent)
                    label = run.Constraints.RequiredHelper == null ? "独行：不托付，也不交出原件"
                        : "必须求得" + Game.ActorName(run.Constraints.RequiredHelper) + "实际帮助；不托付或交原件给其他人";
                contents.Add(Wrap(L("本次已冻结的条件：" + label + "；草稿里的条件不会改写此选择。", 13, Gold)));
            }
            foreach (var choice in plan.Choices)
            {
                var question = Wrap(L(choice.Question, 16, Gold, true)); question.style.marginTop = 14; contents.Add(question);
                var options = Row(); options.style.flexWrap = UnityEngine.UIElements.Wrap.Wrap;
                foreach (var option in choice.Options)
                {
                    string choiceId = choice.Id, optionId = option.Id;
                    var button = PlanButton(option.Label, "planChoice_" + choice.Id + "_" + option.Id, () =>
                    {
                        _planSelection[choiceId] = optionId; RefreshPlan();
                    }, _planSelection.TryGetValue(choice.Id, out var selected) && selected == option.Id);
                    button.tooltip = option.Label; _planOptions.Add(button); options.Add(button);
                }
                contents.Add(options);
            }
            bool currentPlan = ReferenceEquals(plan, run.Plan);
            var steps = currentPlan && (run.Status == "running" || run.Status == "needs_replan" || run.Status == "completed" || run.Status == "failed")
                ? run.ActiveSteps.ToList() : plan.ActiveSteps(_planSelection);
            var results = currentPlan ? run.Results.Skip(Game.PlanResultOffset).ToList() : new List<DsPlanStepResult>();
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i]; var result = results.LastOrDefault(r => r.StepId == step.Id);
                bool next = run.Status == "running" && i == run.NextIndex;
                var box = new VisualElement { name = "planStep_" + step.Id, style = { marginTop = 12, paddingLeft = 12,
                    paddingTop = 8, paddingBottom = 8, borderLeftWidth = next ? 3 : 1, borderLeftColor = next ? Gold : Edge } };
                string status = result == null ? (next ? "下一步" : "尚未执行") : result.Status == "success" ? "已完成"
                    : result.Status == "void" ? "已作废，未执行" : "执行失败";
                box.Add(Wrap(L((i + 1) + ". " + StepLabel(step) + "　[" + status + "]", 16, result?.Status == "void" ? Muted : Paper, next)));
                if (!string.IsNullOrEmpty(step.Why)) box.Add(Wrap(L(step.Why, 13, Muted)));
                if (step.Verb == "give" && step.Item == "pact_page" && step.Target == "maren")
                    box.Add(Wrap(L("交付后原件由梅伦保管，你不能再向总管出示它；同行仍需下一步托付确认。", 13, Gold)));
                if (result != null)
                {
                    if (result.Failure != DsPlanFailure.None) box.Add(Wrap(L(DsPlanRun.Label(result.Failure) + "：" + result.Reason, 14, Gold)));
                    if (!string.IsNullOrEmpty(result.Presented)) box.Add(Wrap(L(result.Presented, 14, Paper)));
                    box.Add(Wrap(L("实际回执 " + result.Receipts.Count + " 份；已提交 " + result.Receipts.Count(r => r?.Applied == true) + " 份", 12, Muted)));
                }
                contents.Add(box);
            }
            if (run.Replans.Count > 0)
                contents.Add(Wrap(L("已重新规划 " + run.Replans.Count + " 次。旧计划与历史回执保留在织言镜，新提案必须重新确认。", 13, Muted)));
            RenderPlanHistory(contents, run.Results.Take(Game.PlanResultOffset));
            RenderRecentReceipts(contents);
        }

        void RenderPlanHistory(VisualElement contents, IEnumerable<DsPlanStepResult> history)
        {
            foreach (var old in history)
            {
                contents.Add(Wrap(L("历史步骤：" + old.StepId + " · " + (old.Status == "void" ? "作废，未执行" : old.Status == "success" ? "已完成" : "失败")
                    + (old.Reason.Length > 0 ? "（" + old.Reason + "）" : ""), 13, Muted)));
                if (old.Failure != DsPlanFailure.None) contents.Add(Wrap(L(DsPlanRun.Label(old.Failure) + "：" + old.Reason, 13, Gold)));
                contents.Add(Wrap(L("实际回执 " + old.Receipts.Count + " 份；已提交 " + old.Receipts.Count(r => r?.Applied == true) + " 份", 12, Muted)));
            }
        }

        void RenderRecentReceipts(VisualElement contents)
        {
            if (Game.ActivePlan?.Status != "abandoned") return;
            foreach (var trace in Game.Session.Traces.Where(t => t.Kind != AiTaskKind.Plan).Reverse().Take(6).Reverse())
                contents.Add(Wrap(L("最近实际调用：" + trace.ActorName + " · " + (trace.Receipt?.Applied == true ? "已提交" : "未提交")
                    + " · " + trace.Presented, 13, Muted)));
        }

        DsPlanBranch SelectedBranch()
        {
            return Game.ActivePlan?.LatestPlanTrace?.PlanReport?.Branches.FirstOrDefault(b => b.Selection.Count == _planSelection.Count &&
                b.Selection.All(pair => _planSelection.TryGetValue(pair.Key, out var value) && value == pair.Value));
        }

        public void RefreshPlanControls()
        {
            if (!_planPanelOpen || _planStatus == null) return;
            bool busy = Game.Busy, alive = Game.Ending == null;
            _planPropose.SetEnabled(!busy && alive && !string.IsNullOrWhiteSpace(_planDraft));
            _planGoal.SetEnabled(!busy && alive);
            _planDisclosure.SetEnabled(!busy && alive);
            _planCancel.style.display = Game.CanCancelPlan ? DisplayStyle.Flex : DisplayStyle.None;
            _planCancel.SetEnabled(Game.CanCancelPlan && !Game.PlanCancellationRequested);
            if (busy)
            {
                _planStatus.text = Game.PlanCancellationRequested ? "取消请求已发出，仍在等待提供方结束；不会接纳迟到提案。"
                    : Game.PlanOperation == "step" ? "正在执行这一步；期间可能推进时间，已提交的后果不会撤销。"
                    : "正在规划，尚未执行任何提案步骤……";
                _planConfirm.SetEnabled(false); _planStep.SetEnabled(false); _planReplan.SetEnabled(false); _planAbandon.SetEnabled(false);
                foreach (var option in _planOptions) option.SetEnabled(false);
                return;
            }
            var run = Game.ActivePlan; string status = run?.Status;
            var branch = SelectedBranch();
            string constraintIssue = run?.Constraints.Check(run.LatestPlanTrace?.PlanReport?.Plan, _planSelection,
                run.Constraints.CooperationAchieved(Game.Session.World));
            bool confirm = status == "awaiting_confirmation" && constraintIssue == null && branch?.Feasible == true &&
                (!run.RequireHomecoming || branch.ReachesGoal);
            _planConfirm.style.display = status == "awaiting_confirmation" ? DisplayStyle.Flex : DisplayStyle.None;
            _planConfirm.SetEnabled(alive && confirm);
            _planStep.style.display = status == "running" ? DisplayStyle.Flex : DisplayStyle.None;
            _planStep.SetEnabled(alive && status == "running");
            _planReplan.SetEnabled(alive && run?.LatestPlanTrace?.Provider?.Error == null &&
                (status == "needs_replan" || (status == "planning" && run.LatestPlanTrace != null)));
            _planAbandon.SetEnabled(alive && run != null && status != "abandoned" && status != "completed" && status != "failed");
            foreach (var option in _planOptions) option.SetEnabled(alive && status == "awaiting_confirmation");
            _planBranch.text = status == "awaiting_confirmation" ? constraintIssue ?? (branch == null ? "没有对应的可执行分支，请重新提出目标。"
                : !branch.Feasible || (run.RequireHomecoming && !branch.ReachesGoal) ? "此取舍无法完成归巢：" + branch.Reason + "。请换选，或重新提出目标。"
                : "此取舍通过条件性试运行，不保证掷骰成功或居民愿意合作。确认不耗时，执行仍逐步进行。") : "";
            _planStatus.text = StateLabel(status) + (string.IsNullOrEmpty(Game.PlanNotice) ? "" : "\n" + Game.PlanNotice);
        }

        static string StateLabel(string status)
        {
            switch (status)
            {
                case "awaiting_confirmation": return "草稿已到，等待你确认取舍。";
                case "running": return "计划已确认，等待执行下一步。";
                case "needs_replan": return "情况有变：剩余步骤已停止，必须重新规划并确认。";
                case "completed": return "本次归巢与合作目标已达成。可以收起计划查看结局。";
                case "failed": return "期限已过，计划结束。";
                case "abandoned": return "计划已放弃。";
                case "planning": return "提案未通过校验；不会自动重试或执行。";
                default: return "写下你的归巢目标。";
            }
        }

        string StepLabel(DsPlanStep step)
        {
            string place = Game.Session.World.Places.TryGetValue(step.Place ?? "", out var p) ? p : step.Place;
            if (step.Op == "go") return "前往" + place;
            if (step.Op == "wait") return "在" + place + "等到" + step.Until;
            string target = step.Target == "whelp" ? "小烬" : Game.ActorName(step.Target);
            string item = Game.Session.World.Items.TryGetValue(step.Item ?? "", out var it) ? it.Name : step.Item;
            switch (step.Verb)
            {
                case "observe": return "在" + place + "观察" + target;
                case "buy": return "在" + place + "购买" + item;
                case "lure": return "用" + item + "安抚" + target;
                case "take": return "取出" + item;
                case "hide": return "在" + place + "藏好" + target;
                case "lead": return "用" + item + "带上" + target;
                case "confide": return "向" + target + "倾诉";
                case "show": return "向" + target + "出示" + item;
                case "give": return "把" + item + "交付给" + target;
                case "rescue": return "从城堡救回小烬";
                case "release": return "放飞小烬";
                default: return step.Why.Length > 0 ? step.Why : "在" + place + "尝试行动";
            }
        }
    }
}
