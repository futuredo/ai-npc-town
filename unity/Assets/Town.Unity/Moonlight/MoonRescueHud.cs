using System.Linq;
using Town.Core.Moonlight;
using UnityEngine;
using UnityEngine.UIElements;

namespace Town.Unity.Moonlight
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class MoonRescueHud : MonoBehaviour
    {
        public MoonRescueGame game;
        public Texture2D paperTexture;
        public Texture2D buttonTexture;
        public Texture2D titleTexture;
        public bool immersive;
        public MoonPlayerInteraction interaction;
        public bool IsModalOpen { get; private set; }
        ScrollView _panel;
        Label _compactObjective, _interactionPrompt, _controls;
        bool _pauseBeforeModal;
        Label _objective, _status, _feedback, _history;
        TextField _method;
        Button _propose, _confirm, _finish, _light, _occluder, _pause, _restart, _follow, _split;
        bool _finishArmed, _restartArmed;
        int _generation;
        string _fixture = "follow";
        Font _font;

        void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            root.Clear(); root.pickingMode = PickingMode.Ignore;
            _font = Font.CreateDynamicFontFromOSFont(new[] { "PingFang SC", "Arial Unicode MS", "Arial" }, 18);
            root.style.unityFontDefinition = FontDefinition.FromFont(_font);
            var panel = new ScrollView { name = "rescue-panel" };
            panel.style.position = Position.Absolute; panel.style.right = 18; panel.style.top = 18; panel.style.bottom = 18;
            panel.style.width = new Length(32, LengthUnit.Percent); panel.style.minWidth = 310; panel.style.maxWidth = 460;
            panel.style.paddingLeft = panel.style.paddingRight = 20; panel.style.paddingTop = panel.style.paddingBottom = 18;
            panel.style.backgroundColor = new Color(0.07f, 0.13f, 0.18f, 0.98f);
            panel.style.borderTopWidth = panel.style.borderBottomWidth = panel.style.borderLeftWidth = panel.style.borderRightWidth = 1;
            panel.style.borderTopColor = panel.style.borderBottomColor = panel.style.borderLeftColor = panel.style.borderRightColor = new Color(0.7f, 0.58f, 0.32f);
            root.Add(panel);
            _panel = panel;
            if (immersive)
            {
                panel.style.width = 420;
                _compactObjective = AddLabel(root, "", 18, new Color(1, .91f, .68f));
                _compactObjective.style.position = Position.Absolute; _compactObjective.style.left = 28; _compactObjective.style.top = 24;
                _compactObjective.style.maxWidth = new Length(45, LengthUnit.Percent);
                _compactObjective.style.backgroundColor = new Color(.035f, .09f, .12f, .82f);
                _compactObjective.style.paddingLeft = _compactObjective.style.paddingRight = 14;
                _compactObjective.style.paddingTop = _compactObjective.style.paddingBottom = 10;
                _compactObjective.pickingMode = PickingMode.Ignore;
                _interactionPrompt = AddLabel(root, "", 19, Color.white);
                _interactionPrompt.style.position = Position.Absolute; _interactionPrompt.style.bottom = 80;
                _interactionPrompt.style.left = new Length(38, LengthUnit.Percent); _interactionPrompt.pickingMode = PickingMode.Ignore;
                _interactionPrompt.style.backgroundColor = new Color(.035f, .09f, .12f, .88f);
                _interactionPrompt.style.paddingLeft = _interactionPrompt.style.paddingRight = 12;
                _interactionPrompt.style.paddingTop = _interactionPrompt.style.paddingBottom = 6;
                _controls = AddLabel(root, "WASD 移动   Shift 快走   右键转镜头   滚轮远近   E 互动   Esc 返回", 13, Color.white);
                _controls.style.position = Position.Absolute; _controls.style.bottom = 16; _controls.style.left = 28;
                _controls.pickingMode = PickingMode.Ignore;
                _controls.style.backgroundColor = new Color(.035f, .09f, .12f, .82f);
                _controls.style.paddingLeft = _controls.style.paddingRight = 10;
                panel.style.display = DisplayStyle.None;
                AddButton(panel, "返回场景 · Esc", () => SetModal(false));
            }
            AddLabel(panel, "未竟之镇 / 借光驿站", 14, new Color(0.55f, 0.8f, 0.8f));
            var title = AddLabel(panel, "借一轮小月亮", 28, new Color(1, 0.85f, 0.51f));
            if (titleTexture) title.style.backgroundImage = new StyleBackground(titleTexture);
            title.style.paddingTop = title.style.paddingBottom = 8;
            _objective = AddLabel(panel, "", 16, new Color(0.12f, 0.19f, 0.2f));
            if (paperTexture) _objective.style.backgroundImage = new StyleBackground(paperTexture);
            _objective.style.backgroundColor = new Color(0.83f, 0.85f, 0.76f);
            _objective.style.paddingLeft = _objective.style.paddingRight = 12;
            _objective.style.paddingTop = _objective.style.paddingBottom = 12;
            var note = AddLabel(panel, "研究模式 · 人工候选，不会解析自由输入\n0 网络请求；真实 AI 尚未开启", 12, new Color(0.7f, 0.82f, 0.86f));
            note.style.marginBottom = 14;
            AddLabel(panel, "① 写下想尝试的方法", 17, Color.white);
            _method = new TextField { multiline = true, maxLength = 500, value = "让小月亮带路，大家跟着它走，光灭就停。", name = "method" };
            _method.style.minHeight = 72; _method.style.marginBottom = 8;
            panel.Add(_method);
            AddLabel(panel, "② 选择离线候选（测试接缝，不冒充 AI）", 13, new Color(0.65f, 0.8f, 0.85f));
            _follow = AddButton(panel, "下路：跟随小月亮", () => { _fixture = "follow"; _method.value = "让小月亮沿下路引路，持续照着你们。"; });
            _split = AddButton(panel, "上路：两束光照住符文", () => { _fixture = "split"; _method.value = "分出两束月光，试亮两秒后从上路过去。"; });
            _follow.name = "route-follow"; _split.name = "route-split";
            _propose = AddButton(panel, "听芬恩的条件", () => game.execution.SubmitFixture(_fixture, _method.value));
            _confirm = AddButton(panel, "确认借光，出发", () => { if (game.ConfirmDeparture() && immersive) SetModal(false); });
            _propose.name = "propose"; _confirm.name = "depart";
            _status = AddLabel(panel, "", 16, new Color(1, 0.85f, 0.51f));
            _feedback = AddLabel(panel, "", 14, Color.white);
            _light = AddButton(panel, "切断月光", () => game.execution.ToggleLight()); _light.name = "light";
            _occluder = AddButton(panel, "放下遮光屏", () => game.execution.ToggleOccluder());
            _pause = AddButton(panel, "暂停护送", () => game.execution.Paused = !game.execution.Paused); _pause.name = "pause";
            if (immersive) _pause.style.display = DisplayStyle.None; // 查看条件本身已暂停，不提供会解除阅读保护的第二开关。
            _finish = AddButton(panel, "提前收束这一页", () =>
            {
                if (!_finishArmed) { _finishArmed = true; return; }
                game.FinishNow(); _finishArmed = false;
            });
            _finish.name = "finish";
            _restart = AddButton(panel, "重开这一页", () =>
            {
                if (game.Chapter.Phase == RescuePhase.Escort && !_restartArmed) { _restartArmed = true; return; }
                game.execution.ResetExperiment(); _restartArmed = false;
            });
            _restart.name = "restart";
            AddLabel(panel, "中断不等于失败：接回月光即可继续。提前收束与途中重开需要再次确认。", 12, new Color(0.7f, 0.82f, 0.86f));
            _history = AddLabel(panel, "", 12, new Color(0.65f, 0.8f, 0.85f));
        }

        Label AddLabel(VisualElement parent, string text, int size, Color color)
        {
            var label = new Label(text); label.style.whiteSpace = WhiteSpace.Normal; label.style.fontSize = size;
            label.style.color = color; label.style.marginBottom = 10; parent.Add(label); return label;
        }

        Button AddButton(VisualElement parent, string text, System.Action action)
        {
            var button = new Button(action) { text = text };
            button.style.minHeight = 36; button.style.marginBottom = 6; button.style.fontSize = 15;
            button.style.color = new Color(0.96f, 0.9f, 0.72f); button.style.backgroundColor = new Color(0.14f, 0.25f, 0.3f);
            if (buttonTexture) button.style.backgroundImage = new StyleBackground(buttonTexture);
            parent.Add(button); return button;
        }

        void Update()
        {
            if (!game || game.execution.Workshop == null) return;
            var workshop = game.execution.Workshop;
            if (_generation != workshop.Generation)
            {
                _generation = workshop.Generation; _finishArmed = false; _restartArmed = false;
                // 新局的暂停状态由新局自己决定，不能恢复旧对话打开前的暂停。
                if (IsModalOpen) { IsModalOpen = false; _panel.style.display = DisplayStyle.None; _method.Blur(); }
            }
            if (immersive)
            {
                _compactObjective.text = game.Chapter.Phase == RescuePhase.Settled ? game.Chapter.Ending : "借一轮小月亮\n" + game.Chapter.Objective;
                string cue = interaction && interaction.Current ? "E  " + interaction.Current.label : "";
                if (string.IsNullOrEmpty(cue) && interaction && interaction.player && !IsModalOpen)
                {
                    float distance = Vector3.Distance(interaction.player.transform.position, game.execution.finn.transform.position);
                    cue = "走近芬恩，按 E 商量或查看本页 · " + distance.ToString("0") + " 米";
                }
                _interactionPrompt.text = IsModalOpen ? "" : cue;
                _interactionPrompt.style.display = string.IsNullOrEmpty(_interactionPrompt.text) ? DisplayStyle.None : DisplayStyle.Flex;
            }
            _objective.text = game.Chapter.Objective;
            _status.text = $"芬恩：{(game.Chapter.FinnSafe ? "已到月台" : "尚未抵达")}  ·  灯：{game.Chapter.SavedLights}/3\n可用月焰：{workshop.FreeEnergy}/2  ·  光路：{(game.execution.LightVerified ? "畅通" : "中断")}";
            string next = game.Chapter.Phase == RescuePhase.Settled ? "这一页已收束。可以重开，比较另一条路线。" :
                workshop.State == MoonTaskState.AwaitingConfirmation ? "芬恩已提出条件。确认后才会预留月焰并出发。" :
                game.execution.Paused ? "护送已暂停；按「继续护送」恢复。" :
                game.Chapter.Phase == RescuePhase.Escort && !game.execution.LightVerified ? "光路中断：接通月光或移开遮光屏，队伍会原地等待。" :
                game.Chapter.Phase == RescuePhase.Escort && !workshop.CanMove && workshop.Agreement?.Method == LightMethod.SplitBeam ? "两枚符文需要连续试亮两秒；中断后重新计时。" : game.execution.Feedback;
            _feedback.text = workshop.Agreement?.Summary + "\n" + next;
            _history.text = string.Join("\n", game.Chapter.Events.Reverse().Take(3));
            _propose.SetEnabled(workshop.State == MoonTaskState.None);
            _confirm.SetEnabled(workshop.State == MoonTaskState.AwaitingConfirmation);
            _finish.SetEnabled(game.Chapter.Phase == RescuePhase.Escort);
            _method.SetEnabled(workshop.State == MoonTaskState.None);
            _follow.SetEnabled(workshop.State == MoonTaskState.None); _split.SetEnabled(workshop.State == MoonTaskState.None);
            _light.text = workshop.LightOn ? "切断月光" : "接通月光，恢复护送";
            _occluder.text = game.execution.beamBlocker.activeSelf ? "移开遮光屏" : "放下遮光屏";
            _pause.text = game.execution.Paused ? "继续护送" : "暂停护送";
            _pause.SetEnabled(game.Chapter.Phase == RescuePhase.Escort);
            _light.SetEnabled(game.Chapter.Phase != RescuePhase.Settled); _occluder.SetEnabled(game.Chapter.Phase != RescuePhase.Settled);
            _finish.text = _finishArmed ? "再次确认：按已抵达人数提前收束" : "提前收束这一页";
            _restart.text = _restartArmed ? "再次确认：放弃本页进度并重开" : "重开这一页";
        }

        public void SetModal(bool open)
        {
            if (!immersive || _panel == null || IsModalOpen == open) return;
            if (open) { _pauseBeforeModal = game.execution.Paused; game.execution.Paused = true; }
            else { game.execution.Paused = _pauseBeforeModal; _panel.panel?.focusController?.focusedElement?.Blur(); }
            IsModalOpen = open; _panel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void OnDisable() { if (IsModalOpen && game && game.execution) SetModal(false); }
        void OnDestroy() { if (_font) Destroy(_font); }
    }
}
