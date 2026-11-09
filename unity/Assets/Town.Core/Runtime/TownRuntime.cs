using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Town.Core.Brain;
using Town.Core.Content;
using Town.Core.Logging;
using Town.Core.Memory;
using Town.Core.Rules;
using Town.Core.Social;
using Town.Core.World;

namespace Town.Core.Runtime
{
    /// <summary>Unity 与无画面测试共享的组合入口。默认离线，不引用任何网络实现。</summary>
    public sealed class TownRuntime : IDisposable
    {
        private ILLMClient _dialogue;
        private readonly StalenessGuard _guard = new StalenessGuard();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private bool _disposed;
        private bool _batchingTime;
        private bool _unrecordedCommand;
        private readonly List<ReplayStep> _steps = new List<ReplayStep>();
        public HammerEventSetup.Session Session { get; }
        public HammerEventRules Rules { get; }
        public EventJournal Journal { get; } = new EventJournal();
        public DialogueMemory Memory { get; } = new DialogueMemory();
        public bool IsDialoguePending { get; private set; }

        public TownRuntime(int sessionGen = 1, ILLMClient dialogue = null)
        {
            Session = HammerEventSetup.Create(sessionGen);
            Rules = new HammerEventRules(Session);
            _dialogue = dialogue ?? new RuleDialogueClient();
            Session.World.Changed += OnWorldChanged;
            Rules.Noted += OnRuleNote;
            Journal.Append(Session.World, "session", "started");
            Rules.Story.Tick();
        }

        private void OnWorldChanged(WorldState world, string reason)
        {
            // 按分钟推进时，逐分钟的时间提交合并成一条，避免挤掉有用的日志。
            if (_batchingTime && reason.StartsWith("time+", StringComparison.Ordinal)) return;
            Journal.Append(world, "world", reason);
        }

        private void OnRuleNote(string kind, string detail) => Journal.Append(Session.World, kind, detail);

        /// <summary>逐分钟推进，保证日程到达、闲聊、截止在大步长跳时也按正确顺序发生。</summary>
        public void Advance(int minutes)
        {
            ThrowIfDisposed();
            if (IsDialoguePending || minutes <= 0) return;
            _batchingTime = true;
            try
            {
                for (int i = 0; i < minutes; i++)
                {
                    Session.World.AdvanceTime(1);
                    Rules.Tick();
                }
            }
            finally { _batchingTime = false; }
            Journal.Append(Session.World, "world", "time+" + minutes);
            _steps.Add(new ReplayStep { Kind = "advance", Minutes = minutes });
        }

        /// <summary>由导航到达检测回报；null 表示仍在路上。不能由点击目标直接调用到达。</summary>
        public void SetPlayerLocation(string locationId)
        {
            ThrowIfDisposed();
            if (locationId != null && !Ids.Loc.All.Contains(locationId)) throw new ArgumentException("未知地点。");
            if (Session.World.PlayerLocationId != locationId)
            {
                Session.World.Commit("player:location:" + (locationId ?? "travelling"), w => w.PlayerLocationId = locationId);
                _steps.Add(new ReplayStep { Kind = "location", Target = locationId });
            }
        }

        /// <summary>兼容旧测试/脚本。任意委托不可持久化；正式玩法请使用类型化命令。</summary>
        public CommandResult Execute(Func<HammerEventRules, CommandResult> command)
        {
            _unrecordedCommand = true;
            return ExecuteCore(command);
        }

        public CommandResult Execute(TownCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            var result = ExecuteCore(command.Apply);
            if (result.Success) _steps.Add(new ReplayStep { Kind = "command", Command = (int)command.Kind,
                Target = command.NpcId, Topic = command.PropositionId, Mode = (int)command.Mode });
            return result;
        }

        private CommandResult ExecuteCore(Func<HammerEventRules, CommandResult> command)
        {
            ThrowIfDisposed();
            if (IsDialoguePending) return CommandResult.Reject("请等待本轮对话结束。");
            var result = command(Rules);
            Journal.Append(Session.World, result.Success ? "command:ok" : "command:rejected", result.Message);
            return result;
        }

        public async Task<DialogueResult> TalkAsync(string npcId, string playerText, CancellationToken token = default)
        {
            ThrowIfDisposed();
            if (IsDialoguePending) return DialogueResult.Failure("dialogue_busy");
            if (!Session.World.Npcs.TryGetValue(npcId, out var npc) || npc.LocationId == null || npc.LocationId != Session.World.PlayerLocationId)
                return DialogueResult.Failure("npc_not_nearby");
            bool trusts = Session.Relations.Get(npcId, Ids.Player) == RelationLevel.Trust;
            // 按钮话题问到对方自己的秘密、对方又还不信任你时，规则直接让他含糊过去。
            if (!trusts && Knowledge.Propositions.All.TryGetValue(playerText, out var asked) && asked.SecretOwnerId == npcId)
            {
                var shy = new DialogueResult { Success = true, Reply = $"（{npc.DisplayName}犹豫了一下）……没什么，以后再说吧。" };
                Memory.Record(npcId, playerText, shy.Reply);
                RecordDialogue(npcId, playerText, shy);
                return shy;
            }
            var request = NpcDialogueRequest.From(Session.Knowledge.ViewFor(npcId), playerText, includeSecrets: trusts);
            var envelope = _guard.Issue(npcId, Session.World);
            IsDialoguePending = true;
            Journal.Append(Session.World, "dialogue:request", envelope.RequestId + ":" + npcId);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token))
            {
                try
                {
                    var result = await _dialogue.ReplyAsync(request, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    if (_disposed || _guard.Check(envelope, Session.World) != StaleReason.Fresh ||
                        envelope.StateVersion != Session.World.Version)
                        return DialogueResult.Failure("stale_response");
                    if (result == null) result = DialogueResult.Failure("empty_result");
                    if (result.Success)
                    {
                        var error = DialogueValidator.Validate(request, result.Reply, result.CitedBeliefs);
                        if (error != null) result = DialogueResult.Failure(error, result.Usage);
                    }
                    _guard.MarkApplied(envelope);
                    Memory.Record(npcId, playerText, result.Reply);
                    if (result.Success) Rules.Rumor.PlayerHeard(npcId, result.CitedBeliefs);
                    Journal.Append(Session.World, "dialogue:result", envelope.RequestId + ":" + (result.Success ? "ok" : result.ErrorCode));
                    RecordDialogue(npcId, playerText, result);
                    return result;
                }
                catch (OperationCanceledException) { return DialogueResult.Failure("cancelled"); }
                catch (Exception)
                {
                    // 不把可能含请求内容的异常写入公开日志。
                    if (!_disposed) Journal.Append(Session.World, "dialogue:result", envelope.RequestId + ":client_error");
                    return DialogueResult.Failure("client_error");
                }
                finally { IsDialoguePending = false; }
            }
        }

        private void RecordDialogue(string npc, string text, DialogueResult result) => _steps.Add(new ReplayStep
        {
            Kind = "dialogue", Target = npc, Topic = text, Reply = result.Reply, Success = result.Success,
            Error = result.ErrorCode, Citations = result.CitedBeliefs?.ToArray() ?? Array.Empty<string>()
        });

        public TownSave CreateSave(SpatialBookmark position = null)
        {
            ThrowIfDisposed();
            if (IsDialoguePending) throw new InvalidOperationException("请等待对话结束后存档。");
            if (_unrecordedCommand) throw new InvalidOperationException("本局包含未记录的开发命令，不能生成可靠存档。");
            var save = new TownSave { Steps = _steps.Select(s => s.Copy()).ToArray(), StateHash = RuntimeEvidence.Hash(this), Position = position };
            // 在写盘前实际重建一次，直接发现绕过 Runtime 的开发修改，不能生成假成功存档。
            using (TownSaveCodec.Restore(save, Session.World.SessionGen + 1)) { }
            return save;
        }

        internal void UseOfflineDialogue() => _dialogue = new RuleDialogueClient();

        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(TownRuntime)); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            Session.World.Changed -= OnWorldChanged;
            Rules.Noted -= OnRuleNote;
            _lifetime.Dispose();
        }
    }
}
