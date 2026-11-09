using System.Linq;
using Town.Core.Dragonsleep;
using UnityEngine;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class LoomLensHud
    {
        void ShowJournal()
        {
            var w = Game.Session.World;
            var card = Card(740);
            card.Add(L("日　志", 32, Ink, true));
            var list = new ScrollView { style = { alignSelf = Align.Stretch, marginTop = 12, maxHeight = 420 } };
            list.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            foreach (var o in DsObjectives.DayOne(w))
            {
                var title = Wrap(L((o.Done ? "✓ " : "○ ") + o.Text, 18, o.Done ? new Color(0.18f, 0.35f, 0.12f) : Ink));
                title.style.marginTop = 12; list.Add(title);
                if (!o.Done) { var hint = Wrap(L(o.Hint, 14, InkSoft)); hint.style.marginTop = 4; list.Add(hint); }
            }
            var clues = w.PlayerKnows.Where(k => !k.StartsWith("law:")).Select(w.Belief).Where(b => b != null).ToList();
            if (clues.Count > 0)
            {
                var h = L("听来的线索", 18, Ink, true); h.style.marginTop = 14; list.Add(h);
                foreach (var b in clues) list.Add(Wrap(L("· " + w.Actors[b.Owner].Name.Split('（')[0] + "：" + b.Text, 16, InkSoft)));
            }
            var laws = w.Laws.Values.Where(l => w.KnowsLaw(l.Id)).ToList();
            if (laws.Count > 0)
            {
                var h = L("已知的世界规律（可在【行】中利用）", 18, Ink, true); h.style.marginTop = 14; list.Add(h);
                foreach (var l in laws) list.Add(Wrap(L("· " + l.Text, 16, InkSoft)));
            }
            card.Add(list);
            card.Add(CardButton("合上", CloseModal));
        }

        void ShowMap()
        {
            var card = Card(660);
            card.Add(L("龙 眠 镇", 30, Ink, true));
            card.Add(L("快速前往（不消耗时间）", 14, InkSoft));
            var grid = Row();
            grid.style.flexWrap = UnityEngine.UIElements.Wrap.Wrap;
            grid.style.justifyContent = Justify.Center; grid.style.marginTop = 14;
            foreach (var p in Game.TravelTargets)
            {
                string place = p;
                string id = p == "castle_gate" ? "castle" : p == "dragon_skull" ? "skull_hill" : p;
                string name = Game.Session.World.Places.TryGetValue(id, out var n) ? n : p;
                var b = SkinButton(name, () => { Game.TravelTo(place); CloseModal(); }, Skin != null ? Skin.ButtonDark : null, 200, 52);
                b.style.marginLeft = 6; b.style.marginRight = 6; b.style.marginTop = 6; b.style.marginBottom = 6;
                grid.Add(b);
            }
            card.Add(grid);
            var row = Row();
            var end = CardButton("结束这一时段", () => { CloseModal(); Game.EndPhase(); }); end.style.marginRight = 12;
            row.Add(end);
            row.Add(CardButton("关闭", CloseModal));
            card.Add(row);
        }
    }
}
