using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Town.Core.Brain;
using Town.Core.Content;
using Town.Core.Logging;
using Town.Core.Memory;
using Town.Core.Rules;
using Town.Core.World;

namespace Town.Core.Runtime
{
    /// <summary>Unity 与无画面测试共享的组合入口。默认离线，不引用任何网络实现。</summary>
    public sealed class TownRuntime : IDisposable
    {
        private readonly ILLMClient _dialogue;
        private readonly StalenessGuard _guard = new StalenessGuard();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private bool _disposed;
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
            Journal.Append(Session.World, "session", "started");
        }

        private void OnWorldChanged(WorldState world, string reason) => Journal.Append(world, "world", reason);

        public void Advance(int minutes)
        {
            ThrowIfDisposed();
            if (IsDialoguePending) return;
            Session.World.AdvanceTime(minutes);
            Rules.Tick();
        }

        /// <summary>由导航到达检测回报；null 表示仍在路上。不能由点击目标直接调用到达。</summary>
        public void SetPlayerLocation(string locationId)
        {
            ThrowIfDisposed();
            if (locationId != null && !Ids.Loc.All.Contains(locationId)) throw new ArgumentException("未知地点。");
            if (Session.World.PlayerLocationId != locationId)
                Session.World.Commit("player:location:" + (locationId ?? "travelling"), w => w.PlayerLocationId = locationId);
        }

        public CommandResult Execute(Func<HammerEventRules, CommandResult> command)
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
            if (!Session.World.Npcs.TryGetValue(npcId, out var npc) || npc.LocationId != Session.World.PlayerLocationId)
                return DialogueResult.Failure("npc_not_nearby");
            var request = NpcDialogueRequest.From(Session.Knowledge.ViewFor(npcId), playerText);
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
                    Journal.Append(Session.World, "dialogue:result", envelope.RequestId + ":" + (result.Success ? "ok" : result.ErrorCode));
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

        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(TownRuntime)); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            Session.World.Changed -= OnWorldChanged;
            _lifetime.Dispose();
        }
    }
}
