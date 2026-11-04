using System.Collections.Generic;
using System.Linq;
using System.Text;
using Town.Core.Content;
using Town.Core.Knowledge;
using Town.Core.World;
using UnityEngine;

namespace Town.Unity
{
    /// <summary>
    /// 场景入口：持有 Core 的一局状态，推进时钟，把世界状态同步到表现层。
    /// 规则：MonoBehaviour 只调用 Core 的接口，不自己改世界事实。
    /// </summary>
    public sealed class TownGame : MonoBehaviour
    {
        public static TownGame Instance { get; private set; }

        [Tooltip("现实多少秒 = 游戏 1 分钟")]
        public float realSecondsPerGameMinute = 2f;

        public HammerEventSetup.Session Session { get; private set; }
        public bool Paused { get; set; }
        public float SpeedMultiplier { get; private set; } = 1f;
        /// <summary>对话等界面打开时为 true，阻止点地移动。</summary>
        public bool InputBlocked { get; set; }

        private readonly Dictionary<string, LocationMarker> _locations = new Dictionary<string, LocationMarker>();
        private readonly Dictionary<string, NpcAgent> _npcs = new Dictionary<string, NpcAgent>();
        private float _accum;
        private bool _showDebug = true;
        private int _debugNpcIndex;

        private void Awake()
        {
            Instance = this;
            foreach (var l in FindObjectsByType<LocationMarker>(FindObjectsSortMode.None)) _locations[l.locationId] = l;
            foreach (var n in FindObjectsByType<NpcAgent>(FindObjectsSortMode.None)) _npcs[n.npcId] = n;
            StartSession(1);
        }

        private void StartSession(int gen)
        {
            Session = HammerEventSetup.Create(gen);
            foreach (var npc in Session.World.Npcs.Values)
            {
                if (_npcs.TryGetValue(npc.Id, out var agent) && _locations.TryGetValue(npc.LocationId, out var loc))
                    agent.WarpTo(loc.StandPoint);
            }
            _accum = 0;
            Debug.Log($"[Town] 新局开始 SessionGen={Session.World.SessionGen} {Session.World.Now}");
        }

        /// <summary>重置本局：代际 +1，之前发出的模型请求全部作废。</summary>
        public void ResetSession() => StartSession(Session.World.SessionGen + 1);

        private void Update()
        {
            HandleDevKeys();
            if (Paused) return;

            _accum += Time.deltaTime * SpeedMultiplier;
            int minutes = Mathf.FloorToInt(_accum / realSecondsPerGameMinute);
            if (minutes > 0)
            {
                _accum -= minutes * realSecondsPerGameMinute;
                Session.World.AdvanceTime(minutes);
            }
        }

        private void HandleDevKeys()
        {
            if (Input.GetKeyDown(KeyCode.F1)) _showDebug = !_showDebug;
            if (Input.GetKeyDown(KeyCode.F2)) Paused = !Paused;
            if (Input.GetKeyDown(KeyCode.F3)) SpeedMultiplier = SpeedMultiplier >= 4f ? 1f : 4f;
            if (Input.GetKeyDown(KeyCode.F5)) ResetSession();
            if (Input.GetKeyDown(KeyCode.Tab)) _debugNpcIndex = (_debugNpcIndex + 1) % Ids.Npc.All.Length;
        }

        public bool TryGetLocation(string id, out LocationMarker marker) => _locations.TryGetValue(id, out marker);
        public bool TryGetNpc(string id, out NpcAgent agent) => _npcs.TryGetValue(id, out agent);

        // —— 开发者调试面板（F1）。正式 UI 在 W3 用 UI Toolkit 重做。 ——
        private void OnGUI()
        {
            if (!_showDebug || Session == null) return;
            var w = Session.World;
            var sb = new StringBuilder();
            sb.AppendLine($"{w.Now}   局={w.SessionGen}  版本={w.Version}  {(Paused ? "已暂停" : $"x{SpeedMultiplier:0}")}");
            sb.AppendLine("F1 面板  F2 暂停  F3 加速  F5 重开  Tab 切换 NPC");
            sb.AppendLine();
            sb.AppendLine("【世界事实】");
            foreach (var item in w.Items.Values)
                sb.AppendLine($"  {item.DisplayName}: {(item.HolderId != null ? "在 " + item.HolderId + " 手上" : "在 " + item.LocationId)}（{item.Mark}）");
            sb.AppendLine($"  长凳进度: {w.GetFlag(Ids.Flag.BenchProgress)}%");
            sb.AppendLine();

            string npcId = Ids.Npc.All[_debugNpcIndex];
            sb.AppendLine($"【{w.Npcs[npcId].DisplayName} 的认知】");
            var view = Session.Knowledge.ViewFor(npcId);
            foreach (var b in view.History.OrderBy(b => b.PropositionId))
            {
                string text = Propositions.All[b.PropositionId].Text;
                sb.AppendLine($"  {(b.IsActive ? "" : "（已纠正）")}{(b.Stance ? "信" : "不信")}「{text}」 来源:{SourceName(b)} 把握:{b.Confidence:0.0}");
            }

            GUI.Box(new Rect(10, 10, 460, 330), GUIContent.none);
            GUI.Label(new Rect(20, 16, 440, 320), sb.ToString());
        }

        private static string SourceName(Belief b)
        {
            switch (b.Source)
            {
                case SourceType.Experienced: return "亲历";
                case SourceType.Witnessed: return "目击";
                case SourceType.ToldBy: return "听" + b.SourceActorId + "说";
                case SourceType.Inferred: return "推断";
                case SourceType.SawEvidence: return "证据";
                default: return b.Source.ToString();
            }
        }
    }
}
