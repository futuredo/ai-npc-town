using System.Linq;
using Town.Core.Dragonsleep;
using UnityEngine;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class DragonsleepJourneyProbe
    {
        void SetupSocialFixture()
        {
            var w = _game.Session.World;
            w.RollCounter = 0; w.PlayerPlace = w.Whelp.Place = w.Actors["maren"].Place = "cathedral";
            w.Actors["maren"].Trust = 0.05f; w.Items["pact_page"].Holder = "player";
            w.Whelp.Following = true; w.Whelp.Hidden = true; w.Whelp.TrustPlayer = 0.6f; w.Whelp.Fear = 0.4f;
            w.Flags["whelp_hidden_by_player"] = w.ClockText;
            w.PlayerKnows.UnionWith(new[] { "b_maren_pact", "b_maren_page", "law:law_guards_dusk" });
            var anchor = _game.PlacesRoot.Find("Place_cathedral");
            Require(anchor != null, "社会夹具缺少大教堂锚点。");
            _game.Player.Teleport(anchor.position + new Vector3(-3, 0.2f, -3));
            Checkpoint("显式夹具：信任0.05、持有原件、大教堂、已遮盖跟随；不算新局获取证据");
        }

        void CheckSocialDraft(string kind)
        {
            var run = _game.ActivePlan; var w = _game.Session.World;
            Require(w.Items["pact_page"].Holder == "player" && !w.Flags.ContainsKey("pact_custody:maren"), "草稿提前转移原件。");
            Require(!w.Flags.ContainsKey("ally:maren"), "草稿提前建立盟友。");
            var steps = run.Plan.ActiveSteps(run.Plan.DefaultSelection());
            Require(steps.Count >= 2 && steps[1].Verb == "confide" && steps[1].Target == "maren", "草稿未保留明确托付。");
            Require(steps[0].Verb == (kind == "replan" ? "give" : "show") && steps[0].Item == "pact_page", "补救前缀错误。");
            if (kind != "replan")
            {
                Require(!w.Flags.ContainsKey("requested:pact_custody:maren"), "试运行泄露尚未实际提出的条件。");
                return;
            }
            string text = string.Join("\n", _root.Q("planPanel").Query<Label>().ToList().Select(l => l.text));
            Result.deliveryCostVisible = text.Contains("不能再向总管") || text.Contains("失去原件") || text.Contains("不再持有");
            Require(Result.deliveryCostVisible, "重规划确认前没有交付原件代价说明。");
            Require(Result.actualCooperationRejection, "没有实际拒绝却进入低信任补救。");
        }

        void CheckSocialStep()
        {
            var run = _game.ActivePlan; var w = _game.Session.World;
            if (!Result.actualCooperationRejection && run.LastReasonCode.Code == "confide_custody_required")
            {
                Require(run.Status == "needs_replan" && w.Flags.ContainsKey("requested:pact_custody:maren"), "实际拒绝未公开合作要求。");
                Require(run.Results.Any(r => r.Failure == DsPlanFailure.Precondition && r.Receipts.Any(t => t.Applied)), "拒绝没有真实回执。");
                Result.actualCooperationRejection = true;
            }
            if (w.Flags.ContainsKey("pact_custody:maren") && !Result.custodyDelivered)
            {
                Require(w.Items["pact_page"].Holder == "maren" && !w.Flags.ContainsKey("ally:maren"), "交付不应自动托付。");
                Require(run.Results.Last().Status == "success" && run.Results.Last().Receipts.Any(r => r.Applied), "交付没有提交成功。");
                Result.custodyDelivered = true; Checkpoint("重新确认后交付原件，尚未自动结盟");
            }
        }

        bool FinishSocialFixture()
        {
            var w = _game.Session.World; var run = _game.ActivePlan;
            if (!w.Flags.ContainsKey("ally:maren")) return false;
            Require(Result.custodyDelivered && Result.deliveryCostVisible && Result.actualCooperationRejection, "社会补救证据不完整。");
            Require(w.Items["pact_page"].Holder == "maren" && w.Actors["maren"].Trust < 0.5f, "合作不是原件保管路径。");
            Require(run.Replans.Count == 1 && run.Replans[0].Decision != "pending" && run.AcceptedRequestIds.Count == 2,
                "补救未经过第二次明确确认。");
            Require(run.NextIndex == 2 && run.Results.Last().Receipts.Any(r => r.Applied), "托付未单独提交。");
            Result.allyMaren = true; Result.finalHash = State(); Result.planCalls = run.PlanTraces.Count; Result.replans = run.Replans.Count;
            Result.successfulSteps = run.Results.Count(r => r.Status == "success"); Result.failedSteps = run.Results.Count(r => r.Status == "failed");
            Result.voidSteps = run.Results.Count(r => r.Status == "void"); Result.realExecutionFailure = true;
            Result.confideCalls = _game.Session.Traces.Count(t => t.Kind == AiTaskKind.Adjudicate && t.Proposal?.Str("verb") == "confide");
            Result.appliedReceipts = _game.Session.Traces.Count(t => t.Receipt?.Applied == true) +
                _game.Session.RuleEvents.Count(t => t.Receipt?.Applied == true);
            Checkpoint("再次托付才成为盟友；仅验证社会补救前缀，不冒充完整归巢"); Finish(null); return true;
        }
    }
}
