using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Moonlight
{
    /// <summary>新场景的隔离执行核；无网络、无世界真值外发、无旧 v3 存档迁移。</summary>
    public sealed class MoonWorkshop
    {
        public const string Scenario = "moonlight-slice-1";
        static readonly double[][] LowerRoute = { new[] { -4d, 0d }, new[] { 0d, -3d }, new[] { 4d, 0d } };
        static readonly double[][] UpperRoute = { new[] { -4d, 0d }, new[] { 0d, 3d }, new[] { 4d, 0d } };
        readonly List<MoonRecord> _history = new List<MoonRecord>();
        readonly List<MoonRecord> _commands = new List<MoonRecord>();
        readonly int _generation;
        MoonRequest _pending;
        int _requestSequence;
        int _waypoint;
        double _lastX = -4, _lastZ;
        double _litSeconds;
        bool _lightVerified;
        bool _playerConfirmed;
        MoonCandidate _agreement;
        public int Generation => _generation;
        public int Version { get; private set; }
        public int FreeEnergy { get; private set; } = 2;
        public bool LightOn { get; private set; } = true;
        public bool HasPending => _pending != null;
        public MoonTaskState State { get; private set; }
        public MoonCandidate Agreement => _agreement?.Copy();
        public IReadOnlyList<MoonRecord> History => _history.Select(r => r.Copy()).ToArray();
        internal MoonRecord[] Commands => _commands.Select(r => r.Copy()).ToArray();
        public int Waypoint => _waypoint;
        public double LitSeconds => _litSeconds;
        public double[][] Route => (_agreement?.Method == LightMethod.SplitBeam ? UpperRoute : LowerRoute).Select(p => (double[])p.Clone()).ToArray();
        public MoonWorkshop(int generation = 1)
        { if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation)); _generation = generation; }

        public MoonRequest Begin(string playerInput)
        {
            if (_pending != null || State == MoonTaskState.Travelling || State == MoonTaskState.AwaitingConfirmation)
                throw new InvalidOperationException("已有请求或未结束的约定。");
            if (State != MoonTaskState.None) throw new InvalidOperationException("本次约定已结束，请重开试验。");
            if (string.IsNullOrWhiteSpace(playerInput) || playerInput.Length > 500) throw new ArgumentException("请输入不超过500字的方法。");
            // 先检查并写入有界录制，再占用请求槽；记录已满时不能留下半个 pending。
            Command("request", text: playerInput);
            _pending = new MoonRequest("moon-" + ++_requestSequence, _generation, Version, playerInput);
            Record("request", _pending.Id, playerInput);
            return _pending;
        }

        public MoonResult Receive(MoonRequest request, MoonCandidate candidate)
        {
            if (request == null || request.Generation != _generation) return No("generation", "候选属于另一局。");
            if (_pending == null || !ReferenceEquals(request, _pending)) return No("request", "请求已取消、已处理或不属于当前请求。");
            Command("receive", candidate: candidate?.Copy());
            _pending = null; // 无自动重试；失败样本同样有回执。
            if (request.Version != Version) return No("stale", "状态已变化，请按新状态重新提出办法。");
            if (candidate == null || !Enum.IsDefined(typeof(MoonDecision), candidate.Decision) ||
                string.IsNullOrWhiteSpace(candidate.Summary) || candidate.Summary.Length > 500 || candidate.Evidence == null || candidate.Evidence.Length > 8)
                return No("schema", "候选结构不完整。");
            if (candidate.Evidence.Any(e => !request.EvidenceIds.Contains(e))) return No("evidence", "引用不属于芬恩这次允许的观察。");
            Record("candidate", request.Id, candidate.Summary, candidate: candidate.Copy());
            if (candidate.Decision != MoonDecision.Propose)
                return Yes(candidate.Decision == MoonDecision.Clarify ? "clarify" : "decline", candidate.Summary);
            if (!Enum.IsDefined(typeof(LightMethod), candidate.Method)) return No("capability", "未提供这种魔法能力。");
            if (!candidate.ActorConsents || !candidate.RequiresContinuousLight) return No("consent", "缺少芬恩本人的同意或持续供光的安全条件。");
            if (candidate.Energy != 2 || FreeEnergy < candidate.Energy) return No("resource", "本切片仅有两份月焰，不能凭空增加输出或漏报耗材。");
            _agreement = candidate.Copy(); _playerConfirmed = false; _litSeconds = 0; _waypoint = 1;
            State = MoonTaskState.AwaitingConfirmation; Version++;
            return Yes("proposed", "芬恩同意了条件；尚未开始。请确认占用两份月焰。");
        }

        public MoonResult CancelRequest()
        {
            if (_pending == null) return No("request", "没有待处理请求。");
            Command("cancel_request");
            Record("cancel_request", _pending.Id, "取消请求；迟到候选不会执行。"); _pending = null;
            return Yes("cancelled", "已取消请求。");
        }

        public MoonResult Confirm()
        {
            if (State != MoonTaskState.AwaitingConfirmation || _agreement == null) return No("state", "没有等待确认的约定。");
            if (FreeEnergy < 2) return No("resource", "月焰已被占用。");
            Command("confirm");
            FreeEnergy -= 2; _playerConfirmed = true; _litSeconds = 0; State = MoonTaskState.Travelling; Version++;
            Record("confirm", null, "玩家确认；月焰预留，等待实际试亮与移动。");
            return Yes("committed", "约定已提交，实际到达才算完成。");
        }

        public void SetLight(bool on)
        {
            if (LightOn == on) return;
            Command("light", x: on ? 1 : 0);
            LightOn = on; _litSeconds = 0; Version++;
            Record("light", null, on ? "月光重新接通。" : "月光中断，行进暂停。", x: on ? 1 : 0);
        }

        /// <summary>只由引擎传入物理姿态；靠近终点、点击目的地或台词不能直接完成。</summary>
        public MoonResult Observe(double x, double z, double seconds, bool lightVerified = false)
        {
            if (!Finite(x) || !Finite(z) || !Finite(seconds) || seconds <= 0 || seconds > 0.5) return No("pose", "无效的姿态或采样间隔。");
            if (State != MoonTaskState.Travelling || !_playerConfirmed) return No("state", "没有执行中的约定。");
            double moved = Distance(x, z, _lastX, _lastZ);
            if ((!CanMove || !lightVerified) && moved > 0.06) return No("unlit_movement", "供光未验证，不能把移动写成履约。");
            // 超速/瞬移不能生成到达证明；本场景根移动速度上限2米/秒。
            if (Distance(x, z, _lastX, _lastZ) > 2 * seconds + 0.06) return No("movement", "移动超出连续采样边界，未采纳到达。");
            Command("pose", x: x, z: z, seconds: seconds, flag: lightVerified);
            _lastX = x; _lastZ = z;
            bool readyBefore = CanMove;
            _lightVerified = lightVerified;
            if (LightOn && lightVerified) _litSeconds = Math.Min(2, _litSeconds + seconds); else _litSeconds = 0;
            if (CanMove != readyBefore) Version++;
            Record("pose", null, "引擎位置采样", x: x, z: z, seconds: seconds);
            if (!CanMove) return Yes("waiting_light", "等待持续供光；分光法需稳定两秒。");
            var route = Route;
            if (_waypoint < route.Length && Distance(x, z, route[_waypoint][0], route[_waypoint][1]) <= 0.35)
            { _waypoint++; Version++; }
            if (_waypoint == route.Length)
            {
                State = MoonTaskState.Completed; FreeEnergy = 2; Version++;
                return Yes("completed", "芬恩真实经过约定路线并到达月台；月焰解除占用。");
            }
            return Yes("moving", "执行中；尚未到达。");
        }

        public bool CanMove => State == MoonTaskState.Travelling && LightOn && _lightVerified &&
            (_agreement.Method != LightMethod.SplitBeam || _litSeconds >= 2);

        public MoonResult Stop(bool failed = false)
        {
            if (State != MoonTaskState.Travelling && State != MoonTaskState.AwaitingConfirmation) return No("state", "没有可停止的约定。");
            Command("stop", x: failed ? 1 : 0);
            State = failed ? MoonTaskState.Failed : MoonTaskState.Cancelled; FreeEnergy = 2; Version++;
            Record("stop", null, failed ? "路径或执行失败；月焰解除占用。" : "约定撤回，停止行进；已走过的位置不会倒回。", x: failed ? 1 : 0);
            return Yes("stopped", "已停止；重开才能从起点重新试验。");
        }

        MoonResult Yes(string code, string message) { Record("receipt", null, message, code: code); return new MoonResult(true, code, message); }
        MoonResult No(string code, string message) { Record("rejected", null, message, code: code); return new MoonResult(false, code, message); }
        void Record(string kind, string id, string text, MoonCandidate candidate = null, double x = 0, double z = 0, double seconds = 0, string code = null)
        {
            if (_history.Count == 512) _history.RemoveAt(0);
            _history.Add(new MoonRecord { Kind = kind, RequestId = id, Text = text, Candidate = candidate, X = x, Z = z, Seconds = seconds, Code = code });
        }
        void Command(string kind, string text = null, MoonCandidate candidate = null, double x = 0, double z = 0, double seconds = 0, bool flag = false)
        {
            if (_commands.Count >= 10000) throw new InvalidOperationException("单局执行记录已满，请重开。");
            _commands.Add(new MoonRecord { Kind = kind, Text = text, Candidate = candidate, X = x, Z = z, Seconds = seconds, Flag = flag });
        }
        static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        static double Distance(double x, double z, double a, double b) => Math.Sqrt((x - a) * (x - a) + (z - b) * (z - b));
    }
}
