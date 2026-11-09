using System;
using System.Collections;
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
    /// <summary>真实主场景 UI 事件验收。所有提供方明确离线，不读取密钥。</summary>
    public sealed class DragonsleepPresentationTests
    {
        DragonsleepGame _game;
        VisualElement _root;
        Func<IAiProvider> _original;
        bool _motion;

        [UnitySetUp]
        public IEnumerator Load()
        {
            _original = DragonsleepGame.ProviderFactory;
            _motion = DsUiMotion.Reduced;
            DragonsleepGame.ProviderFactory = () => new RuleProvider();
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity", new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
            yield return null; yield return null;
            _game = UnityEngine.Object.FindFirstObjectByType<DragonsleepGame>();
            Assert.IsNotNull(_game);
            _root = _game.GetComponent<UIDocument>().rootVisualElement;
            Assert.IsTrue(_game.Session.Provider.IsSimulated);
        }

        [TearDown]
        public void Restore() { DragonsleepGame.ProviderFactory = _original; DsUiMotion.Reduced = _motion; }

        static void Activate(Button button)
        {
            Assert.IsNotNull(button);
            button.Focus();
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }

        void Begin() => Activate(_root.Q<Button>("beginJourney"));

        [UnityTest]
        public IEnumerator 开场与手记按钮实际可用_阅读时阻断3C_减少动效可切换()
        {
            Assert.IsNotNull(Resources.Load<Texture2D>("Dragonsleep/TitleArt"));
            Assert.IsTrue(_game.Hud.ModalOpen);
            Assert.IsTrue(_game.Player.InputBlocked);
            Begin(); yield return null; yield return null;
            Assert.IsFalse(_game.Hud.ModalOpen);
            Assert.IsFalse(_game.Player.InputBlocked);
            var journal = _root.Query<Button>().ToList().First(b => b.text == "J  手记");
            Activate(journal); yield return null; yield return null;
            Assert.IsTrue(_game.Hud.ModalOpen);
            Assert.IsTrue(_game.Player.InputBlocked);
            Assert.IsNotNull(_root.Q("storyCard"));
            Activate(_root.Query<Button>().ToList().First(b => b.text == "合上"));
            yield return null;
            var motion = _root.Query<Button>().ToList().First(b => b.text.StartsWith("动效："));
            Activate(motion);
            Assert.AreNotEqual(_motion, DsUiMotion.Reduced);
        }

        [UnityTest]
        public IEnumerator 对话输入按钮贯通规则_得到线索且更新当前目标()
        {
            Begin();
            _game.Player.Teleport(_game.ActorTransforms["marta"].position + Vector3.forward * 2f);
            yield return null; yield return null;
            Assert.AreEqual("marta", _game.NearestActorId);
            _root.Q<TextField>().value = "最近有没有见到冒烟的东西？";
            Activate(_root.Q<Button>("sendButton"));
            yield return null; yield return null;
            Assert.IsTrue(_game.Session.World.PlayerKnows.Contains("b_marta_saw_pip"));
            Assert.AreEqual(1, _game.Session.Traces.Count);
            Assert.IsTrue(_root.Q("questCard").Query<Label>().ToList().Any(l => l.text.Contains("准备雏龙爱吃的东西")));
            // 对话逐字出现；点击卡片可以立即看完，避免等待妨碍阅读。
            using (var click = ClickEvent.GetPooled()) { var card = _root.Q("dialogueCard"); click.target = card; card.SendEvent(click); }
            yield return null;
            Assert.IsTrue(_root.Q("conversationDock").Query<Label>().ToList().Any(l => l.text.Contains("皮普")));
        }

        sealed class DelayedFailure : IAiProvider
        {
            public string Name => "offline-delayed-fixture";
            public bool IsSimulated => true;
            public int Calls;
            public readonly TaskCompletionSource<ProviderResult> Pending = new TaskCompletionSource<ProviderResult>();
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken cancellationToken) { Calls++; return Pending.Task; }
        }

        [UnityTest]
        public IEnumerator 织造图样可取消和确认_取消不扣料_确认后才生成物件()
        {
            Begin();
            Assert.IsFalse(_game.Whelp.gameObject.activeSelf,"藏起的雏龙不能在初始场景中显形");
            _game.TravelTo("workshop");
            yield return null; yield return null;
            Assert.AreEqual("workshop",_game.Session.World.PlayerPlace);
            Activate(_root.Query<Button>().ToList().First(b=>b.text=="造"));
            _root.Q<TextField>().value="用蓝晶石织一盏灯";
            Activate(_root.Q<Button>("sendButton"));
            yield return null; yield return null;
            Assert.IsTrue(_game.Session.HasPendingCraft);
            Assert.IsNotNull(_root.Q("craftProposal"));
            Assert.AreNotEqual("consumed",_game.Session.World.Items["crystal_blue"].Holder);
            Activate(_root.Q<Button>("declineCraft"));
            yield return null; yield return null;
            Assert.IsFalse(_game.Session.HasPendingCraft);
            Assert.IsFalse(_game.Session.World.Items.Values.Any(i=>i.Crafted));
            Assert.AreEqual(4,_game.Session.TimeLeftInPhase);
            _root.Q<TextField>().value="用蓝晶石织一盏灯";
            Activate(_root.Q<Button>("sendButton"));
            yield return null; yield return null;
            Activate(_root.Q<Button>("confirmCraft"));
            yield return null; yield return null;
            Assert.IsFalse(_game.Session.HasPendingCraft);
            Assert.AreEqual("consumed",_game.Session.World.Items["crystal_blue"].Holder);
            Assert.AreEqual(1,_game.Session.World.Items.Values.Count(i=>i.Crafted));
            Assert.AreEqual(2,_game.Session.Traces.Count,"确认不增加模型调用");
        }

        [UnityTest]
        public IEnumerator 等待时锁定发送与移动_失败保留输入且不自动重试()
        {
            Begin();
            _game.Player.Teleport(_game.ActorTransforms["marta"].position + Vector3.forward * 2f);
            yield return null; yield return null;
            var provider = new DelayedFailure(); _game.Session.Provider = provider;
            var field = _root.Q<TextField>(); field.value = "这是需要保留的一句话";
            var send = _root.Q<Button>("sendButton"); Activate(send);
            yield return null; yield return null;
            Assert.IsTrue(_game.Busy);
            Assert.IsTrue(_game.Player.InputBlocked);
            Assert.IsFalse(send.enabledSelf);
            provider.Pending.SetResult(new ProviderResult { ProviderName = provider.Name, Error = "timeout", Simulated = true });
            float deadline = Time.realtimeSinceStartup + 3;
            while (_game.Busy && Time.realtimeSinceStartup < deadline) yield return null;
            yield return null;
            Assert.IsFalse(_game.Busy);
            Assert.AreEqual("这是需要保留的一句话", field.value);
            Assert.AreEqual(1, provider.Calls);
            Assert.AreEqual(4, _game.Session.TimeLeftInPhase);
            Assert.IsTrue(send.enabledSelf);
        }
    }
}
