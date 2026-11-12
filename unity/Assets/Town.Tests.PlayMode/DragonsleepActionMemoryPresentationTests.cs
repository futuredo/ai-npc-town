using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;
using Town.Unity.Dragonsleep;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace Town.Tests.PlayMode
{
    /// <summary>真实主场景与内核的离线管线测试；固定合法提案，不冒充自然语言理解验收，不替换骰子。</summary>
    public sealed class DragonsleepActionMemoryPresentationTests
    {
        const string Feed = "{\"verb\":\"feed\",\"target\":\"whelp\",\"item\":\"smoked_fish\",\"place\":\"docks\"," +
            "\"laws\":[],\"difficulty\":0.1,\"narration\":\"你递出同一串熏鱼。\"}";
        const string Observe = "{\"verb\":\"observe\",\"target\":\"\",\"item\":\"\",\"place\":\"\",\"laws\":[],\"difficulty\":0.1,\"narration\":\"你观察四周。\"}";
        DragonsleepGame _game; VisualElement _root; Scripted _provider;
        Func<IAiProvider> _factory; bool _motion, _playerEnabled;
        DragonsleepSession S => _game.Session;
        bool Idle => !_game.Busy && !S.Busy && Ready("sendButton");

        sealed class Scripted : IAiProvider
        {
            public string Name => "offline-action-memory-scripted";
            public bool IsSimulated => true;
            public readonly ConcurrentQueue<string> Replies = new ConcurrentQueue<string>();
            public readonly ConcurrentQueue<PromptPacket> Packets = new ConcurrentQueue<PromptPacket>();
            public bool Hold;
            ProviderResult _held;
            readonly TaskCompletionSource<ProviderResult> _reply = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                if (!Replies.TryDequeue(out var raw)) throw new InvalidOperationException("unexpected_offline_call");
                bool hold = Hold;
                var result = new ProviderResult { ProviderName = Name, RawText = raw, Simulated = true };
                if (hold) _held = result;
                Packets.Enqueue(packet.FreezeCopy());
                return hold ? _reply.Task : Task.FromResult(result);
            }
            public void Release() { Hold = false; _reply.TrySetResult(_held); }
        }
        static List<object> History(PromptPacket packet) => (List<object>)packet.TaskSnapshot.ToDictionary()["recent_player_actions"];
        PromptPacket Packet(int index) => _provider.Packets.ToArray()[index];
        static IEnumerator Until(Func<bool> done)
        {
            float end = Time.realtimeSinceStartup + 12;
            while (!done() && Time.realtimeSinceStartup < end) yield return null;
            Assert.IsTrue(done(), "真实离线条件须在 12 秒内完成，不能以固定帧数代替证据");
        }
        bool Ready(string name) => _root.Q<Button>(name) is Button b && b.enabledInHierarchy
            && b.resolvedStyle.display != DisplayStyle.None && b.worldBound.width > 0 && b.worldBound.height > 0;
        void Click(string name)
        {
            Assert.IsTrue(Ready(name), name); var b = _root.Q<Button>(name);
            b.Focus(); using (var e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); }
        }
        void Send(string raw, string text)
        {
            _provider.Replies.Enqueue(raw); Click("mode_Act");
            _root.Q<TextField>("playerInput").value = text; Click("sendButton");
        }
        [UnitySetUp] public IEnumerator Load()
        {
            _game = null; _factory = DragonsleepGame.ProviderFactory; _motion = DsUiMotion.Reduced;
            _provider = new Scripted(); DragonsleepGame.ProviderFactory = () => _provider; DsUiMotion.Reduced = true;
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
            _game = UnityEngine.Object.FindFirstObjectByType<DragonsleepGame>(); Assert.IsNotNull(_game);
            _root = _game.GetComponent<UIDocument>().rootVisualElement;
            _playerEnabled = _game.Player.enabled; _game.Player.enabled = false;
            yield return Until(() => Ready("beginJourney")); Click("beginJourney");
            yield return Until(() => !_game.Hud.ModalOpen && Idle);
            var w = S.World; w.Seed = 109; w.RollCounter = 0;
            w.PlayerPlace = w.Whelp.Place = "docks"; w.Items["smoked_fish"].Holder = "player";
            w.Whelp.Hidden = false; w.Whelp.Fear = 0.45f; w.Whelp.TrustPlayer = 0;
            S.RestoreState(S.CaptureState()); _game.TravelTo("docks");
            Assert.AreEqual("docks", S.World.PlayerPlace); Assert.AreEqual("player", S.World.Items["smoked_fish"].Holder);
            Assert.IsFalse(S.World.Whelp.Hidden); Assert.AreEqual(0.45f, S.World.Whelp.Fear);
        }
        [UnityTearDown] public IEnumerator Restore()
        {
            _provider?.Release();
            try { yield return Until(() => _game == null || (!_game.Busy && !S.Busy)); }
            finally
            {
                DragonsleepGame.ProviderFactory = _factory; DsUiMotion.Reduced = _motion;
                if (_game != null) _game.Player.enabled = _playerEnabled;
            }
        }
        [UnityTest] public IEnumerator 两次真实喂鱼按钮以冻结过去裁决识别同物_不补库存不重复耗时()
        {
            Send(Feed, "把这串熏鱼喂给小烬"); yield return Until(() => Idle && S.Traces.Count == 1);
            var first = S.Traces.Single(); Assert.AreEqual("committed", first.KernelCode);
            Assert.IsTrue(first.Receipt.Applied); Assert.AreEqual(VerdictKind.Accepted, first.Verdict.Kind);
            Assert.AreEqual(DsSeededRoll.Instance.Next(109, 1), first.Verdict.Roll.Value);
            Assert.AreEqual(DsActionPerception.Current, Packet(0).ActionPerceptionVersion); Assert.IsEmpty(History(Packet(0)));
            string before = S.CaptureState().Json; int time = S.TimeLeftInPhase, items = S.World.Items.Count;
            Send(Feed, "再把刚才同一串熏鱼喂给小烬"); yield return Until(() => Idle && S.Traces.Count == 2);
            var second = S.Traces.Last(); var safeHistory = History(Packet(1)); Assert.AreEqual(1, safeHistory.Count);
            var past = (Dictionary<string, object>)safeHistory.Single();
            Assert.AreEqual(first.RequestId, past["request_id"]); Assert.AreEqual(first.Clock, past["at"]);
            Assert.AreEqual("feed", past["verb"]); Assert.AreEqual("whelp", past["target"]);
            Assert.AreEqual("smoked_fish", past["item"]); Assert.AreEqual("一串熏鱼", past["item_name"]);
            Assert.AreEqual("Accepted", past["result"]); Assert.AreEqual("consumed_by_this_action", past["item_outcome"]);
            Assert.IsFalse(((List<object>)Packet(1).TaskSnapshot.ToDictionary()["inventory"]).OfType<string>().Any(x => x.StartsWith("smoked_fish：")));
            Assert.AreEqual("smoked_fish", second.Proposal["item"]); Assert.AreEqual("item_missing", second.Verdict.ReasonCode.Code);
            Assert.AreEqual("committed", second.KernelCode); Assert.AreEqual("no_change", second.Receipt.Status);
            Assert.IsFalse(second.Receipt.Applied); Assert.AreEqual(0, second.Receipt.TimeCost); Assert.IsEmpty(second.Receipt.Effects);
            Assert.AreEqual(before, S.CaptureState().Json); Assert.AreEqual(time, S.TimeLeftInPhase);
            Assert.AreEqual(items, S.World.Items.Count); Assert.AreEqual("consumed", S.World.Items["smoked_fish"].Holder);
            Send(Observe, "观察四周"); yield return Until(() => Idle && S.Traces.Count == 3);
            Assert.AreEqual("Rejected", ((Dictionary<string, object>)History(Packet(2))[1])["result"]);
            Assert.AreEqual(1, History(Packet(1)).Count); Assert.IsEmpty(History(Packet(0)), "后续提交不得改写旧 packet");
        }
        [UnityTest] public IEnumerator 同JSON恢复清除记忆_有效TCS晚回经重开按钮不能回灌新局()
        {
            Send(Feed, "把这串熏鱼喂给小烬"); yield return Until(() => Idle && S.Traces.Count == 1);
            string id = S.Traces.Single().RequestId; var same = S.CaptureState(); var world = S.World;
            S.RestoreState(same); Assert.AreNotSame(world, S.World); Assert.AreEqual(same.Json, S.CaptureState().Json);
            Send(Observe, "观察四周"); yield return Until(() => Idle && S.Traces.Count == 1);
            Assert.IsEmpty(History(Packet(1))); Assert.AreEqual(id, S.Traces.Single().RequestId);
            Assert.AreEqual("committed", S.Traces.Single().KernelCode);
            _provider.Hold = true; Send(Observe, "再观察四周");
            yield return Until(() => _provider.Packets.Count == 3 && S.KernelActionInFlight && Ready("restartPendingAction"));
            var old = S.Traces.Last(); var safeHistory = History(Packet(2)); Assert.AreEqual(1, safeHistory.Count);
            Assert.AreEqual("observe", ((Dictionary<string, object>)safeHistory.Single())["verb"]);
            string frozen = MiniJson.Write(safeHistory); world = S.World; Click("restartPendingAction");
            Assert.AreNotSame(world, S.World); Assert.IsEmpty(S.Traces); Assert.IsTrue(_game.Busy && S.Busy);
            string fresh = S.CaptureState().Json, line = _root.Q<Label>("dialogueLine").text;
            _provider.Release(); yield return Until(() => Idle);
            Assert.AreEqual("stale_session", old.KernelCode); Assert.IsNull(old.Provider.Error);
            Assert.AreEqual(fresh, S.CaptureState().Json); Assert.IsEmpty(S.Traces); Assert.AreEqual(0, _game.ActionCommits);
            Assert.AreEqual(line, _root.Q<Label>("dialogueLine").text); Assert.AreEqual(frozen, MiniJson.Write(History(Packet(2))));
            Send(Observe, "新局观察四周"); yield return Until(() => Idle && S.Traces.Count == 1);
            Assert.IsEmpty(History(Packet(3))); Assert.AreEqual("committed", S.Traces.Single().KernelCode);
        }
    }
}
