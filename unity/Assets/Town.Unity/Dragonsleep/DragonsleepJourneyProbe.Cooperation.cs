using System.Linq;
using Town.Core.Dragonsleep;
using UnityEngine;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class DragonsleepJourneyProbe
    {
        void SetupCooperationFixture()
        {
            var w = _game.Session.World;
            w.Phase = DayPhase.Noon; w.PlayerPlace = w.Whelp.Place = "docks";
            w.Whelp.Following = true; w.Whelp.Hidden = true; w.Whelp.TrustPlayer = 0.6f; w.Whelp.Fear = 0.4f;
            w.Flags["whelp_hidden_by_player"] = "第1天·清晨";
            w.Items["pact_page"].Holder = "player";
            w.PlayerKnows.UnionWith(new[] { "b_pip_whelp_place", "b_pip_fish", "law:law_whelp_fish", "b_maren_page", "b_maren_pact" });
            var anchor = _game.PlacesRoot.Find("Place_docks"); Require(anchor != null, "合作夹具缺少码头锚点。");
            _game.Player.Teleport(anchor.position + new Vector3(-3, 0.2f, -3));
            Checkpoint("显式夹具：首日正午码头、持原件、已遮盖跟随；不算从新局获取物品或信任");
        }

        void CheckCooperationActions()
        {
            string mode = Result.cooperationMode;
            var traces = _game.Session.Traces.Where(t => t.Kind == AiTaskKind.Adjudicate && t.Proposal != null).ToList();
            var confides = traces.Where(t => t.Proposal.Str("verb") == "confide").ToList();
            var gives = traces.Where(t => t.Proposal.Str("verb") == "give" && t.Proposal.Str("item") == "pact_page").ToList();
            Result.confideCalls = confides.Count; Result.pactGiveCalls = gives.Count;
            Require(confides.All(t => mode != "solo" && t.Proposal.Str("target") == mode), "实际托付违反 v2 条件。");
            Require(gives.All(t => mode == "maren" && t.Proposal.Str("target") == "maren"), "实际交付原件违反 v2 条件。");
            if (_game.ActivePlan != null)
                Require(_game.ActivePlan.Constraints.HasCooperationIntent && _game.ActivePlan.Constraints.RequiredHelper ==
                    (mode == "solo" ? null : mode), "当前计划或重规划丢失显式合作目标。");
        }
    }
}
