using System;
using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class LoomLensHud
    {
        static readonly Color Midnight = new Color(0.045f, 0.085f, 0.11f, 0.95f);
        static readonly Color Edge = new Color(0.53f, 0.48f, 0.35f, 0.55f);
        Label _location, _connection;
        VisualElement _dock, _shortcuts;
        Button _sendButton;
        string _lastSubmitted, _lastQuest;
        float _conversationUntil;
        bool _wasBusy;
        Coroutine _typingLine;
        string _completeLine;

        void ApplyArtDirection()
        {
            _root.name = "dragonsleepHud";
            var plate = _root.Q("clockPlate");
            plate.style.backgroundImage = StyleKeyword.None;
            plate.style.backgroundColor = Midnight;
            plate.style.width = 330; plate.style.height = 112;
            plate.style.justifyContent = Justify.FlexStart; plate.style.paddingTop = 12;
            plate.style.left = 32; plate.style.top = 28;
            plate.style.alignItems = Align.FlexStart; plate.style.paddingLeft = 20;
            Border(plate, Edge, 1);
            _clock.style.fontSize = 22; _clock.style.color = Paper;
            _location = L("龙眠镇", 13, Gold); plate.Insert(0, _location);
            var under = _root.Q("clockUnder"); under.style.left = 40; under.style.top = 110;
            _coins.style.fontSize = 13;

            FlatPanel(_quest);
            _quest.style.left = 32; _quest.style.top = 160; _quest.style.width = 330;
            _quest.style.paddingLeft = 20; _quest.style.paddingRight = 20;
            _quest.style.paddingTop = 18; _quest.style.paddingBottom = 18;
            _quest.style.borderLeftWidth = 2; _quest.style.borderLeftColor = Gold;
            _questTitle.style.color = Paper; _questTitle.style.fontSize = 19;
            _questHint.style.color = new Color(0.76f, 0.79f, 0.78f); _questHint.style.fontSize = 15;
            _questHint.style.marginTop = 10;
            _questProgress.style.color = Gold; _questProgress.style.marginLeft = 10;
            _quest.ElementAt(_quest.childCount - 1).style.display = DisplayStyle.None;
            var heading = L("手 记   /   当前目标", 12, Gold); heading.style.marginBottom = 10; _quest.Insert(0, heading);

            _dock = _root.Q("conversationDock");
            _dock.style.left = new Length(23, LengthUnit.Percent); _dock.style.right = new Length(23, LengthUnit.Percent); _dock.style.bottom = 60;
            FlatPanel(_dialog);
            _dialog.name = "dialogueCard"; _line.name = "dialogueLine";
            _dialog.RegisterCallback<ClickEvent>(_ => FinishLine());
            _dialog.style.borderTopWidth = 2; _dialog.style.borderTopColor = Gold;
            _dialog.style.minHeight = 94;
            _speaker.style.fontSize = 15; _speaker.style.marginBottom = 6;
            _line.style.fontSize = 19;
            _line.text = "每一句话，都可能改变小镇的命运。";
            _portrait.style.width = 72; _portrait.style.height = 72;
            _portrait.ElementAt(1).style.width = 72; _portrait.ElementAt(1).style.height = 72;
            _portrait.ElementAt(1).style.unityBackgroundImageTintColor = Gold;
            var field = _root.Q("inputFrame"); FlatPanel(field); field.style.height = 48;
            Border(field, Edge, 1);
            _input.style.fontSize = 16;
            _sendButton = _root.Q<Button>("sendButton"); _sendButton.text = "织入  ↵";
            _sendButton.style.height = 48;
            foreach (var b in _modes) { b.style.width = 44; b.style.height = 48; }
            _nearby.style.color = Paper; _nearby.style.fontSize = 14; _nearby.style.marginTop = 10;
            _nearby.style.unityTextOutlineColor = Color.black; _nearby.style.unityTextOutlineWidth = 0.25f;
            _input.RegisterCallback<FocusInEvent>(_ => { field.style.borderBottomColor = Gold; field.style.borderBottomWidth = 2; });
            _input.RegisterCallback<FocusOutEvent>(_ => { field.style.borderBottomColor = Edge; field.style.borderBottomWidth = 1; });

            FlatPanel(_strip); _strip.style.width = 250; _strip.style.right = 32; _strip.style.top = 28;
            _strip.style.paddingTop = 12; _strip.style.paddingBottom = 12;
            _strip.ElementAt(0).style.display = DisplayStyle.None;
            _gems.style.display = DisplayStyle.None;
            _strip.ElementAt(_strip.childCount - 1).style.display = DisplayStyle.None;
            _connection = L(Game.Session.Provider.IsSimulated ? "◇  织言镜  /  离线预演" : "◇  织言镜  /  本局在线", 13, Gold);
            _strip.Insert(0, _connection);
            _stripLine.style.fontSize = 13; _stripLine.style.color = Muted; _stripLine.style.marginTop = 5;
            _stripLine.text = "Tab 查看话语留下的回响";
            FlatPanel(_lens); Border(_lens, Edge, 1);
            _lens.style.width = 560; _lens.style.bottom = 280; _lens.style.top = 28; _lens.style.right = 32;
            _lens.style.paddingTop = 24;

            _warning.style.backgroundImage = StyleKeyword.None; _warning.style.backgroundColor = Midnight;
            _warning.style.width = 520; _warning.style.height = 76; _warning.style.marginLeft = -260;
            _warning.style.top = 28; _warning.style.paddingTop = 0; _warning.style.paddingLeft = 22; _warning.style.paddingRight = 22;
            Border(_warning, Gold, 1);
            _toast.style.bottom = 260; Border(_toast, Edge, 1);
            _modal.style.backgroundColor = new Color(0.018f, 0.035f, 0.048f, 0.82f);

            _shortcuts = Row(); _shortcuts.name = "shortcuts";
            _shortcuts.style.position = Position.Absolute; _shortcuts.style.bottom = 16; _shortcuts.style.left = 32; _shortcuts.style.right = 32;
            _shortcuts.style.justifyContent = Justify.SpaceBetween;
            var links = Row();
            links.Add(SmallLink("J  手记", ShowJournal)); links.Add(SmallLink("M  地图", ShowMap)); links.Add(SmallLink("Tab  织言镜", ToggleLens));
            _shortcuts.Add(links);
            Button motion = null;
            motion = SmallLink(DsUiMotion.Reduced ? "动效：简约" : "动效：完整", () => { DsUiMotion.Reduced = !DsUiMotion.Reduced; motion.text = DsUiMotion.Reduced ? "动效：简约" : "动效：完整"; });
            _shortcuts.Add(motion); _root.Insert(_root.IndexOf(_modal), _shortcuts);
            _root.RegisterCallback<GeometryChangedEvent>(_ => FitLayout());
            SetMode(_mode);
            _conversationUntil = 0;
        }

        Button SmallLink(string text, Action action)
        {
            var b = SkinButton(text, action, null, 116, 34);
            b.style.fontSize = 13; b.style.marginRight = 8;
            return b;
        }

        void RefreshArtState()
        {
            _location.text = "龙眠镇  /  " + Game.PlaceName;
            bool busy = Game.Busy;
            _sendButton.SetEnabled(!busy && Game.Ending == null);
            _input.SetEnabled(!busy && Game.Ending == null);
            foreach (var b in _modes) b.SetEnabled(!busy && Game.Ending == null);
            _sendButton.text = busy ? "聆听中…" : "织入  ↵";
            _sendButton.style.opacity = busy ? 0.5f : 1f;
            if (busy) _stripLine.text = "丝线正在回应" + new string('·', 1 + (int)(Time.unscaledTime * 2) % 3);
            if (_wasBusy && !busy && _selected == null) _stripLine.text = "Tab 查看话语留下的回响";
            _wasBusy = busy;
            bool show = busy || Typing || Time.unscaledTime < _conversationUntil;
            _dialog.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (_lastQuest != _questTitle.text)
            {
                if (_lastQuest != null) EnterMotion(_quest);
                _lastQuest = _questTitle.text;
            }
        }

        static string QuestSummary(string id, string fallback)
        {
            switch (id)
            {
                case "find": return "与集市的玛尔妲交谈，打听她见过的异常。";
                case "food": return "在玛尔妲身边，用「行」买一串熏鱼。";
                case "pip": return "前往码头，向皮普表达你的善意。";
                case "whelp": return "用熏鱼和耐心，让小烬愿意靠近你。";
                case "hide": return "天黑前，想办法把小烬藏好。";
                default: return fallback;
            }
        }

        void FitLayout()
        {
            float width = _root.resolvedStyle.width, height = _root.resolvedStyle.height;
            bool compact = width < 1500;
            float side = compact ? 22 : 32;
            _dock.style.left = new Length(compact ? 15 : 23, LengthUnit.Percent);
            _dock.style.right = new Length(compact ? 15 : 23, LengthUnit.Percent);
            _quest.style.left = side; _quest.style.width = compact ? 290 : 330;
            _questHint.style.display = height < 760 ? DisplayStyle.None : DisplayStyle.Flex;
            _lens.style.width = Mathf.Min(560, width - 44); _lens.style.bottom = 280;
            _warning.style.top = width < 1450 ? 120 : 28;
            _root.Q("clockPlate").style.left = side;
        }

        void BuildIllustratedIntro()
        {
            _modal.Clear(); _modal.style.display = DisplayStyle.Flex;
            var card = new VisualElement { name = "introCard", style = { width = new Length(100, LengthUnit.Percent), height = new Length(100, LengthUnit.Percent), backgroundColor = Midnight } };
            var art = Resources.Load<Texture2D>("Dragonsleep/TitleArt");
            if (art != null) { card.style.backgroundImage = art; card.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover); }
            Border(card, Edge, 0);
            var wash = new VisualElement { pickingMode = PickingMode.Ignore, style = { position = Position.Absolute, left = 0, top = 0, right = 0, bottom = 0, backgroundColor = new Color(0.025f, 0.06f, 0.09f, 0.22f) } };
            card.Add(wash);
            var content = new VisualElement { style = { position = Position.Absolute, left = new Length(9, LengthUnit.Percent), top = new Length(10, LengthUnit.Percent), width = new Length(42, LengthUnit.Percent), bottom = new Length(10, LengthUnit.Percent), justifyContent = Justify.Center } };
            var over = L("DRAGONSLEEP   /   言 契 师 的 手 记", 13, Gold); over.style.letterSpacing = 3; content.Add(over);
            var title = L("龙眠镇", 90, Paper); title.style.marginTop = 16; title.style.letterSpacing = 10; content.Add(title);
            var titleFont = FontAssetForTitle();
            if (titleFont != null) title.style.unityFontDefinition = FontDefinition.FromSDFFont(titleFont);
            var rule = new VisualElement { style = { height = 2, width = 56, backgroundColor = Gold, marginTop = 18, marginBottom = 24 } }; content.Add(rule);
            var subtitle = L("在第三个黎明之前，\n替小镇写下另一种命运。", 28, Paper); subtitle.style.whiteSpace = WhiteSpace.Normal; content.Add(subtitle);
            var story = Wrap(L("龙蛋已经孵化。雏龙藏进了小镇，\n它的母亲正在雪峰上空等待。\n\n你是一位言契师。去倾听、探寻，\n让说出口的话，成为改变现实的丝线。", 20, new Color(0.79f, 0.83f, 0.82f))); story.style.marginTop = 20; content.Add(story);
            var begin = CardButton("启程   →", CloseModal); begin.name = "beginJourney"; begin.style.width = 210; begin.style.marginTop = 28; content.Add(begin);
            var keys = Wrap(L("WASD 移动 · 右键转镜头\nEnter 交谈 · J 手记 · M 地图", 12, Paper)); keys.style.marginTop = 12; content.Add(keys);
            card.Add(content); _modal.Add(card); EnterMotion(card);
            if (!DsUiMotion.Reduced)
            {
                title.style.opacity = 0; begin.style.opacity = 0;
                title.schedule.Execute(() => EnterMotion(title)).StartingIn(80);
                begin.schedule.Execute(() => EnterMotion(begin)).StartingIn(170);
            }
        }

        void AnimateLine(string text)
        {
            FinishLine(); _completeLine = text ?? "";
            if (DsUiMotion.Reduced) { _line.text = _completeLine; return; }
            _typingLine = StartCoroutine(TypeLine());
        }

        IEnumerator TypeLine()
        {
            var glyphs = StringInfo.ParseCombiningCharacters(_completeLine);
            for (int i = 1; i < glyphs.Length && !DsUiMotion.Reduced; i++)
            {
                _line.text = _completeLine.Substring(0, glyphs[i]);
                yield return new WaitForSecondsRealtime(0.025f);
            }
            _line.text = _completeLine; _typingLine = null;
        }

        void FinishLine()
        {
            if (_typingLine == null) return;
            StopCoroutine(_typingLine); _typingLine = null; _line.text = _completeLine;
        }

        static UnityEngine.TextCore.Text.FontAsset _titleFont;
        static UnityEngine.TextCore.Text.FontAsset FontAssetForTitle()
        {
            if (_titleFont == null) _titleFont = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset("Songti SC", "Regular", 90);
            return _titleFont;
        }

        static void FlatPanel(VisualElement e)
        {
            e.style.backgroundImage = StyleKeyword.None; e.style.backgroundColor = Midnight;
            e.style.borderTopLeftRadius = 3; e.style.borderTopRightRadius = 3;
            e.style.borderBottomLeftRadius = 3; e.style.borderBottomRightRadius = 3;
        }

        static void Border(VisualElement e, Color color, float width)
        {
            e.style.borderTopWidth = width; e.style.borderBottomWidth = width; e.style.borderLeftWidth = width; e.style.borderRightWidth = width;
            e.style.borderTopColor = color; e.style.borderBottomColor = color; e.style.borderLeftColor = color; e.style.borderRightColor = color;
        }

        static void DressButton(Button b, bool primary = false)
        {
            b.style.backgroundImage = StyleKeyword.None; b.style.backgroundColor = primary ? Gold : Midnight;
            b.style.color = primary ? Ink : Paper; Border(b, Edge, 1);
            b.style.borderTopLeftRadius = 2; b.style.borderTopRightRadius = 2; b.style.borderBottomLeftRadius = 2; b.style.borderBottomRightRadius = 2;
            b.RegisterCallback<PointerEnterEvent>(_ => { b.style.borderTopColor = Gold; b.style.borderLeftColor = Gold; b.style.borderRightColor = Gold; b.style.color = primary ? Ink : Gold; });
            b.RegisterCallback<PointerLeaveEvent>(_ => { b.style.borderTopColor = Edge; b.style.borderLeftColor = Edge; b.style.borderRightColor = Edge; b.style.color = primary ? Ink : Paper; b.style.translate = new Translate(0, 0); });
            b.RegisterCallback<PointerDownEvent>(_ => { if (!DsUiMotion.Reduced) b.style.translate = new Translate(0, 1); });
            b.RegisterCallback<PointerUpEvent>(_ => b.style.translate = new Translate(0, 0));
            b.RegisterCallback<FocusInEvent>(_ => b.style.borderTopColor = Gold);
            b.RegisterCallback<FocusOutEvent>(_ => b.style.borderTopColor = Edge);
        }

        static void EnterMotion(VisualElement e) => DsUiMotion.Enter(e);
    }

    /// <summary>短时进入反馈；使用 UI 调度器，不受游戏暂停影响，不常驻分配动画协程。</summary>
    public static class DsUiMotion
    {
        const string Preference = "Dragonsleep.ReduceMotion";
        public static bool Reduced { get => PlayerPrefs.GetInt(Preference, 0) == 1; set => PlayerPrefs.SetInt(Preference, value ? 1 : 0); }
        public static void Enter(VisualElement e)
        {
            if (Reduced) { e.style.opacity = 1; e.style.translate = new Translate(0, 0); return; }
            float start = Time.unscaledTime;
            e.style.opacity = 0; e.style.translate = new Translate(0, 10);
            IVisualElementScheduledItem job = null;
            job = e.schedule.Execute(() =>
            {
                float t = Mathf.Clamp01((Time.unscaledTime - start) / 0.22f);
                float eased = 1f - Mathf.Pow(1f - t, 3f);
                e.style.opacity = eased; e.style.translate = new Translate(0, 10f * (1f - eased));
                if (t >= 1 || Reduced) { e.style.opacity = 1; e.style.translate = new Translate(0, 0); job.Pause(); }
            }).Every(16);
        }
    }
}
