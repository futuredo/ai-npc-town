using Town.Core.Dragonsleep;
using UnityEngine;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class DragonsleepJourneyProbe
    {
        void SetupLateRescueFixture()
        {
            var w = _game.Session.World;
            w.Day = 3; w.Phase = DayPhase.Dusk; w.PlayerPlace = "skull_hill";
            w.Whelp.Place = "castle"; w.Whelp.CapturedBy = "osric"; w.Whelp.Following = false;
            w.Whelp.Hidden = true; w.Whelp.TrustPlayer = .6f; w.Whelp.Fear = .2f;
            w.Items["pact_page"].Holder = "player";
            w.Flags["seen_by_guards"] = w.ClockText; w.Flags["ally:pip"] = w.ClockText;
            w.PlayerKnows.UnionWith(new[] { "b_maren_page", "law:law_guards_dusk" });
            var anchor = _game.PlacesRoot.Find("Place_dragon_skull");
            Require(anchor != null, "晚救援夹具缺少龙骨岗锚点。");
            _game.Player.Teleport(anchor.position + new Vector3(-3, .2f, -3));
            Checkpoint("显式夹具：最后黄昏剩四点，王城被捕，皮普已结盟；保留正常随机风险，不算完整新局");
        }
    }
}
