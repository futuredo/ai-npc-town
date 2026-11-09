using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    public static partial class DsRules
    {
        // ---------------- N1/N2 对话 ----------------

        public static void Dialogue(DsWorld w, string npcId, Dictionary<string, object> p, AiTrace t)
        {
            var actor = w.Actors[npcId];
            string say = p.Str("say");
            var cites = p.StrList("cites");
            var reveal = p.StrList("reveal");
            var persuaded = p.StrList("persuaded_by");
            double delta = p.Num("trust_delta") ?? 0;
            string action = p.Str("action");
            if (string.IsNullOrWhiteSpace(say) || cites == null || reveal == null || persuaded == null)
            { Fail(t, "schema_invalid", "缺少 say 或数组字段类型错误"); return; }
            if (action != null && action != "none") t.ValidationIssues.Add("action 只能为 none，已忽略：" + action);

            var own = new HashSet<string>(w.BeliefsOf(npcId).Select(b => b.Id));
            foreach (var id in cites.Concat(reveal).Distinct().Where(id => !own.Contains(id)).ToList())
            {
                t.ValidationIssues.Add("引用不属于该角色的认知：" + id);
                cites.Remove(id); reveal.Remove(id);
            }
            foreach (var tag in persuaded.Where(x => !ContextBuilder.PersuasionTags.Contains(x)).ToList())
            { t.ValidationIssues.Add("未知论点标签：" + tag); persuaded.Remove(tag); }

            // N2：信任变化由规则限幅。命中角色可被打动的论点，上限才放宽。
            bool resonates = persuaded.Any(x => actor.Persuadable.Contains(x));
            bool threatened = persuaded.Contains("threat");
            double cap = resonates ? 0.25 : 0.08;
            double clamped = Math.Max(-0.2, Math.Min(cap, delta));
            if (threatened && clamped > 0) clamped = Math.Min(clamped, 0.02);
            if (Math.Abs(clamped - delta) > 1e-6)
                t.Verdict.Notes.Add($"信任变化 {delta:+0.00;-0.00} 被限幅为 {clamped:+0.00;-0.00}（{(resonates ? "论点命中" : "论点未命中")}其价值观）");
            float before = actor.Trust;
            actor.Trust = (float)Math.Max(0, Math.Min(1, actor.Trust + clamped));
            if (Math.Abs(actor.Trust - before) > 1e-4)
                t.Changes.Add(new WorldChange
                {
                    Path = npcId + ".trust", Before = before.ToString("0.00"),
                    After = actor.Trust.ToString("0.00"), Note = string.Join("/", persuaded)
                });

            // N1：秘密只有信任达到门槛才允许说出。模型想说但不够格 → 拦截并换成回避台词。
            var blocked = new List<DsBelief>();
            foreach (var id in reveal)
            {
                var b = w.Belief(id);
                if (b.RevealTrust > actor.Trust) blocked.Add(b);
            }
            foreach (var b in w.BeliefsOf(npcId).Where(b => b.RevealTrust > actor.Trust && !reveal.Contains(b.Id)))
                if (Overlap(say, b.Text) >= 0.45) blocked.Add(b);   // 没申报却在台词里说漏了
            if (blocked.Count > 0)
            {
                foreach (var b in blocked)
                    t.ValidationIssues.Add($"泄密拦截：{b.Id} 需要信任 {b.RevealTrust:0.00}，当前 {actor.Trust:0.00}");
                cites = cites.Where(id => blocked.All(b => b.Id != id)).ToList();
                reveal = reveal.Where(id => blocked.All(b => b.Id != id)).ToList();
                // 规则改写：回避秘密，但保留本来就允许说的内容（否则合法线索也被吞掉）。
                var allowed = cites.Concat(reveal).Distinct().Select(w.Belief).Where(b => b.RevealTrust <= actor.Trust).ToList();
                say = Deflect(actor) + (allowed.Count > 0 ? "……不过，" + allowed[0].Text : "");
                t.Verdict.Kind = VerdictKind.Partial;
                t.Verdict.Reason = "台词触及尚未赢得的秘密，已由规则改写为回避";
            }
            else
            {
                t.Verdict.Kind = VerdictKind.Accepted;
                t.Verdict.Reason = cites.Count == 0 ? "闲谈，未引用认知" : "引用均属本人认知";
            }

            // 玩家获得线索：被引用的非秘密认知 + 被允许说出的秘密。
            foreach (var id in cites.Concat(reveal).Distinct())
            {
                var b = w.Belief(id);
                if (b.RevealTrust > actor.Trust) continue;
                if (w.PlayerKnows.Add(id))
                    t.Changes.Add(new WorldChange { Path = "player.knows", Before = "", After = id, Note = Short(b.Text) });
                string law = DsContent.LawUnlockedBy(id);
                if (law != null && w.PlayerKnows.Add("law:" + law))
                    t.Changes.Add(new WorldChange { Path = "player.laws", Before = "", After = law, Note = "可在【行】中引用" });
            }
            t.Presented = actor.Name + "：" + say;
        }
    }
}
