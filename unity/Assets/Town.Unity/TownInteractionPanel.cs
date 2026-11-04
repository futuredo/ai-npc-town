using System.Linq;
using Town.Core.Knowledge;
using Town.Core.Rules;
using Town.Core.World;
using UnityEngine;

namespace Town.Unity
{
    /// <summary>灰盒验收面板；正式 UGUI 对话、记事本和收尾页后续替换。</summary>
    public sealed class TownInteractionPanel : MonoBehaviour
    {
        public static Rect PanelRect => new Rect(Mathf.Max(10, Screen.width - 400), 10, 390, Mathf.Min(650, Screen.height - 20));
        private TownGame _game;
        private string _message = "点地面走到居民或雨棚旁，再使用下方交互。";
        private Vector2 _scroll;
        private int _generation;
        private void Awake() => _game = GetComponent<TownGame>();

        private void OnGUI()
        {
            if (_game == null || _game.Runtime == null) return;
            var runtime = _game.Runtime;
            var world = runtime.Session.World;
            if (_generation != world.SessionGen)
            {
                _generation = world.SessionGen;
                _message = "点地面走到居民或雨棚旁，再使用下方交互。";
            }
            GUILayout.BeginArea(PanelRect, GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("小镇交互 · 离线规则预览");
            GUILayout.Label("当前位置：" + (world.PlayerLocationId ?? "路上"));
            GUILayout.Label("维修：" + runtime.Rules.Repair.State + " / " + world.GetFlag(Ids.Flag.BenchProgress) + "%");
            GUILayout.Label(world.GetFlag(Ids.Flag.MarketStarted) == 0 ? "18:00 集市开始" :
                world.GetFlag(Ids.Flag.BenchProgress) == 100 ? "集市开始：长凳已修好" : "集市开始：使用临时座位");
            GUI.enabled = !runtime.IsDialoguePending;
            if (world.PlayerLocationId == Ids.Loc.Shelter)
            {
                if (GUILayout.Button("查看收纳箱")) Run(r => r.InspectShelter());
                if (GUILayout.Button("拿起锤子")) Run(r => r.TakeHammer());
            }
            foreach (var npc in world.Npcs.Values.Where(n => n.LocationId == world.PlayerLocationId))
            {
                GUILayout.Space(8);
                GUILayout.Label("与" + npc.DisplayName + "交谈");
                if (GUILayout.Button("问：小川拿走了锤子吗？")) Talk(npc.Id, Propositions.Took);
                if (GUILayout.Button("问：这是经同意借用的吗？")) Talk(npc.Id, Propositions.Borrowed);
                if (GUILayout.Button("问：锤子还了吗？")) Talk(npc.Id, Propositions.Returned);
                if (GUILayout.Button("问：锤子在哪里？")) Talk(npc.Id, Propositions.Location);
                if (GUILayout.Button("出示发现锤子的线索")) Run(r => r.ShowEvidence(npc.Id));
                if (npc.Id == Ids.Npc.Amu && GUILayout.Button("归还锤子")) Run(r => r.ReturnHammer());
            }
            GUILayout.Space(8);
            GUILayout.Label(_message);
            GUILayout.Space(8);
            if (GUILayout.Button("开发验证：推进 60 游戏分钟")) runtime.Advance(60);
            if (GUILayout.Button("导出本局调试日志")) ExportJournal();
            GUI.enabled = true;
            GUILayout.Label("最近事件");
            foreach (var entry in runtime.Journal.Entries.Reverse().Take(6))
                GUILayout.Label(entry.GameTime + " · " + entry.Detail);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void Run(System.Func<HammerEventRules, CommandResult> command) => _message = _game.Runtime.Execute(command).Message;

        private async void Talk(string npcId, string topic)
        {
            var runtime = _game.Runtime;
            try
            {
                var reply = await runtime.TalkAsync(npcId, topic);
                if (this != null && _game != null && _game.Runtime == runtime) _message = reply.Reply;
            }
            catch (System.Exception) { if (this != null) _message = "对话暂不可用，请重新尝试。"; }
        }

        private void ExportJournal()
        {
            try
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, "town-session-log.txt");
                System.IO.File.WriteAllLines(path, _game.Runtime.Journal.Entries.Select(e =>
                    $"{e.Sequence}\t{e.SessionGen}\t{e.StateVersion}\t{e.GameTime}\t{e.Kind}\t{e.Detail}"));
                _message = "日志已导出：" + path;
            }
            catch (System.Exception) { _message = "日志导出失败，请检查目录权限。"; }
        }
    }
}
