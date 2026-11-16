using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NUnit.Framework;
using Town.Core.Dragonsleep;
using Town.OpenAI;

namespace Town.Core.Tests
{
    /// <summary>rules-9社会链与实际规则事件；固定骰、零网络、零密钥。</summary>
    public sealed class DragonsleepRules9SocialTests
    {
        static DsWorld Together()
        {
            var w = DsContent.NewWorld(7);
            foreach (var a in w.Actors.Values) a.Place = "market";
            w.Phase = DayPhase.Night;
            return w;
        }

        static Dictionary<string, object> TellProposal(string target, params string[] ids) => new Dictionary<string, object>
        {
            ["verb"] = "tell", ["place"] = "market", ["target"] = target,
            ["share"] = ids.Cast<object>().ToList(), ["reason"] = "模型动机不能覆盖规则原因"
        };

        static AiTrace Tell(DsWorld w, string speaker, string target, params string[] ids)
        {
            var t = new AiTrace();
            DsRules.AgentPlan(w, speaker, TellProposal(target, ids), t);
            return t;
        }

        static AiTrace Act(DsWorld w, string verb, string target = "maren", string item = "pact_page", double roll = .9999)
        {
            var t = new AiTrace();
            DsRules.Adjudicate(w, new DsPlanStep { Verb = verb, Target = target, Item = item }.ToProposal(w.PlayerPlace),
                t, new DsFixedRoll(roll));
            return t;
        }

        [Test]
        public void 两跳保留错误认知与来源_第三跳拒绝不假报成功()
        {
            var w = Together();
            var root = w.Belief("b_marta_saw_pip"); root.True = false; root.RevealTrust = .8f;
            Assert.AreEqual(VerdictKind.Accepted, Tell(w, "marta", "vesna", root.Id).Verdict.Kind);
            var first = w.Belief(root.Id + "@vesna");
            Assert.AreEqual(root.Id, first.RootId); Assert.AreEqual("marta", first.RootOwner);
            Assert.AreEqual(1, first.Hops); Assert.AreEqual(.4f, first.RevealTrust); Assert.IsFalse(first.True);
            var secondTrace = Tell(w, "vesna", "osric", first.Id);
            var second = w.Belief(first.Id + "@osric");
            Assert.AreEqual(VerdictKind.Accepted, secondTrace.Verdict.Kind);
            Assert.AreEqual(2, second.Hops); Assert.AreEqual(.2f, second.RevealTrust); Assert.IsFalse(second.True);
            StringAssert.Contains("听" + w.Actors["vesna"].Name + "说：听" + w.Actors["marta"].Name + "说", second.Text);
            int count = w.Beliefs.Count;
            var rejected = Tell(w, "osric", "pip", second.Id);
            Assert.AreEqual(VerdictKind.Rejected, rejected.Verdict.Kind);
            Assert.AreEqual("hearsay_too_far", rejected.Verdict.ReasonCode.Code);
            Assert.AreEqual("没有可实际传出的认知", rejected.Verdict.Reason);
            Assert.AreEqual(count, w.Beliefs.Count);
            StringAssert.DoesNotContain(root.Text, rejected.Presented);
            Assert.IsFalse(rejected.Changes.Any(c => c.Note == "收到新信息"));
        }

        [Test]
        public void 按根去重与防回传_不靠旧拼接判断_信任下限不变()
        {
            var w = Together(); var root = w.Belief("b_marta_saw_pip"); root.RevealTrust = 0;
            Tell(w, "marta", "vesna", root.Id);
            var first = w.Belief(root.Id + "@vesna");
            Assert.AreEqual(.1f, first.RevealTrust);
            Assert.AreEqual("hearsay_to_origin", Tell(w, "vesna", "marta", first.Id).Verdict.ReasonCode.Code);
            Tell(w, "marta", "osric", root.Id);
            var duplicate = Tell(w, "vesna", "osric", first.Id);
            Assert.AreEqual(VerdictKind.Rejected, duplicate.Verdict.Kind);
            Assert.AreEqual("already_known", duplicate.Verdict.ReasonCode.Code);
            Assert.AreEqual(1, w.BeliefsOf("osric").Count(b => b.EffectiveRootId == root.Id));
            first.Id = "explicit_source_without_at";
            var t = Tell(w, "vesna", "pip", first.Id);
            Assert.AreEqual(VerdictKind.Accepted, t.Verdict.Kind);
            Assert.AreEqual(2, w.Belief(first.Id + "@pip").Hops);
            Assert.AreEqual(.1f, w.Belief(first.Id + "@pip").RevealTrust);
        }

        [Test]
        public void 部分传出只呈现实际内容_他人认知与空选择全拒绝()
        {
            var w = Together();
            var t = Tell(w, "marta", "vesna", "b_marta_saw_pip", "b_maren_page");
            Assert.AreEqual(VerdictKind.Partial, t.Verdict.Kind);
            Assert.AreEqual("partial_hearsay", t.Verdict.ReasonCode.Code);
            StringAssert.Contains(w.Belief("b_marta_saw_pip").Text, t.Presented);
            StringAssert.DoesNotContain(w.Belief("b_maren_page").Text, t.Presented);
            Assert.IsTrue(t.Changes.Any(c => c.Note.Contains("root=b_marta_saw_pip, hops=1")));
            var bad = Tell(w, "marta", "vesna", "b_maren_page");
            Assert.AreEqual(VerdictKind.Rejected, bad.Verdict.Kind);
            Assert.AreEqual("cannot_relay_others_belief", bad.Verdict.ReasonCode.Code);
            var empty = Tell(w, "marta", "vesna");
            Assert.AreEqual(VerdictKind.Rejected, empty.Verdict.Kind);
            Assert.AreEqual("no_beliefs_selected", empty.Verdict.ReasonCode.Code);
        }

        [Test]
        public void 旧快照补全root与跳数_新字段影响摘要差分哈希()
        {
            var w = Together(); Tell(w, "marta", "vesna", "b_marta_saw_pip");
            var d = (Dictionary<string, object>)MiniJson.Parse(DsWorldSnapshot.Capture(w, 0, 4).Json);
            foreach (Dictionary<string, object> b in (List<object>)d["beliefs"])
            { b.Remove("root"); b.Remove("hops"); b.Remove("root_owner"); }
            var loaded = DsWorldSnapshot.Parse(MiniJson.Write(d));
            var restored = loaded.Restore();
            Assert.AreEqual(0, restored.Belief("b_marta_saw_pip").Hops);
            Assert.AreEqual(1, restored.Belief("b_marta_saw_pip@vesna").Hops);
            Assert.AreEqual("b_marta_saw_pip", restored.Belief("b_marta_saw_pip@vesna").RootId);
            Assert.AreEqual("marta", restored.Belief("b_marta_saw_pip@vesna").RootOwner);
            restored.Belief("b_marta_saw_pip@vesna").Hops = 2;
            var changed = DsWorldSnapshot.Capture(restored, 0, 4);
            Assert.AreNotEqual(loaded.Hash, changed.Hash);
            Assert.IsTrue(DsWorldSnapshot.Diff(loaded, changed).Any(x => x.Path.EndsWith(".hops")));
            StringAssert.Contains("\"hops\":2", NpcPerceptionView.Capture(restored, "vesna").Json);
        }

        [Test]
        public void 交付取回出示只在实际成功时给在场者_包含原件保管建立撤销()
        {
            var w = DsContent.NewWorld(7); w.PlayerPlace = "cathedral";
            w.Items["pact_page"].Holder = "player"; w.Actors["pip"].Place = "cathedral";
            Act(w, "give", roll: 0);
            Assert.IsFalse(w.Beliefs.Any(b => b.Topic == "event:give"));
            Act(w, "show"); Act(w, "give");
            Assert.IsTrue(w.Flags.ContainsKey("pact_custody:maren"));
            Assert.IsFalse(w.Flags.ContainsKey("ally:maren"));
            Act(w, "confide", item: ""); Act(w, "take", target: "");
            Assert.IsFalse(w.Flags.ContainsKey("pact_custody:maren"));
            Assert.IsFalse(w.Flags.ContainsKey("ally:maren"));
            foreach (var topic in new[] { "show", "give", "take" })
            {
                var events = w.Beliefs.Where(b => b.Topic == "event:" + topic).ToList();
                CollectionAssert.AreEquivalent(new[] { "maren", "pip" }, events.Select(b => b.Owner));
                Assert.IsTrue(events.All(b => b.Hops == 0 && b.RootId == b.Id && b.RootOwner == b.Owner && b.True));
                Assert.IsFalse(NpcPerceptionView.Capture(w, "marta").Json.Contains(events[0].Id));
                Assert.IsFalse(TaskPerceptionView.Capture(w, AiTaskKind.AgentPlan, "vesna").Json.Contains(events[0].Id));
            }
            StringAssert.Contains("保管核验", w.BeliefsOf("maren").Single(b => b.Topic == "event:give").Text);
            StringAssert.Contains("已撤销", w.BeliefsOf("maren").Single(b => b.Topic == "event:take").Text);
        }

        [Test]
        public void 准备候选与过期拒绝不写本人事件_真实提交才写_回放稳定()
        {
            var w = DsContent.NewWorld(7); w.PlayerPlace = "cathedral"; w.Items["pact_page"].Holder = "player";
            var e = new DsExecutor(new DsFixedRoll(.9999));
            var p = new DsPlanStep { Verb = "give", Target = "maren", Item = "pact_page" }.ToProposal(w.PlayerPlace);
            var trial = e.Prepare(w, 0, 4, AiTaskKind.Adjudicate, "player", "candidate", p);
            Assert.IsFalse(w.Beliefs.Any(b => b.Topic == "event:give"));
            Assert.IsTrue(DsWorldSnapshot.Parse(trial.AfterJson).Restore().Beliefs.Any(b => b.Topic == "event:give"));
            int spent = 0; w.Coins++;
            Assert.AreEqual("stale_state", e.Commit(trial, w, ref spent, 4).Status);
            Assert.IsFalse(w.Beliefs.Any(b => b.Topic == "event:give"));
            var again = e.Prepare(w, 0, 4, AiTaskKind.Adjudicate, "player", "different-request-id", p);
            var identical = e.Prepare(w, 0, 4, AiTaskKind.Adjudicate, "player", "another-id", p);
            Assert.AreEqual(again.AfterJson, identical.AfterJson, "事件ID不能依赖请求编号");
            Assert.IsTrue(e.Commit(again, w, ref spent, 4).Applied);
            Assert.IsTrue(w.Beliefs.Any(b => b.Topic == "event:give"));
        }

        [Test]
        public void 本地岗哨观察不带异地原因_不会被离线居民误认成护送目标()
        {
            var w = DsContent.NewWorld(7); w.Phase = DayPhase.Night;
            w.Actors["osric"].Place = "skull_hill"; w.Flags["guard:skull_hill"] = "osric";
            var t = new AiTrace(); DsRules.PhaseStart(w, t);
            var seen = w.BeliefsOf("osric").Single(b => b.Topic == "event:guard");
            StringAssert.Contains("龙骨岗", seen.Text); StringAssert.Contains("看见这里有", seen.Text);
            StringAssert.DoesNotContain("护送计划", seen.Text);
            var first = TaskPerceptionView.Capture(w, AiTaskKind.AgentPlan, "osric");
            w.Flags["guard:castle"] = "osric";
            Assert.AreEqual(first.Json, TaskPerceptionView.Capture(w, AiTaskKind.AgentPlan, "osric").Json);
            var s = new DragonsleepSession(new RuleProvider()); s.RestoreState(DsWorldSnapshot.Capture(w, 0, 4));
            var trace = s.RunAsync(AiTaskKind.AgentPlan, "osric", null).GetAwaiter().GetResult();
            Assert.AreEqual("search", trace.Proposal.Str("verb"), "岗哨地点不是他已知的玩家护送目的地");
        }

        [Test]
        public void 每人每类最近N条含传播事件_淘汰根后仍防回传()
        {
            var w = Together(); w.PlayerPlace = "market"; w.Items["pact_page"].Holder = "player";
            w.Actors["vesna"].Place = "castle";
            Act(w, "show", "marta");
            var root = w.BeliefsOf("marta").Last(b => b.Topic == "event:show");
            w.Actors["vesna"].Place = "market";
            Tell(w, "marta", "vesna", root.Id);
            var first = w.Belief(root.Id + "@vesna");
            w.Actors["vesna"].Place = "castle";
            for (int i = 0; i < 6; i++) Act(w, "show", "marta");
            Assert.IsNull(w.Belief(root.Id));
            Assert.AreEqual(DsRules.MaxObservedBeliefsPerOwnerAndTopic, w.BeliefsOf("marta").Count(b => b.Topic == "event:show"));
            w.Actors["vesna"].Place = "market";
            Assert.AreEqual("hearsay_to_origin", Tell(w, "vesna", "marta", first.Id).Verdict.ReasonCode.Code);
            foreach (var belief in w.BeliefsOf("marta").Where(b => b.Topic == "event:show").ToArray())
                Tell(w, "marta", "vesna", belief.Id);
            Assert.AreEqual(DsRules.MaxObservedBeliefsPerOwnerAndTopic, w.BeliefsOf("vesna").Count(b => b.Topic == "event:show"));
        }

        [Test]
        public void 饱和事件感知冻结payload确定且不超过12KiB_他人私有差分逐字相同()
        {
            var w = Together(); w.PlayerPlace = "market"; w.Items["pact_page"].Holder = "player";
            for (int i = 0; i < 5; i++)
            { Act(w, "show"); Act(w, "give"); Act(w, "take", ""); DsRules.PhaseStart(w, new AiTrace()); }
            int max = 0;
            foreach (var actor in w.Actors.Keys)
            foreach (var kind in new[] { AiTaskKind.Dialogue, AiTaskKind.AgentPlan })
            {
                PromptPacket Packet(DsWorld world) => kind == AiTaskKind.Dialogue
                    ? ContextBuilder.BuildDialogue(NpcPerceptionView.Capture(world, actor), "请仅回顾自己亲历的事实。")
                    : ContextBuilder.BuildTask(TaskPerceptionView.Capture(world, kind, actor), "仅据本人的见闻行动。");
                string Body(PromptPacket p) => OpenAiDragonsleepProvider.BuildPayload(p, OpenAiRunProfile.SixMechanismResearch).ToString(Formatting.None);
                var packet = Packet(w); string original = Body(packet);
                Assert.AreEqual(original, Body(Packet(w)));
                var changed = DsWorldSnapshot.Capture(w, 0, 4).Restore();
                foreach (var b in changed.Beliefs.Where(b => b.Owner != actor)) b.Text = "PRIVATE_RULES9_SENTINEL";
                Assert.AreEqual(original, Body(Packet(changed)));
                int bytes = Encoding.UTF8.GetByteCount(original); max = Math.Max(max, bytes);
                Assert.LessOrEqual(bytes, 12 * 1024, actor + ":" + kind);
                w.Coins++;
                Assert.AreEqual(original, Body(packet), "冻结payload不回读可变world");
            }
            TestContext.WriteLine("rules9_saturated_max_request_bytes=" + max);
        }
    }
}
