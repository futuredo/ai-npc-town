using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    public static partial class DsRules
    {
        /// <summary>每名居民、每种规则事件最多保留的最近亲历认知。</summary>
        public const int MaxObservedBeliefsPerOwnerAndTopic = 2;

        /// <summary>
        /// 从候选世界中的实际规则结果生成居民亲历认知。调用方必须已经完成成功判定；
        /// 这里不读取世界真值，也不接受模型台词或玩家自述作为事件来源。
        /// </summary>
        internal static void ObserveEvent(DsWorld w, string topic, string place, string eventKey,
            string text, AiTrace t)
        {
            var witnesses = w.Actors.Values
                .Where(a => a.Place == place)
                .OrderBy(a => a.Id, StringComparer.Ordinal)
                .ToList();
            if (witnesses.Count == 0) return;

            foreach (var witness in witnesses)
            {
                string previous = w.BeliefsOf(witness.Id).LastOrDefault(b => b.Topic == "event:" + topic)?.Id;
                string seed = w.ClockText + "|" + w.Version + "|" + w.RollCounter + "|" + topic + "|" +
                    eventKey + "|" + witness.Id + "|" + previous;
                string id = "b_event_" + DsWorldSnapshot.Digest(seed).Substring(0, 16) + "_" + witness.Id;
                if (w.Belief(id) != null) continue;
                w.Beliefs.Add(new DsBelief
                {
                    Id = id,
                    Owner = witness.Id,
                    RootId = id,
                    RootOwner = witness.Id,
                    Hops = 0,
                    Topic = "event:" + topic,
                    Text = w.ClockText + "的亲历事件：" + text,
                    RevealTrust = 0.0f,
                    True = true,
                    Source = "seen:event:" + topic
                });
                t?.Changes.Add(new WorldChange
                {
                    Path = witness.Id + ".beliefs",
                    Before = "",
                    After = id,
                    Note = "在场目击：" + topic
                });
                TrimObservedBeliefs(w, witness.Id, "event:" + topic, t);
            }
        }

        static void TrimObservedBeliefs(DsWorld w, string owner, string topic, AiTrace t)
        {
            var matching = w.Beliefs.Where(b => b.Owner == owner && b.Topic == topic).ToList();
            var old = matching.Take(Math.Max(0, matching.Count - MaxObservedBeliefsPerOwnerAndTopic)).ToList();
            foreach (var belief in old)
            {
                w.Beliefs.Remove(belief);
                t?.Changes.Add(new WorldChange
                {
                    Path = owner + ".beliefs",
                    Before = belief.Id,
                    After = "",
                    Note = "超出事件认知保留上限"
                });
            }
        }

        static string RootOwner(DsWorld w, DsBelief belief)
        {
            var root = w.Belief(belief.EffectiveRootId);
            return belief.RootOwner ?? root?.Owner ?? (belief.EffectiveHops == 0 ? belief.Owner : null);
        }

        /// <summary>仅在移动/换岗的候选规则中调用；各居民只观察自己所在地点，不携带岗位成因或异地信息。</summary>
        internal static void ObserveLocalGuards(DsWorld w, string place, AiTrace t)
        {
            bool watched = DsGuards.IsWatched(w, place);
            ObserveEvent(w, "guard", place, watched ? "present" : "absent",
                w.ClockText + "，我在" + w.Places[place] + (watched ? "看见这里有王城卫兵岗位。" : "此刻没有看见王城卫兵岗位。") +
                "这只是本地点本时刻的观察，不说明其他地点或后来是否安全。", t);
        }

        static void AddShareIssue(AiTrace t, string code, string id)
        {
            t.ValidationIssues.Add(code + (string.IsNullOrEmpty(id) ? "" : "：" + id));
            t.Verdict.Notes.Add(code + (string.IsNullOrEmpty(id) ? "" : "：" + id));
        }

        static bool AlreadyKnowsRoot(DsWorld w, string owner, string rootId) =>
            w.BeliefsOf(owner).Any(b => b.EffectiveRootId == rootId);

        static string NewHearsayId(DsBelief source, string listener) => source.Id + "@" + listener;

        /// <summary>规则层传话：最多两跳、根去重、禁止传回根主人。</summary>
        static void ShareBeliefs(DsWorld w, DsActor speaker, DsActor listener,
            IEnumerable<string> requested, AiTrace t, out List<DsBelief> shared, out string firstFailure)
        {
            shared = new List<DsBelief>();
            firstFailure = null;
            foreach (var id in requested.Distinct(StringComparer.Ordinal))
            {
                var source = w.Belief(id);
                string failure = null;
                if (source == null || source.Owner != speaker.Id)
                    failure = "cannot_relay_others_belief";
                else if (source.EffectiveHops >= 2)
                    failure = "hearsay_too_far";
                else if (RootOwner(w, source) == listener.Id)
                    failure = "hearsay_to_origin";
                else if (AlreadyKnowsRoot(w, listener.Id, source.EffectiveRootId))
                    failure = "already_known";

                if (failure != null)
                {
                    firstFailure = firstFailure ?? failure;
                    AddShareIssue(t, failure, id);
                    continue;
                }

                string newId = NewHearsayId(source, listener.Id);
                if (w.Belief(newId) != null)
                {
                    firstFailure = firstFailure ?? "already_known";
                    AddShareIssue(t, "already_known", id);
                    continue;
                }
                var belief = new DsBelief
                {
                    Id = newId,
                    Owner = listener.Id,
                    RootId = source.EffectiveRootId,
                    RootOwner = RootOwner(w, source),
                    Hops = source.EffectiveHops + 1,
                    Topic = source.Topic,
                    Text = "听" + speaker.Name + "说：" + source.Text,
                    RevealTrust = Math.Max(0.1f, source.RevealTrust * 0.5f),
                    True = source.True,
                    Source = "told:" + speaker.Id
                };
                w.Beliefs.Add(belief);
                shared.Add(belief);
                t.Changes.Add(new WorldChange { Path = listener.Id + ".beliefs", Before = "", After = newId,
                    Note = "实际传出（root=" + belief.RootId + ", hops=" + belief.Hops + "）：" + belief.Text });
                if (belief.Topic != null && belief.Topic.StartsWith("event:", StringComparison.Ordinal))
                    TrimObservedBeliefs(w, listener.Id, belief.Topic, t);
            }
        }
    }
}
