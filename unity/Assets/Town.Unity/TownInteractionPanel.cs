using System.Collections.Generic;
using System.Linq;
using Town.Core.Knowledge;
using Town.Core.Outcome;
using Town.Core.Rules;
using Town.Core.Social;
using Town.Core.World;
using UnityEngine;
using N = Town.Core.World.Ids.Npc;
using P = Town.Core.Knowledge.Propositions;

namespace Town.Unity
{
    /// <summary>
    /// 灰盒玩法面板：信使手账、传话、回音石板、钟响结算。只调用 Runtime / Rules 的公开命令。
    /// 正式 UI（UGUI + 奇幻皮肤）在素材到位后替换，逻辑不变。
    /// </summary>
    public sealed class TownInteractionPanel : MonoBehaviour
    {
        public static Rect PanelRect => new Rect(Mathf.Max(10, Screen.width - 430), 10, 420, Screen.height - 20);

        private static readonly (string prop, string label)[] Topics =
        {
            (P.Took, "符文锤是谁拿走的？"),
            (P.Borrowed, "是借的吗？"),
            (P.Returned, "锤子还回来了吗？"),
            (P.Location, "锤子现在在哪？"),
            (P.Stole, "芬恩是不是偷了锤子？"),
            (P.WellEvil, "井里有恶灵吗？"),
            (P.WellWisp, "井里是灯灵吗？"),
        };

        private TownGame _game;
        private string _message = "你是刚到回音镇的信使。日落时誓钟会响——被足够多人相信的事会变成真的。";
        private Vector2 _scroll;
        private int _generation;
        private string _selected;           // 手账里选中的条目（命题 ID）
        private List<BellReading> _board;   // 上次在钟楼看到的石板
        private string _boardTime;

        private void Awake() => _game = GetComponent<TownGame>();

        private void OnGUI()
        {
            if (_game == null || _game.Runtime == null) return;
            var rt = _game.Runtime;
            var rules = rt.Rules;
            var world = rt.Session.World;
            if (_generation != world.SessionGen)
            {
                _generation = world.SessionGen;
                _message = "新的一天。日落时誓钟会响。";
                _selected = null;
                _board = null;
            }

            GUILayout.BeginArea(PanelRect, GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            int left = HammerEventSetupMinutesLeft(world);
            GUILayout.Label($"<b>回音镇</b>  {world.Now.Hour:00}:{world.Now.Minute:00}  {(left > 0 ? $"距誓钟 {left / 60} 小时 {left % 60} 分" : "誓钟已响")}", Rich());
            GUILayout.Label("你在：" + Ids.Name(world.PlayerLocationId) + "    拱门修复：" + world.GetFlag(Ids.Flag.BenchProgress) + "%");
            GUILayout.Label(_message, Wrap());

            if (world.GetFlag(Ids.Flag.MarketStarted) == 1) DrawFinale(rules);

            GUI.enabled = !rt.IsDialoguePending;
            DrawPlace(rules, world);
            foreach (var npc in world.Npcs.Values.Where(n => n.LocationId != null && n.LocationId == world.PlayerLocationId))
                DrawNpc(rt, rules, npc);
            DrawNotebook(rt);
            DrawBoard(rules, world);

            GUILayout.Space(8);
            GUILayout.Label("开发：", Wrap());
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+30 分钟")) rt.Advance(30);
            if (GUILayout.Button("+60 分钟")) rt.Advance(60);
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            GUILayout.Label("最近发生");
            foreach (var e in rt.Journal.Entries.Reverse().Where(e => e.Kind != "world").Take(8))
                GUILayout.Label($"{e.GameTime.Substring(e.GameTime.Length - 5)} · {e.Detail}", Wrap());

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static int HammerEventSetupMinutesLeft(WorldState w) =>
            Town.Core.Content.HammerEventSetup.MarketTime.TotalMinutes - w.Now.TotalMinutes;

        // —— 地点 ——
        private void DrawPlace(HammerEventRules rules, WorldState world)
        {
            switch (world.PlayerLocationId)
            {
                case Ids.Loc.Shelter:
                    Header("旧哨塔石亭");
                    if (GUILayout.Button("查看箱子")) Run(r => r.InspectShelter());
                    if (GUILayout.Button("拿起符文锤")) Run(r => r.TakeHammer());
                    break;
                case Ids.Loc.Well:
                    Header("旧井");
                    if (GUILayout.Button("探头往井里看")) Run(r => r.InspectWell());
                    break;
                case Ids.Loc.BellTower:
                    Header("钟楼");
                    if (GUILayout.Button("读回音石板"))
                    {
                        _board = rules.Bell.Read().ToList();
                        _boardTime = $"{world.Now.Hour:00}:{world.Now.Minute:00}";
                        _message = "石板上的符文闪着微光：每条说法有几人相信、来自几个源头。";
                    }
                    break;
            }
        }

        // —— 居民 ——
        private void DrawNpc(Town.Core.Runtime.TownRuntime rt, HammerEventRules rules, NpcState npc)
        {
            Header($"{npc.DisplayName}（{RelationLevel.Name(rt.Session.Relations.Get(npc.Id, Ids.Player))}）· {npc.Activity}");
            foreach (var (prop, label) in Topics)
                if (GUILayout.Button("问：" + label)) Talk(npc.Id, prop);
            string secret = npc.Id == N.Xiaochuan ? P.WantsToLearn : npc.Id == N.Amu ? P.WantsApprentice : null;
            if (secret != null && GUILayout.Button("问：最近有什么心事？")) Talk(npc.Id, secret);

            if (npc.Id == N.Amu)
            {
                if (GUILayout.Button("出示：石亭里的符文锤")) Run(r => r.ShowEvidence(npc.Id));
                if (GUILayout.Button("把符文锤交给他")) Run(r => r.ReturnHammer());
                if (GUILayout.Button("承诺：日落前把锤子找回来")) Run(r => r.PromiseToBrunn());
            }
            if (npc.Id == N.Xiaochuan)
            {
                if (GUILayout.Button("请他亲手把锤子还给布伦")) Run(r => r.AskFinnToReturnHammer());
                if (GUILayout.Button("道歉")) Run(r => r.ApologizeToFinn());
            }
            if (npc.Id == N.Ahe && GUILayout.Button("出示：石亭里的符文锤")) Run(r => r.ShowEvidence(npc.Id));

            if (_selected != null)
            {
                GUILayout.Label($"把「{P.TextOf(_selected)}」告诉{npc.DisplayName}：", Wrap());
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("如实转述")) Run(r => r.Tell(npc.Id, _selected, TellMode.Faithful));
                if (GUILayout.Button("说成亲眼所见")) Run(r => r.Tell(npc.Id, _selected, TellMode.ClaimSeen));
                if (P.All[_selected].ExaggeratesTo != null && GUILayout.Button("添油加醋")) Run(r => r.Tell(npc.Id, _selected, TellMode.Exaggerate));
                GUILayout.EndHorizontal();
            }
        }

        // —— 信使手账 ——
        private void DrawNotebook(Town.Core.Runtime.TownRuntime rt)
        {
            Header("信使手账（点选一条，再对身边的人传话；不说就是保密）");
            var view = rt.Session.Knowledge.ViewFor(Ids.Player);
            var mine = view.ActiveBeliefs.Where(b => b.PropositionId != P.Promise).Select(b => b.PropositionId).Distinct()
                .OrderBy(id => id).Select(id => view.Current(id)).ToList();
            if (mine.Count == 0) GUILayout.Label("（还是空的：去问问居民，或者自己去看看）");
            foreach (var b in mine)
            {
                string mark = _selected == b.PropositionId ? "▶ " : "";
                string text = (b.Stance ? "" : "并非：") + P.TextOf(b.PropositionId);
                if (GUILayout.Button($"{mark}{text}  〔{SourceLabel(b)}〕", LeftButton()))
                    _selected = _selected == b.PropositionId ? null : b.PropositionId;
            }
        }

        // —— 回音石板 ——
        private void DrawBoard(HammerEventRules rules, WorldState world)
        {
            Header("回音石板" + (_board == null ? "（去钟楼读一读）" : $"（{_boardTime} 读到）"));
            if (_board == null) return;
            foreach (var r in _board)
                GUILayout.Label($"{(r.Ready ? "✦" : "·")} {P.TextOf(r.PropositionId)}：{r.Believers.Count} 人相信，{r.Origins.Count} 个源头", Wrap());
            GUILayout.Label("（需要 2 人相信、2 个源头。你是外乡人，你的信念不算数。）", Wrap());
        }

        // —— 钟响结算 ——
        private void DrawFinale(HammerEventRules rules)
        {
            Header("🔔 誓钟敲响了三声");
            foreach (var m in rules.Bell.Manifested)
                GUILayout.Label("✦ 显化：" + P.All[m.PropositionId].ManifestEffect, Wrap());
            if (rules.Bell.Manifested.Count == 0) GUILayout.Label("什么也没有显化。", Wrap());
            GUILayout.Label("<b>结局</b>", Rich());
            foreach (var e in FestivalOutcome.Endings(rules))
                GUILayout.Label($"{e.Title}：{e.Description}", Wrap());
            GUILayout.Label("<b>《回音镇灯节小报》</b>", Rich());
            foreach (var h in FestivalOutcome.Headlines(rules)) GUILayout.Label("· " + h, Wrap());
            GUILayout.Label("<b>流言溯源</b>", Rich());
            foreach (var line in FestivalOutcome.Trace(rules)) GUILayout.Label(line, Wrap());
        }

        private void Run(System.Func<HammerEventRules, CommandResult> command) => _message = _game.Runtime.Execute(command).Message;

        private async void Talk(string npcId, string topic)
        {
            var runtime = _game.Runtime;
            try
            {
                var reply = await runtime.TalkAsync(npcId, topic);
                if (this != null && _game != null && _game.Runtime == runtime)
                    _message = $"{Ids.Name(npcId)}：{(reply.Success ? reply.Reply : "（没听清你在说什么）")}";
            }
            catch (System.Exception) { if (this != null) _message = "对话暂不可用，请重新尝试。"; }
        }

        private static string SourceLabel(Belief b)
        {
            switch (b.Source)
            {
                case SourceType.SawEvidence: return "亲眼看到证据";
                case SourceType.Witnessed: return "亲眼看见";
                case SourceType.Experienced: return "亲身经历";
                case SourceType.Inferred: return "猜的";
                case SourceType.ToldBy:
                    string who = Ids.Name(b.SourceActorId);
                    return b.OriginId != null && b.OriginId != b.SourceActorId ? $"听{who}说（最早出自{Ids.Name(b.OriginId)}）" : $"听{who}说";
                default: return b.Source.ToString();
            }
        }

        private static void Header(string text)
        {
            GUILayout.Space(6);
            GUILayout.Label($"<b>{text}</b>", Rich());
        }

        private static GUIStyle _rich, _wrap, _left;
        private static GUIStyle Rich() => _rich ?? (_rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
        private static GUIStyle Wrap() => _wrap ?? (_wrap = new GUIStyle(GUI.skin.label) { wordWrap = true });
        private static GUIStyle LeftButton() => _left ?? (_left = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, wordWrap = true });
    }
}
