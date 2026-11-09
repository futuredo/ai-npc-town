using System;
using System.Collections.Generic;
using System.Linq;
using Town.Core.Content;
using Town.Core.Knowledge;
using Town.Core.Social;
using Town.Core.World;

namespace Town.Core.Rules
{
    /// <summary>
    /// 话怎么传：玩家传话的三种说法（第四种「保密」就是不说）、居民闲聊、偷听、反驳与澄清。
    /// 所有认知变化都在这里按规则写入，并记进流言账本；模型台词不能调用这里。
    /// </summary>
    public sealed class RumorRules
    {
        public const float ShareThreshold = 0.5f;
        public const int GossipCooldownMinutes = 120;

        private readonly HammerEventSetup.Session _s;
        private readonly Action<string, string> _note;
        private readonly Dictionary<string, int> _lastGossip = new Dictionary<string, int>();

        public RumorRules(HammerEventSetup.Session session, Action<string, string> note)
        {
            _s = session;
            _note = note ?? ((_, __) => { });
        }

        private WorldState World => _s.World;
        private KnowledgeStore K => _s.Knowledge;

        // ———————————— 玩家获知 ————————————

        /// <summary>居民回答玩家时引用了哪些认知，玩家就按「听某人说」记进记事本。</summary>
        public void PlayerHeard(string npcId, IEnumerable<string> citedBeliefIds)
        {
            foreach (var id in citedBeliefIds ?? Enumerable.Empty<string>())
            {
                var b = K.Find(id);
                if (b == null || b.NpcId != npcId || !b.IsActive) continue;
                LearnPlayer(b.PropositionId, b.Stance, b.Confidence, npcId, OriginOf(b), TellMode.Answer);
            }
        }

        /// <summary>玩家亲眼看到证据。</summary>
        public void PlayerSaw(string propositionId, bool stance, string evidenceId)
        {
            var cur = K.ViewFor(Ids.Player).Current(propositionId);
            if (cur != null && cur.Source == SourceType.SawEvidence && cur.Stance == stance) return;
            K.Learn(Ids.Player, propositionId, stance, 1f, SourceType.SawEvidence, World.Now, evidenceId: evidenceId);
            _s.Ledger.Add(new Transmission { At = World.Now, Teller = evidenceId, Receiver = Ids.Player, PropositionId = propositionId,
                Stance = stance, Mode = TellMode.Evidence, OriginId = Ids.Player, Adopted = true, Reaction = "你亲眼看到了" });
        }

        /// <summary>玩家亲眼目睹某件事发生。</summary>
        public void PlayerWitnessed(string propositionId, bool stance)
        {
            K.Learn(Ids.Player, propositionId, stance, 1f, SourceType.Witnessed, World.Now);
            _s.Ledger.Add(new Transmission { At = World.Now, Teller = Ids.Player, Receiver = Ids.Player, PropositionId = propositionId,
                Stance = stance, Mode = TellMode.Firsthand, OriginId = Ids.Player, Adopted = true, Reaction = "你亲眼看见了" });
        }

        private void LearnPlayer(string prop, bool stance, float confidence, string teller, string origin, TellMode mode)
        {
            var cur = K.ViewFor(Ids.Player).Current(prop);
            if (cur != null && cur.Stance == stance && cur.OriginId == origin) return;
            K.Learn(Ids.Player, prop, stance, confidence, SourceType.ToldBy, World.Now, sourceActorId: teller, originId: origin);
            _s.Ledger.Add(new Transmission { At = World.Now, Teller = teller, Receiver = Ids.Player, PropositionId = prop,
                Stance = stance, Mode = mode, OriginId = origin, Adopted = true, Reaction = "记进了读者手记" });
        }

        // ———————————— 玩家传话 ————————————

        public CommandResult Tell(string receiverId, string propositionId, TellMode mode)
        {
            if (mode != TellMode.Faithful && mode != TellMode.ClaimSeen && mode != TellMode.Exaggerate)
                return CommandResult.Reject("只能如实转述、说成亲眼所见或添油加醋。");
            if (!World.Npcs.TryGetValue(receiverId, out var npc) || npc.LocationId == null || npc.LocationId != World.PlayerLocationId)
                return CommandResult.Reject("请先走到这位居民身边。");
            if (!Propositions.All.ContainsKey(propositionId)) return CommandResult.Reject("未知的说法。");

            var mine = K.ViewFor(Ids.Player).Current(propositionId);
            if (mine == null) return CommandResult.Reject("你的读者手记里没有这件事。");

            string say = propositionId;
            bool stance = mine.Stance;
            bool firsthand = mine.Source == SourceType.SawEvidence || mine.Source == SourceType.Witnessed || mine.Source == SourceType.Experienced;
            string origin;
            bool lie = false;
            float confidence;
            switch (mode)
            {
                case TellMode.Faithful:
                    origin = OriginOf(mine);
                    confidence = 0.5f;
                    break;
                case TellMode.ClaimSeen:
                    origin = Ids.Player;
                    lie = !firsthand;
                    confidence = 0.8f;
                    break;
                default: // Exaggerate
                    var p = Propositions.All[propositionId];
                    if (!stance || p.ExaggeratesTo == null) return CommandResult.Reject("这件事没法再往重里说了。");
                    say = p.ExaggeratesTo;
                    stance = true;
                    origin = Ids.Player;
                    lie = true;
                    confidence = 0.6f;
                    break;
            }

            var t = Deliver(Ids.Player, receiverId, say, stance, confidence, mode, origin, lie);
            if (t.Adopted && receiverId == Ids.Npc.Ahe)
                _s.Relations.ApplyOnce("ahe_news", Ids.Npc.Ahe, Ids.Player, +1, "你跟她分享了新鲜事", World.Now);
            return CommandResult.Ok(t.Reaction);
        }

        // ———————————— 一句话送达：所有传播的唯一入口 ————————————

        internal Transmission Deliver(string teller, string receiver, string prop, bool stance, float confidence,
            TellMode mode, string origin, bool isLie, int depth = 0)
        {
            var p = Propositions.All[prop];
            var view = K.ViewFor(receiver);
            var cur = view.Current(prop);
            string name = Ids.Name(receiver);
            var t = _s.Ledger.Add(new Transmission { At = World.Now, Teller = teller, Receiver = receiver, PropositionId = prop,
                Stance = stance, Mode = mode, OriginId = origin, IsLie = isLie });

            // 1. 当面说本人的坏话
            if (p.IsAccusation && stance && p.SubjectId == receiver)
            {
                t.Reaction = $"{name}又气又委屈：「我没有！」";
                _s.Relations.ApplyOnce($"accused:{teller}:{prop}", receiver, teller, -1, $"当面说他「{p.Text}」", World.Now);
                if (origin == Ids.Player && teller != Ids.Player)
                    _s.Relations.ApplyOnce($"accused:origin_player:{prop}", receiver, Ids.Player, -1, "听说这话最早是你说的", World.Now);
                Rebut(receiver, teller, prop, depth);
                return Finish(t);
            }

            // 2. 不信任说话的人
            if (teller != receiver && _s.Relations.Get(receiver, teller) == RelationLevel.Wary)
            {
                t.Reaction = $"{name}不太信你说的话。";
                if (teller != Ids.Player) t.Reaction = $"{name}不太信{Ids.Name(teller)}说的话。";
                return Finish(t);
            }

            // 3. 自己亲身知道的事与此相反（推断不算）
            var conflict = view.ActiveBeliefs.FirstOrDefault(b => b.Stance && b.Source != SourceType.Inferred &&
                b.Confidence >= ShareThreshold && Propositions.All[b.PropositionId].Refutes.Contains(prop) &&
                KnowledgeStore.Strength(b.Source) > KnowledgeStore.Strength(SourceType.ToldBy));
            if (conflict != null || (cur != null && cur.Stance != stance && KnowledgeStore.Strength(cur.Source) > KnowledgeStore.Strength(SourceType.ToldBy)))
            {
                t.Reaction = $"{name}摇头：「不对，我知道的不是这样。」";
                Rebut(receiver, teller, prop, depth);
                return Finish(t);
            }

            // 4. 早就亲身知道了
            if (cur != null && cur.Stance == stance && KnowledgeStore.Strength(cur.Source) > KnowledgeStore.Strength(SourceType.ToldBy))
            {
                t.Reaction = $"{name}：「这个我早就知道了。」";
                t.AlreadyKnew = true;
                return Finish(t);
            }

            // 5. 同一个源头的话再听一遍，不算新证据
            if (cur != null && cur.Stance == stance && cur.OriginId == origin)
            {
                t.Reaction = $"{name}：「这话我已经听过了。」";
                t.AlreadyKnew = true;
                return Finish(t);
            }

            // 5b. 已经因为别的源头相信了：记为又一个人这么说，保留原来的源头（墨钟按源头计数）
            if (cur != null && cur.Stance == stance && cur.Source == SourceType.ToldBy)
            {
                t.Adopted = true;
                t.Reaction = $"{name}：「{Ids.Name(cur.SourceActorId)}也这么说过。」";
                return Finish(t);
            }

            // 6. 较真的人听到指控：要么说话人声称亲眼所见，要么已从两个不同源头听到
            if (p.IsAccusation && stance && DemandsEvidence(receiver) && mode != TellMode.ClaimSeen &&
                _s.Ledger.OriginsHeard(receiver, prop, true).Count() < 2)
            {
                t.Reaction = $"{name}皱眉：「你亲眼看见了？就凭一句传言，我不信。」";
                Rebut(receiver, teller, prop, depth);
                return Finish(t);
            }

            // 7. 采信
            var belief = K.Learn(receiver, prop, stance, confidence, SourceType.ToldBy, World.Now, sourceActorId: teller, originId: origin);
            t.Adopted = true;
            t.Reaction = mode == TellMode.ClaimSeen
                ? $"{name}吃了一惊：「你亲眼看见的？那我信了。」"
                : $"{name}点点头，记下了这件事。";
            ApplyRefutation(receiver, belief, teller);
            if (prop == Propositions.Stole && !stance && cur != null && cur.Stance && teller == Ids.Player)
                _s.Relations.ApplyOnce("clarified_finn", Ids.Npc.Xiaochuan, Ids.Player, +1, "你替他澄清了偷锤子的谣言", World.Now);
            return Finish(t);
        }

        private Transmission Finish(Transmission t)
        {
            _note(t.Adopted ? "rumor:adopted" : "rumor:rejected",
                $"{Ids.Name(t.Teller)}→{Ids.Name(t.Receiver)}「{Propositions.TextOf(t.PropositionId)}」{(t.Adopted ? "信了" : "没信")}");
            return t;
        }

        /// <summary>新认知为真时，把它反驳的旧说法改为假（证据或转述都适用，强度不低于旧认知才生效）。</summary>
        internal void ApplyRefutation(string receiver, Belief trigger, string teller)
        {
            if (!trigger.Stance) return;
            var view = K.ViewFor(receiver);
            foreach (var refuted in Propositions.All[trigger.PropositionId].Refutes)
            {
                var old = view.Current(refuted);
                if (old == null || !old.Stance) continue;
                if (KnowledgeStore.Strength(old.Source) > KnowledgeStore.Strength(trigger.Source)) continue;
                K.Learn(receiver, refuted, false, trigger.Confidence, trigger.Source, World.Now,
                    sourceActorId: trigger.SourceActorId, evidenceId: trigger.EvidenceId, originId: trigger.OriginId);
                _note("rumor:corrected", $"{Ids.Name(receiver)}不再相信「{Propositions.TextOf(refuted)}」");
                if (refuted == Propositions.Stole && (teller == Ids.Player || trigger.Source == SourceType.SawEvidence))
                    _s.Relations.ApplyOnce("clarified_finn", Ids.Npc.Xiaochuan, Ids.Player, +1, "你替他澄清了偷锤子的谣言", World.Now);
            }
        }

        /// <summary>居民不同意时，拿自己亲身知道的事反驳。</summary>
        private void Rebut(string rebutter, string target, string prop, int depth)
        {
            if (depth > 1 || !WillRebut(rebutter, prop)) return;
            var defenses = Propositions.All[prop].Defenses;
            foreach (var b in K.ViewFor(rebutter).ActiveBeliefs.Where(b => b.Stance && defenses.Contains(b.PropositionId) &&
                         b.Source != SourceType.Inferred && b.Source != SourceType.ToldBy).OrderBy(b => b.PropositionId))
            {
                if (target == Ids.Player) LearnPlayer(b.PropositionId, true, b.Confidence, rebutter, rebutter, TellMode.Rebuttal);
                else Deliver(rebutter, target, b.PropositionId, true, 0.7f, TellMode.Rebuttal, rebutter, false, depth + 1);
            }
        }

        // ———————————— 居民闲聊与偷听 ————————————

        /// <summary>同一地点的居民按冷却时间闲聊一次；只有爱说话的人会主动开口。玩家在场就能偷听。</summary>
        public void TickGossip()
        {
            var present = World.Npcs.Values.Where(n => !n.IsTravelling && n.LocationId != null)
                .GroupBy(n => n.LocationId);
            foreach (var group in present)
            {
                var here = group.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
                if (here.Count < 2) continue;
                foreach (var teller in here.Where(n => IsChatty(n.Id)))
                foreach (var listener in here.Where(n => n.Id != teller.Id))
                {
                    string key = teller.Id + ">" + listener.Id;
                    if (_lastGossip.TryGetValue(key, out var last) && World.Now.TotalMinutes - last < GossipCooldownMinutes) continue;
                    _lastGossip[key] = World.Now.TotalMinutes;
                    Gossip(teller.Id, listener.Id, group.Key);
                }
            }
        }

        private void Gossip(string teller, string listener, string locationId)
        {
            var view = K.ViewFor(teller);
            var said = new HashSet<string>();
            bool playerHere = World.PlayerLocationId == locationId;
            _note("gossip", $"{Ids.Name(teller)}和{Ids.Name(listener)}在{Ids.Name(locationId)}闲聊");
            foreach (var b in view.ActiveBeliefs.Where(b => b.Stance && b.Confidence >= ShareThreshold).OrderBy(b => b.PropositionId, StringComparer.Ordinal))
            {
                var p = Propositions.All[b.PropositionId];
                if (p.Id == Propositions.Promise || p.SecretOwnerId == teller) continue;
                string say = p.Id;
                var mode = TellMode.Gossip;
                if (p.ExaggeratesTo != null && !(view.Current(p.ExaggerationBlockedBy)?.Stance ?? false))
                {
                    say = p.ExaggeratesTo;
                    mode = TellMode.GossipExaggerated;
                }
                if (Propositions.All[say].SubjectId == listener || !said.Add(say)) continue;
                string origin = mode == TellMode.GossipExaggerated ? teller : OriginOf(b);
                Deliver(teller, listener, say, true, 0.5f, mode, origin, false);
                if (playerHere) LearnPlayer(say, true, 0.5f, teller, origin, TellMode.Overheard);
            }
        }

        // ———————————— 证据 ————————————

        /// <summary>居民看到玩家出示的证据。</summary>
        internal bool ShowEvidence(string receiver, string prop, string evidenceId)
        {
            var view = K.ViewFor(receiver);
            if (view.History.Any(b => b.EvidenceId == evidenceId)) return false;
            var belief = K.Learn(receiver, prop, true, 1f, SourceType.SawEvidence, World.Now, evidenceId: evidenceId, originId: Ids.Player);
            _s.Ledger.Add(new Transmission { At = World.Now, Teller = Ids.Player, Receiver = receiver, PropositionId = prop, Stance = true,
                Mode = TellMode.Evidence, OriginId = Ids.Player, Adopted = true, Reaction = $"{Ids.Name(receiver)}看到了证据" });
            ApplyRefutation(receiver, belief, Ids.Player);
            return true;
        }

        // ———————————— 性格 ————————————

        /// <summary>梅芙爱闲聊；布伦和芬恩不主动传话。</summary>
        public static bool IsChatty(string npcId) => npcId == Ids.Npc.Ahe;

        /// <summary>布伦对指控要证据。</summary>
        public static bool DemandsEvidence(string npcId) => npcId == Ids.Npc.Amu;

        /// <summary>布伦反驳一切指控；芬恩只为自己辩护；梅芙不同意就会说。</summary>
        private static bool WillRebut(string npcId, string prop)
        {
            var p = Propositions.All[prop];
            if (npcId == Ids.Npc.Amu) return p.IsAccusation;
            if (npcId == Ids.Npc.Xiaochuan) return p.IsAccusation && p.SubjectId == npcId;
            return npcId == Ids.Npc.Ahe;
        }

        /// <summary>转述的认知追溯原始源头；亲历、目击、推断、证据的源头就是本人。</summary>
        internal static string OriginOf(Belief b) => b.Source == SourceType.ToldBy ? (b.OriginId ?? b.SourceActorId) : b.NpcId;
    }
}
