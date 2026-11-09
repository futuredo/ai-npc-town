using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Town.Core.Brain;
using Town.Core.Social;
using Town.Core.World;

namespace Town.Core.Runtime
{
    [DataContract]
    public sealed class ReplayStep
    {
        [DataMember] public string Kind;
        [DataMember] public int Minutes;
        [DataMember] public int Command;
        [DataMember] public string Target;
        [DataMember] public string Topic;
        [DataMember] public int Mode;
        [DataMember] public bool Success;
        [DataMember] public string Reply;
        [DataMember] public string Error;
        [DataMember] public string[] Citations;
        internal ReplayStep Copy() { var s = (ReplayStep)MemberwiseClone(); s.Citations = Citations?.ToArray(); return s; }
    }

    [DataContract]
    public sealed class SpatialBookmark
    {
        [DataMember] public string ActorId;
        [DataMember] public float X;
        [DataMember] public float Y;
        [DataMember] public float Z;
        [DataMember] public float Yaw;
    }

    [DataContract]
    public sealed class TownSave
    {
        // 回放受规则版本约束。改变规则必须升级版本，宁可明确拒绝也不悄悄改写一局。
        public const int CurrentSchema = 1;
        public const string CurrentRules = "unfinished-town-v3.1";
        [DataMember] public int Schema = CurrentSchema;
        [DataMember] public string Rules = CurrentRules;
        [DataMember] public ReplayStep[] Steps;
        [DataMember] public string StateHash;
        [DataMember] public SpatialBookmark Position;
        [DataMember] public SpatialBookmark[] Residents;
    }

    public static class TownSaveCodec
    {
        public const int MaxBytes = 2 * 1024 * 1024;
        public const int MaxSteps = 20000;
        public const int MaxGameMinutes = 1440;
        [DataContract]
        private sealed class Envelope
        {
            [DataMember] public byte[] Payload;
            [DataMember] public string Sha256;
        }

        internal static byte[] Serialize<T>(T data)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, data);
                return stream.ToArray();
            }
        }
        private static T Deserialize<T>(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        public static byte[] Encode(TownSave save)
        {
            Validate(save);
            byte[] payload = Serialize(save);
            byte[] result = Serialize(new Envelope { Payload = payload, Sha256 = Hash(payload) });
            if (result.Length > MaxBytes) throw new InvalidDataException("存档超过 2 MiB 上限。");
            return result;
        }

        public static TownSave Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("存档大小无效。");
            try
            {
                var envelope = Deserialize<Envelope>(bytes);
                if (envelope?.Payload == null || Hash(envelope.Payload) != envelope.Sha256)
                    throw new InvalidDataException("存档完整性校验失败。");
                var save = Deserialize<TownSave>(envelope.Payload);
                Validate(save);
                return save;
            }
            catch (SerializationException e) { throw new InvalidDataException("存档格式无效。", e); }
        }

        private static void Validate(TownSave save)
        {
            if (save == null || save.Schema != TownSave.CurrentSchema || save.Rules != TownSave.CurrentRules)
                throw new InvalidDataException("存档版本与当前规则不兼容。");
            if (save.Steps == null || save.Steps.Length > MaxSteps || save.StateHash == null || save.StateHash.Length != 64)
                throw new InvalidDataException("存档记录无效。");
            long minutes = 0;
            foreach (var s in save.Steps)
            {
                if (s == null || (s.Kind != "advance" && s.Kind != "location" && s.Kind != "command" && s.Kind != "dialogue"))
                    throw new InvalidDataException("存档包含未知操作。");
                if (s.Kind == "advance" && (s.Minutes <= 0 || (minutes += s.Minutes) > MaxGameMinutes))
                    throw new InvalidDataException("回放时间超过单章上限。");
                if (s.Kind == "location" && s.Target != null && !Ids.Loc.All.Contains(s.Target))
                    throw new InvalidDataException("存档包含未知地点。");
                if (s.Kind == "command" && (!Enum.IsDefined(typeof(TownCommandKind), s.Command) ||
                    !Enum.IsDefined(typeof(TellMode), s.Mode))) throw new InvalidDataException("存档包含未知命令。");
                if ((s.Kind == "dialogue" || (s.Kind == "command" &&
                    (s.Command == (int)TownCommandKind.ShowEvidence || s.Command == (int)TownCommandKind.Tell))) &&
                    !Ids.Npc.All.Contains(s.Target)) throw new InvalidDataException("存档包含未知居民。");
                if (s.Kind == "command" && s.Command == (int)TownCommandKind.Tell &&
                    (s.Topic == null || !Knowledge.Propositions.All.ContainsKey(s.Topic))) throw new InvalidDataException("存档包含未知说法。");
                if (s.Kind == "dialogue" && (string.IsNullOrWhiteSpace(s.Topic) || s.Topic.Length > 500 ||
                    string.IsNullOrWhiteSpace(s.Reply) || s.Reply.Length > 500 || s.Citations == null || s.Citations.Length > 24))
                    throw new InvalidDataException("存档对话无效。");
            }
            if (save.Residents != null && (save.Residents.Length != Ids.Npc.All.Length ||
                !save.Residents.Select(p => p?.ActorId).OrderBy(id => id).SequenceEqual(Ids.Npc.All.OrderBy(id => id))))
                throw new InvalidDataException("存档居民坐标无效。");
            foreach (var p in (save.Residents ?? Array.Empty<SpatialBookmark>()).Concat(new[] { save.Position }))
                if (p != null && new[] { p.X, p.Y, p.Z, p.Yaw }.Any(v => float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v) > 10000))
                    throw new InvalidDataException("存档角色坐标无效。");
        }

        /// <summary>始终使用回放客户端：不接受外部客户端，不可能因读档重新收费。</summary>
        public static TownRuntime Restore(TownSave save, int generation)
        {
            Validate(save);
            var client = new ReplayDialogue();
            var runtime = new TownRuntime(generation, client);
            try
            {
                foreach (var s in save.Steps)
                {
                    switch (s.Kind)
                    {
                        case "advance": runtime.Advance(s.Minutes); break;
                        case "location": runtime.SetPlayerLocation(s.Target); break;
                        case "command":
                            if (!runtime.Execute(new TownCommand((TownCommandKind)s.Command, s.Target, s.Topic, (TellMode)s.Mode)).Success)
                                throw new InvalidDataException("命令回放与当前规则不一致。");
                            break;
                        case "dialogue":
                            client.Next = s;
                            var result = runtime.TalkAsync(s.Target, s.Topic).GetAwaiter().GetResult();
                            if (result.Success != s.Success || result.Reply != s.Reply || !result.CitedBeliefs.SequenceEqual(s.Citations))
                                throw new InvalidDataException("对话回放校验失败。");
                            break;
                    }
                }
                if (RuntimeEvidence.Hash(runtime) != save.StateHash) throw new InvalidDataException("回放状态不一致，保留当前局。");
                // 回放完成后恢复离线对话，不把最后一次录音当成之后所有居民的回答。
                runtime.UseOfflineDialogue();
                return runtime;
            }
            catch { runtime.Dispose(); throw; }
        }

        private sealed class ReplayDialogue : ILLMClient
        {
            public ReplayStep Next;
            public Task<DialogueResult> ReplyAsync(NpcDialogueRequest request, CancellationToken cancellationToken)
            {
                var s = Next ?? throw new InvalidDataException("缺少对话回放。");
                Next = null;
                return Task.FromResult(new DialogueResult { Success = s.Success, Reply = s.Reply,
                    ErrorCode = s.Error, CitedBeliefs = s.Citations.ToArray() });
            }
        }
    }

    /// <summary>开发与读档一致性证据，不是提供给模型的认知输入。不包含密钥、请求体或 API 预算。</summary>
    public static class RuntimeEvidence
    {
        public static string Hash(TownRuntime rt)
        {
            var w = rt.Session.World;
            var b = new StringBuilder();
            void Add(params object[] values)
            {
                foreach (var v in values)
                {
                    string s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";
                    b.Append(s.Length).Append(':').Append(s);
                }
                b.AppendLine();
            }
            Add(w.Now.TotalMinutes, w.Version, w.PlayerLocationId);
            foreach (var i in w.Items.Values.OrderBy(i => i.Id, StringComparer.Ordinal)) Add(i.Id, i.OwnerId, i.LocationId, i.HolderId);
            foreach (var n in w.Npcs.Values.OrderBy(n => n.Id, StringComparer.Ordinal)) Add(n.Id, n.LocationId, n.TravelTo, n.ArriveAt?.TotalMinutes, n.CurrentTaskId, n.Activity);
            foreach (var f in w.Flags.OrderBy(f => f.Key, StringComparer.Ordinal)) Add(f.Key, f.Value);
            foreach (var k in rt.Session.Knowledge.AllForDebug) Add(k.Id, k.NpcId, k.PropositionId, k.Stance, k.Confidence, k.Source, k.SourceActorId, k.OriginId, k.EvidenceId, k.LearnedAt.TotalMinutes, k.SupersededBy);
            foreach (var t in rt.Session.Ledger.All) Add(t.Sequence, t.At.TotalMinutes, t.Teller, t.Receiver, t.PropositionId, t.Stance, t.Mode, t.OriginId, t.Adopted, t.IsLie, t.AlreadyKnew);
            foreach (var n in Ids.Npc.All)
            {
                foreach (var other in Ids.Npc.All.Concat(new[] { Ids.Player })) Add(n, other, rt.Session.Relations.Get(n, other));
                Add(n, rt.Session.Schedule.IsTaskControlled(n));
                foreach (var m in rt.Memory.For(n)) Add(m.PlayerText, m.Reply);
            }
            foreach (var c in rt.Session.Relations.History) Add(c.From, c.To, c.Old, c.New, c.Reason, c.At.TotalMinutes);
            Add(rt.Rules.Repair.State, rt.Rules.Repair.StartedAt?.TotalMinutes, rt.Rules.Help.Carry,
                rt.Rules.Help.CarryFailReason, rt.Rules.Help.FinnReturnedHimself, rt.Rules.Help.Promise?.State,
                rt.Rules.Help.Promise?.ResolvedAt?.TotalMinutes, rt.Rules.Apprentice.State, rt.Rules.Apprentice.LastReason);
            Add(rt.Rules.HasShelterEvidence, rt.Rules.HasWellEvidence, rt.Rules.Bell.Rung, rt.Rules.Story.Chapter, rt.Rules.Story.HasBookmark, rt.Rules.Story.HasReadLastPage, rt.Rules.Story.LastPageText);
            // 结算是钟响时封存的读数，不能只用钟后仍在变化的当前认知代替。
            foreach (var r in rt.Rules.Bell.FinalReadings)
                Add("bell:final", r.PropositionId, string.Join(",", r.Believers), string.Join(",", r.Origins));
            foreach (var r in rt.Rules.Bell.Manifested)
                Add("bell:manifested", r.PropositionId, string.Join(",", r.Believers), string.Join(",", r.Origins));
            foreach (var s in rt.Rules.Story.History) Add(s.Id, s.At.TotalMinutes, s.Text);
            return TownSaveCodec.Hash(Encoding.UTF8.GetBytes(b.ToString()));
        }
    }
}
