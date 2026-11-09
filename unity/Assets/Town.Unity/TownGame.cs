using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.IO;
using Town.Core.Content;
using Town.Core.Knowledge;
using Town.Core.World;
using Town.Core.Runtime;
using Town.Core.Rules;
using UnityEngine;
using UnityEngine.AI;

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

        public TownRuntime Runtime { get; private set; }
        public HammerEventSetup.Session Session => Runtime?.Session;
        public bool Paused { get; set; }
        public float SpeedMultiplier { get; private set; } = 1f;
        /// <summary>对话等界面打开时为 true，阻止点地移动。</summary>
        public bool InputBlocked { get; set; }
        public string SystemMessage { get; private set; } = "";
        public PlayerClickMover Player { get; private set; }
        public TownCameraRig CameraRig { get; private set; }
        public string SaveDirectoryOverride { private get; set; }
        private TownSaveStore SaveStore => new TownSaveStore(Path.Combine(SaveDirectoryOverride ?? Path.Combine(Application.persistentDataPath, "unfinished-town"), "chapter7.json"));

        private readonly Dictionary<string, LocationMarker> _locations = new Dictionary<string, LocationMarker>();
        private readonly Dictionary<string, NpcAgent> _npcs = new Dictionary<string, NpcAgent>();
        private float _accum;
        private static Rect DebugRect => new Rect(10, 10, 470, Mathf.Min(560, Screen.height - 20));
        private bool _showDebug = false; // 真值面板会剧透，默认关闭，F1 打开
        private int _debugNpcIndex;

        private void Awake()
        {
            Instance = this;
            foreach (var l in FindObjectsByType<LocationMarker>(FindObjectsSortMode.None)) _locations[l.locationId] = l;
            foreach (var n in FindObjectsByType<NpcAgent>(FindObjectsSortMode.None)) _npcs[n.npcId] = n;
            Player = FindFirstObjectByType<PlayerClickMover>();
            if (Camera.main != null)
                CameraRig = Camera.main.GetComponent<TownCameraRig>() ?? Camera.main.gameObject.AddComponent<TownCameraRig>();
            StartSession(1);
            if (GetComponent<TownInteractionPanel>() == null) gameObject.AddComponent<TownInteractionPanel>();
        }

        private void StartSession(int gen)
        {
            Runtime?.Dispose();
            Runtime = new TownRuntime(gen);
            foreach (var npc in Session.World.Npcs.Values)
            {
                if (_npcs.TryGetValue(npc.Id, out var agent) && npc.LocationId != null && _locations.TryGetValue(npc.LocationId, out var loc))
                    agent.WarpTo(loc.StandPoint + NpcOffset(npc.Id), npc.LocationId);
            }
            if (Player != null && _locations.TryGetValue(Ids.Loc.Plaza, out var spawn))
            {
                Player.ResetPosition(spawn.StandPoint + Vector3.right * 1.5f);
                CameraRig?.Bind(Player.transform);
            }
            InputBlocked = false;
            Paused = false;
            _accum = 0;
            SpeedMultiplier = 1f;
            SystemMessage = "";
            Debug.Log($"[Town] 新局开始 SessionGen={Session.World.SessionGen} {Session.World.Now}");
        }

        /// <summary>重置本局：代际 +1，之前发出的模型请求全部作废。</summary>
        public void ResetSession() => StartSession(Session.World.SessionGen + 1);

        private void Update()
        {
            HandleDevKeys();
            SyncNpcs();
            if (Paused || Runtime.IsDialoguePending) return;

            _accum += Time.deltaTime * SpeedMultiplier;
            float secondsPerMinute = Mathf.Max(0.1f, realSecondsPerGameMinute);
            int minutes = Mathf.FloorToInt(_accum / secondsPerMinute);
            if (minutes > 0)
            {
                _accum -= minutes * secondsPerMinute;
                Runtime.Advance(minutes);
            }
        }

        /// <summary>把 Core 里居民的目的地同步给导航；到达与否以 Core 为准，这里只是表现。</summary>
        private void SyncNpcs()
        {
            bool stopped = Paused || Runtime.IsDialoguePending;
            foreach (var npc in Session.World.Npcs.Values)
            {
                if (!_npcs.TryGetValue(npc.Id, out var agent)) continue;
                string target = npc.TravelTo ?? npc.LocationId;
                if (target != null && _locations.TryGetValue(target, out var loc))
                    agent.Sync(target, loc.StandPoint + NpcOffset(npc.Id), SpeedMultiplier, stopped);
                agent.SetStatus(npc.Activity);
            }
        }

        private static Vector3 NpcOffset(string npcId)
        {
            switch (npcId)
            {
                case Ids.Npc.Amu: return new Vector3(-1.4f, 0, 0.4f);
                case Ids.Npc.Ahe: return new Vector3(1.4f, 0, 0.4f);
                default: return new Vector3(0, 0, 1.5f);
            }
        }

        private void HandleDevKeys()
        {
            if (Input.GetKeyDown(KeyCode.F1)) _showDebug = !_showDebug;
            if (Input.GetKeyDown(KeyCode.F2)) Paused = !Paused;
            if (Input.GetKeyDown(KeyCode.F3)) SpeedMultiplier = SpeedMultiplier >= 4f ? 1f : 4f;
            if (Input.GetKeyDown(KeyCode.F5)) ResetSession();
            if (Input.GetKeyDown(KeyCode.F6)) SaveSession();
            if (Input.GetKeyDown(KeyCode.F9)) LoadSession();
            if (Input.GetKeyDown(KeyCode.E) && !Paused && !InputBlocked && !Runtime.IsDialoguePending) InteractNearby();
            if (Input.GetKeyDown(KeyCode.Tab)) _debugNpcIndex = (_debugNpcIndex + 1) % Ids.Npc.All.Length;
        }

        public bool TryGetLocation(string id, out LocationMarker marker) => _locations.TryGetValue(id, out marker);
        public bool TryGetNpc(string id, out NpcAgent agent) => _npcs.TryGetValue(id, out agent);

        public bool CanInteract(string id) => Player != null && _npcs.TryGetValue(id, out var actor) &&
            Session.World.Npcs.TryGetValue(id, out var npc) && npc.LocationId != null &&
            npc.LocationId == Session.World.PlayerLocationId && Vector3.Distance(Player.transform.position, actor.transform.position) <= 5f;

        public CommandResult Execute(TownCommand command)
        {
            string target = command.NpcId;
            if (command.Kind == TownCommandKind.ReturnHammer || command.Kind == TownCommandKind.Promise) target = Ids.Npc.Amu;
            if (command.Kind == TownCommandKind.AskFinnToReturn || command.Kind == TownCommandKind.Apologize) target = Ids.Npc.Xiaochuan;
            if (target != null && !CanInteract(target)) return CommandResult.Reject("先走到这位居民身边，等他停下脚步。");
            return Runtime.Execute(command);
        }

        private async void InteractNearby()
        {
            switch (Session.World.PlayerLocationId)
            {
                case Ids.Loc.BellTower: SystemMessage = Execute(new TownCommand(Runtime.Rules.Story.HasBookmark ? TownCommandKind.ReadTablet : TownCommandKind.MeetKeeper)).Message; return;
                case Ids.Loc.Shelter: SystemMessage = Execute(new TownCommand(TownCommandKind.InspectShelter)).Message; return;
                case Ids.Loc.Well: SystemMessage = Execute(new TownCommand(TownCommandKind.InspectWell)).Message; return;
            }
            string id = _npcs.Keys.Where(CanInteract).OrderBy(n => Vector3.Distance(Player.transform.position, _npcs[n].transform.position)).FirstOrDefault();
            if (id == null) { SystemMessage = "附近没有可交谈的人，先走近一点。"; return; }
            var runtime = Runtime;
            var reply = await runtime.TalkAsync(id, Propositions.Took);
            if (this != null && Runtime == runtime) SystemMessage = Ids.Name(id) + "：" + reply.Reply;
        }

        private static SpatialBookmark Bookmark(Transform actor, string id = null) => new SpatialBookmark
        { ActorId = id, X = actor.position.x, Y = actor.position.y, Z = actor.position.z, Yaw = actor.eulerAngles.y };

        public bool SaveSession()
        {
            try
            {
                if (Player == null) throw new InvalidOperationException();
                var save = Runtime.CreateSave(Bookmark(Player.transform, Ids.Player));
                save.Residents = _npcs.OrderBy(n => n.Key).Select(n => Bookmark(n.Value.transform, n.Key)).ToArray();
                SaveStore.Write(save);
                SystemMessage = "书签已夹好。这一局保存在本机；读档不会重新调用模型。";
                return true;
            }
            catch (Exception) { SystemMessage = "存档未完成：请等待对话结束，并确认本机目录可写。旧存档没有被主动清除。"; return false; }
        }

        public bool LoadSession()
        {
            if (Runtime.IsDialoguePending) { SystemMessage = "请等待对话结束后读档。"; return false; }
            TownRuntime restored = null;
            try
            {
                restored = SaveStore.Read(Session.World.SessionGen + 1, out var save, out bool backup);
                if (Player == null || save.Position == null || save.Position.ActorId != Ids.Player || save.Residents == null ||
                    save.Residents.Any(p => !_npcs.ContainsKey(p.ActorId))) throw new InvalidDataException();
                // 先校验全部坐标；失败不替换当前局、不移动任何角色。
                foreach (var p in (save.Residents ?? Array.Empty<SpatialBookmark>()).Concat(new[] { save.Position }))
                    if (!NavMesh.SamplePosition(new Vector3(p.X, p.Y, p.Z), out _, 2f, NavMesh.AllAreas)) throw new InvalidDataException();
                Player.ResetPosition(new Vector3(save.Position.X, save.Position.Y, save.Position.Z));
                Player.transform.rotation = Quaternion.Euler(0, save.Position.Yaw, 0);
                foreach (var p in save.Residents ?? Array.Empty<SpatialBookmark>())
                {
                    var actor = _npcs[p.ActorId];
                    actor.WarpTo(new Vector3(p.X, p.Y, p.Z));
                    actor.transform.rotation = Quaternion.Euler(0, p.Yaw, 0);
                }
                // 场景前置条件检查完、角色定位完才交换核心运行时。
                var old = Runtime;
                Runtime = restored;
                restored = null;
                old.Dispose();
                _accum = 0; SpeedMultiplier = 1f; Paused = true; InputBlocked = false;
                CameraRig?.Bind(Player.transform);
                SystemMessage = backup ? "主存档损坏，已恢复上一份书签。当前暂停，按 F2 继续。" : "已回到书签夹住的那一刻。当前暂停，按 F2 继续。";
                return true;
            }
            catch (Exception) { restored?.Dispose(); SystemMessage = "没有可用的兼容存档，或坐标已不可达；当前局保持不变。"; return false; }
        }

        public void ReportPlayerPosition(Vector3 position)
        {
            string nearest = null;
            float distance = 3f;
            foreach (var marker in _locations.Values)
            {
                float d = Vector3.Distance(position, marker.StandPoint);
                if (d < distance) { distance = d; nearest = marker.locationId; }
            }
            Runtime.SetPlayerLocation(nearest);
        }

        public bool IsPointerOverPanel(Vector3 mousePosition)
        {
            var point = new Vector2(mousePosition.x, Screen.height - mousePosition.y);
            return (_showDebug && DebugRect.Contains(point)) || TownInteractionPanel.PanelRect.Contains(point);
        }

        private void OnDestroy()
        {
            Runtime?.Dispose();
            if (Instance == this) Instance = null;
        }

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
                sb.AppendLine($"  {item.DisplayName}: {(item.HolderId != null ? "在 " + Ids.Name(item.HolderId) + " 手上" : "在 " + Ids.Name(item.LocationId))}");
            sb.AppendLine($"  拱门修复: {w.GetFlag(Ids.Flag.BenchProgress)}%");
            sb.AppendLine();

            sb.AppendLine("【钟楼石板（真值面板，开发用）】");
            foreach (var r in Runtime.Rules.Bell.Read())
                sb.AppendLine($"  {Propositions.All[r.PropositionId].Text}: {r.Believers.Count}人/{r.Origins.Count}源{(r.Ready ? " ★" : "")}");
            sb.AppendLine();

            string npcId = Ids.Npc.All[_debugNpcIndex];
            sb.AppendLine($"【{w.Npcs[npcId].DisplayName}】在 {Ids.Name(w.Npcs[npcId].LocationId)}{(w.Npcs[npcId].IsTravelling ? " → " + Ids.Name(w.Npcs[npcId].TravelTo) : "")}，对你：{Town.Core.Social.RelationLevel.Name(Session.Relations.Get(npcId, Ids.Player))}");
            sb.AppendLine($"【{w.Npcs[npcId].DisplayName} 的认知】");
            var view = Session.Knowledge.ViewFor(npcId);
            foreach (var b in view.History.OrderBy(b => b.PropositionId))
            {
                string text = Propositions.All[b.PropositionId].Text;
                sb.AppendLine($"  {(b.IsActive ? "" : "（已纠正）")}{(b.Stance ? "信" : "不信")}「{text}」 来源:{SourceName(b)} 把握:{b.Confidence:0.0}");
            }

            GUI.Box(DebugRect, GUIContent.none);
            GUI.Label(new Rect(DebugRect.x + 10, DebugRect.y + 6, DebugRect.width - 20, DebugRect.height - 10), sb.ToString());
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
