using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Town.Core.Dragonsleep;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    /// <summary>
    /// 龙眠镇界面（Medieval Kingdom UI 白金皮肤）。默认只显示四样东西：
    /// 时间牌、当前目标、对话框、织言条（七颗宝石逐个点亮 = 一次 AI 调用的七个阶段）。
    /// Tab 展开完整织言镜（研究视图），J 日志，M 地图。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed partial class LoomLensHud : MonoBehaviour
    {
        public DragonsleepGame Game;
        public DsUiSkin Skin;
        public float StageRevealSeconds = 0.12f;

        static readonly Color Ink = new Color(0.16f, 0.20f, 0.22f);
        static readonly Color InkSoft = new Color(0.32f, 0.35f, 0.35f);
        static readonly Color Paper = new Color(0.96f, 0.92f, 0.82f);
        static readonly Color Gold = new Color(0.80f, 0.68f, 0.43f);
        static readonly Color Muted = new Color(0.72f, 0.68f, 0.60f);
        static readonly Color Shade = new Color(0.04f, 0.04f, 0.06f, 0.82f);

        VisualElement _root, _plates, _quest, _dialog, _portrait, _strip, _gems, _lens, _timeline, _detail, _stageRow, _modal, _warning, _toast, _pips;
        Label _clock, _coins, _questTitle, _questHint, _questProgress, _speaker, _line, _stripLine, _nearby, _metrics;
        TextField _input;
        Button[] _modes;
        LoomMode _mode = LoomMode.Speak;
        readonly Dictionary<string, Label> _plateLabels = new Dictionary<string, Label>();
        readonly Dictionary<AiTrace, VisualElement> _rows = new Dictionary<AiTrace, VisualElement>();
        AiTrace _selected;
        int _revealed = 7;
        Coroutine _reveal;
        string _lastWarning;

        public bool Typing
        {
            get
            {
                var f = _input?.focusController?.focusedElement as VisualElement;
                return f != null && ((f == _input || _input.Contains(f)) || (_planGoal != null && (f == _planGoal || _planGoal.Contains(f))));
            }
        }

        public bool ReadingPanelOpen => ModalOpen || (_lens != null && _lens.style.display == DisplayStyle.Flex);

        public bool ModalOpen => _modal != null && _modal.style.display == DisplayStyle.Flex;

        bool _started;
        void Start() { _started = true; BuildDocument(); }
        void OnEnable() { if (_started) BuildDocument(); }
        void OnDisable()
        {
            StopAllCoroutines(); _reveal = null; _typingLine = null;
            // 先卸下文字元素，避免退出 Play / 卸载场景时排队的绘制使用已释放的字体材质。
            _root?.Clear(); _plateLabels.Clear(); _rows.Clear(); _selected = null;
        }

        void BuildDocument()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _root.Clear();
            _root.style.flexGrow = 1;
            ApplyFont(_root);
            Build();
            ShowIntro();
        }

        static FontAsset _font;
        static void ApplyFont(VisualElement root)
        {
            if (_font == null)
                foreach (var face in new[]
                {
                    ("Hiragino Sans GB", "W3"), ("PingFang SC", "Regular"), ("Microsoft YaHei", "Regular"),
                    ("Noto Sans CJK SC", "Regular"), ("Songti SC", "Regular")
                })
                {
                    _font = FontAsset.CreateFontAsset(face.Item1, face.Item2, 64);
                    if (_font != null) break;
                }
            if (_font != null) root.style.unityFontDefinition = FontDefinition.FromSDFFont(_font);
        }

        // ================= 构建 =================

        void Build()
        {
            _plates = new VisualElement
            {
                pickingMode = PickingMode.Ignore, style = { position = Position.Absolute, left = 0, right = 0, top = 0, bottom = 0 }
            };
            _root.Add(_plates);

            // ---- 左上：时间牌 ----
            var plate = Img(Skin != null ? Skin.TitlePlate : null, 380, 75);
            plate.name = "clockPlate";
            plate.style.position = Position.Absolute; plate.style.left = 22; plate.style.top = 18;
            plate.style.alignItems = Align.Center; plate.style.justifyContent = Justify.Center;
            _clock = L("", 21, Paper, true);
            plate.Add(_clock);
            _root.Add(plate);
            var under = new VisualElement
            {
                style = { position = Position.Absolute, left = 44, top = 98, flexDirection = FlexDirection.Row, alignItems = Align.Center }
            };
            _pips = Row();
            under.name = "clockUnder";
            under.Add(_pips);
            _coins = L("", 15, Gold);
            _coins.style.marginLeft = 14;
            _coins.style.unityTextOutlineColor = new Color(0, 0, 0, 0.95f); _coins.style.unityTextOutlineWidth = 0.3f;
            under.Add(_coins);
            _root.Add(under);

            // ---- 左：当前目标（羊皮纸） ----
            _quest = Sliced(Skin != null ? Skin.Parchment : null, 0.07f, 0.12f, 14);
            _quest.style.position = Position.Absolute; _quest.style.left = 22; _quest.style.top = 134; _quest.style.width = 380;
            _quest.style.paddingLeft = 26; _quest.style.paddingRight = 24; _quest.style.paddingTop = 18; _quest.style.paddingBottom = 16;
            _quest.name = "questCard";
            var qhead = Row(); qhead.style.justifyContent = Justify.SpaceBetween;
            _questTitle = Wrap(L("", 17, Ink, true)); _questTitle.style.flexShrink = 1;
            qhead.Add(_questTitle);
            _questProgress = L("", 13, InkSoft);
            qhead.Add(_questProgress);
            _quest.Add(qhead);
            _questHint = Wrap(L("", 15, Ink));
            _questHint.style.marginTop = 6;
            _quest.Add(_questHint);
            _quest.Add(Tiny("J 日志 · M 地图 · Tab 织言镜", InkSoft));
            _root.Add(_quest);

            // ---- 底部：对话框 ----
            var bottom = new VisualElement
            {
                style = { position = Position.Absolute, left = new Length(20, LengthUnit.Percent),
                    right = new Length(20, LengthUnit.Percent), bottom = 20 }
            };
            bottom.name = "conversationDock";
            _dialog = Sliced(Skin != null ? Skin.DarkPanel : null, 0.06f, 0.06f, 14);
            _dialog.style.flexDirection = FlexDirection.Row; _dialog.style.alignItems = Align.Center;
            _dialog.style.paddingLeft = 18; _dialog.style.paddingRight = 22; _dialog.style.paddingTop = 14; _dialog.style.paddingBottom = 14;
            _dialog.style.minHeight = 110;
            _portrait = new VisualElement { style = { width = 92, height = 92, marginRight = 16, flexShrink = 0 } };
            var portraitImg = new VisualElement { name = "img", style = { position = Position.Absolute, left = 9, top = 9, right = 9, bottom = 9,
                borderTopLeftRadius = 40, borderTopRightRadius = 40, borderBottomLeftRadius = 40, borderBottomRightRadius = 40, overflow = Overflow.Hidden } };
            _portrait.Add(portraitImg);
            var ring = Img(Skin != null ? Skin.RoundFrame : null, 92, 92);
            ring.style.position = Position.Absolute;
            _portrait.Add(ring);
            _portrait.style.display = DisplayStyle.None;
            _dialog.Add(_portrait);
            var textCol = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
            _speaker = L("", 16, Gold, true);
            textCol.Add(_speaker);
            _line = Wrap(L("走近一位居民，按 Enter 和他说话；身边没人时，Enter 可以描述任何你想做的事。", 18, Paper));
            textCol.Add(_line);
            _dialog.Add(textCol);
            bottom.Add(_dialog);

            var inputRow = Row();
            inputRow.name = "inputRow";
            inputRow.style.marginTop = 8; inputRow.style.alignItems = Align.Center;
            _modes = new[] { ModeButton("言", LoomMode.Speak), ModeButton("行", LoomMode.Act), ModeButton("造", LoomMode.Craft) };
            foreach (var b in _modes) inputRow.Add(b);
            var field = Sliced(Skin != null ? Skin.NavyBar : null, 0.02f, 0.1f, 6);
            field.name = "inputFrame";
            field.style.flexGrow = 1; field.style.height = 46; field.style.marginLeft = 8;
            field.style.justifyContent = Justify.Center; field.style.paddingLeft = 10; field.style.paddingRight = 10;
            _input = new TextField { maxLength = 300 };
            StyleInput(_input);
            _input.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { Send(); e.StopPropagation(); }
                if (e.keyCode == KeyCode.Escape) _input.Blur();
            }, TrickleDown.TrickleDown);
            field.Add(_input);
            inputRow.Add(field);
            var send = SkinButton("织 入", Send, Skin != null ? Skin.ButtonRed : null, 110, 46);
            send.name = "sendButton";
            send.style.marginLeft = 8;
            inputRow.Add(send);
            bottom.Add(inputRow);
            _nearby = L("", 14, Gold);
            _nearby.style.marginTop = 4; _nearby.style.unityTextAlign = TextAnchor.MiddleCenter;
            bottom.Add(_nearby);
            _root.Add(bottom);

            // ---- 右上：织言条（常驻、极简） ----
            _strip = Sliced(Skin != null ? Skin.NavyBar : null, 0.02f, 0.12f, 8);
            _strip.style.position = Position.Absolute; _strip.style.right = 22; _strip.style.top = 22; _strip.style.width = 330;
            _strip.style.paddingLeft = 18; _strip.style.paddingRight = 18; _strip.style.paddingTop = 12; _strip.style.paddingBottom = 12;
            var shead = Row(); shead.style.justifyContent = Justify.SpaceBetween;
            shead.Add(L("织言机", 16, Gold, true));
            bool sim = Game.Session.Provider.IsSimulated;
            shead.Add(Tiny(sim ? "离线·模拟" : Game.Session.Provider.Name.Split('（')[0], sim ? new Color(0.6f, 0.82f, 1f) : new Color(1f, 0.65f, 0.45f)));
            _strip.Add(shead);
            _gems = Row(); _gems.style.marginTop = 10; _gems.style.marginBottom = 8; _gems.style.marginLeft = 4;
            _strip.Add(_gems);
            _stripLine = Wrap(L("尚无调用。说一句话，看织言机如何理解它。", 15, Paper));
            _strip.Add(_stripLine);
            _strip.Add(Tiny("Tab 展开完整管线", Muted));
            _strip.RegisterCallback<ClickEvent>(_ => ToggleLens());
            _root.Add(_strip);
            RenderGems(null);

            // ---- 织言镜（Tab 展开） ----
            _lens = Sliced(Skin != null ? Skin.NavyWindow : null, 0.03f, 0.06f, 22);
            _lens.style.position = Position.Absolute; _lens.style.right = 18; _lens.style.top = 18; _lens.style.bottom = 200; _lens.style.width = 560;
            _lens.style.backgroundColor = new Color(0.05f, 0.06f, 0.10f, 0.94f);
            _lens.style.paddingLeft = 24; _lens.style.paddingRight = 24; _lens.style.paddingTop = 34; _lens.style.paddingBottom = 18;
            _lens.style.display = DisplayStyle.None;
            var lhead = Row(); lhead.style.justifyContent = Justify.SpaceBetween; lhead.style.alignItems = Align.Center;
            lhead.Add(L("织言镜 · AI 管线", 20, Gold, true));
            lhead.Add(SkinButton("收起", ToggleLens, Skin != null ? Skin.ButtonDark : null, 90, 36));
            _lens.Add(lhead);
            _metrics = Wrap(L("", 12, Muted));
            _lens.Add(_metrics);
            _timeline = new ScrollView { style = { height = 150, marginTop = 8, marginBottom = 8, backgroundColor = new Color(0, 0, 0, 0.25f) } };
            _lens.Add(_timeline);
            _stageRow = Row(); _stageRow.style.flexWrap = UnityEngine.UIElements.Wrap.Wrap; _stageRow.style.marginBottom = 6;
            _lens.Add(_stageRow);
            _detail = new ScrollView { style = { flexGrow = 1 } };
            _lens.Add(_detail);
            _root.Add(_lens);

            // ---- 预警、提示、模态 ----
            _warning = Img(Skin != null ? Skin.TitlePlateEpic : null, 640, 110);
            _warning.style.position = Position.Absolute; _warning.style.top = 14;
            _warning.style.left = new Length(50, LengthUnit.Percent); _warning.style.marginLeft = -320;
            _warning.style.alignItems = Align.Center; _warning.style.justifyContent = Justify.Center;
            _warning.style.paddingLeft = 80; _warning.style.paddingRight = 80; _warning.style.paddingTop = 16;
            _warning.style.display = DisplayStyle.None;
            _root.Add(_warning);
            _toast = new VisualElement { style = { position = Position.Absolute, bottom = 210,
                left = new Length(32, LengthUnit.Percent), right = new Length(32, LengthUnit.Percent),
                backgroundColor = Shade, paddingLeft = 14, paddingRight = 14, paddingTop = 8, paddingBottom = 8, display = DisplayStyle.None,
                borderTopLeftRadius = 6, borderTopRightRadius = 6, borderBottomLeftRadius = 6, borderBottomRightRadius = 6 } };
            _root.Add(_toast);
            _modal = new VisualElement { style = { position = Position.Absolute, left = 0, right = 0, top = 0, bottom = 0, display = DisplayStyle.None,
                backgroundColor = new Color(0, 0, 0, 0.55f), alignItems = Align.Center, justifyContent = Justify.Center } };
            _root.Add(_modal);
            SetMode(LoomMode.Speak);
            ApplyArtDirection();
            BuildPlanEntry();
        }

        // ================= 每帧 =================

        void Update()
        {
            if (Game == null || _clock == null || Game.Session == null) return;
            RefreshArtState();
            RefreshPlanControls();
            // Core 的异步执行尚未接入主线程桥；等待时不枚举它正在写入的世界或轨迹。
            if (Game.Busy)
            {
                if (ModalOpen && Input.GetKeyDown(KeyCode.Escape)) CloseModal();
                return;
            }
            var w = Game.Session.World;
            _clock.text = w.ClockText;
            RefreshPips();
            _coins.text = $"银币 {w.Coins}　·　距黎明之审 {Mathf.Max(0, 12 - w.TimeIndex)} 时段";
            RefreshQuest(w);
            RefreshPlates();
            RefreshWarning(w);
            _nearby.text = Game.NearestActorId != null ? "Enter　与 " + Game.ActorName(Game.NearestActorId).Split('（')[0] + " 交谈" : "";
            if (_lens.style.display == DisplayStyle.Flex)
            {
                var m = Game.Session.Metrics();
                _metrics.text = $"{Game.Session.Provider.Name}\n调用 {m["calls"]} · 接受 {m["accepted"]} · 部分 {m["partial"]} · 驳回 {m["rejected"]}" +
                    $" · 失败 {m["failed"]} · 规则修正 {m["validation_issues"]} · 均 {m["avg_ms"]}ms" +
                    $" · 估算 {(m["estimated_usd"] is decimal d ? "$" + d.ToString("0.0000") : "未知")}";
            }
            if (ModalOpen) { if (Input.GetKeyDown(KeyCode.Escape)) CloseModal(); return; }
            if (Input.GetKeyDown(KeyCode.Escape) && _lens.style.display == DisplayStyle.Flex) { ToggleLens(); return; }
            if (Typing) return;
            if (Input.GetKeyDown(KeyCode.Tab)) ToggleLens();
            if (Input.GetKeyDown(KeyCode.J)) ShowJournal();
            if (Input.GetKeyDown(KeyCode.M)) ShowMap();
            if (Input.GetKeyDown(KeyCode.P)) ShowPlan();
            if ((Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) && !Game.Busy)
            {
                if (Game.NearestActorId != null) SetMode(LoomMode.Speak);
                else if (_mode == LoomMode.Speak) SetMode(w.PlayerPlace == "workshop" ? LoomMode.Craft : LoomMode.Act);
                _input.Focus();
            }
        }

        void RefreshPips()
        {
            int total = Game.Session.PhaseBudget, left = Game.Session.TimeLeftInPhase;
            if (_pips.childCount != total)
            {
                _pips.Clear();
                for (int i = 0; i < total; i++) _pips.Add(Diamond(12, Gold));
            }
            for (int i = 0; i < total; i++) _pips[i].style.backgroundColor = i < left ? Gold : Color.clear;
        }

        void RefreshQuest(DsWorld w)
        {
            var all = DsObjectives.DayOne(w);
            var next = DsObjectives.Next(w);
            int done = all.Count(o => o.Done && !o.Optional), total = all.Count(o => !o.Optional);
            _questProgress.text = $"{done}/{total}";
            if (next != null) { _questTitle.text = next.Text; _questHint.text = QuestSummary(next.Id, next.Hint); }
            else { _questTitle.text = "第一天的目标已完成"; _questHint.text = "按 M 打开地图结束这一时段，或继续探索（J 查看可选目标）。"; }
        }

        void RefreshPlates()
        {
            var cam = Camera.main;
            if (cam == null || _plates.panel == null) return;
            foreach (var kv in Game.ActorTransforms)
            {
                if (kv.Key == "player") continue;
                if (!_plateLabels.TryGetValue(kv.Key, out var label))
                {
                    label = L("", 14, Paper);
                    label.style.position = Position.Absolute;
                    label.style.unityTextOutlineColor = new Color(0, 0, 0, 0.9f);
                    label.style.unityTextOutlineWidth = 0.25f;
                    label.pickingMode = PickingMode.Ignore;
                    _plates.Add(label);
                    _plateLabels[kv.Key] = label;
                }
                Vector3 head = kv.Value.position + Vector3.up * 2.2f;
                Vector3 sp = cam.WorldToScreenPoint(head);
                float dist = Vector3.Distance(cam.transform.position, head);
                bool show = sp.z > 0 && dist < 22f && kv.Value.gameObject.activeInHierarchy;
                label.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                if (!show) continue;
                bool near = Game.NearestActorId == kv.Key;
                label.text = Game.ActorName(kv.Key).Split('（')[0];
                label.style.color = near ? Gold : Paper;
                label.style.fontSize = near ? 17 : 14;
                var pos = RuntimePanelUtils.ScreenToPanel(_plates.panel, new Vector2(sp.x, Screen.height - sp.y));
                float lw = label.resolvedStyle.width; if (float.IsNaN(lw)) lw = 80;
                label.style.left = pos.x - lw / 2; label.style.top = pos.y - 12;
            }
        }

        void RefreshWarning(DsWorld w)
        {
            string warn = DsObjectives.Warning(w);
            if (warn == _lastWarning) return;
            _lastWarning = warn;
            _warning.Clear();
            if (warn == null) { _warning.style.display = DisplayStyle.None; return; }
            var l = Wrap(L(warn, 15, Paper, true)); l.style.unityTextAlign = TextAnchor.MiddleCenter;
            _warning.Add(l);
            _warning.style.display = DisplayStyle.Flex;
        }

        void RenderGems(AiTrace t, bool pending = false)
        {
            _gems.Clear();
            for (int i = 0; i < 7; i++)
            {
                bool lit = t != null && i < _revealed && i < t.Stages.Count;
                var color = lit ? StatusColor(t.Stages[i].Status) : pending ? new Color(0.5f, 0.6f, 0.85f, 0.25f + 0.08f * i) : new Color(1, 1, 1, 0.08f);
                var gem = Diamond(18, Gold * 0.85f);
                gem.style.backgroundColor = color;
                gem.style.marginRight = 22;
                gem.tooltip = AiTrace.StageLabel((AiStage)i);
                _gems.Add(gem);
            }
            if (t != null && _revealed >= 7)
                _stripLine.text = t.FailureCode != null ? "丝线中断 · 输入已保留" : "织言已落定 · 查看回响";
            else if (t != null) _stripLine.text = AiTrace.StageLabel((AiStage)Mathf.Min(6, _revealed)) + "……";
            else if (pending) _stripLine.text = "织言机正在理解……";
        }

        public void Toast(string message)
        {
            _toast.Clear();
            _toast.Add(Wrap(L(message, 15, Paper)));
            _toast.style.display = DisplayStyle.Flex;
            EnterMotion(_toast);
            CancelInvoke(nameof(HideToast));
            Invoke(nameof(HideToast), 3f);
        }

        void HideToast() => _toast.style.display = DisplayStyle.None;

        void ToggleLens()
        {
            bool open = _lens.style.display != DisplayStyle.Flex;
            _lens.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            _strip.style.display = open ? DisplayStyle.None : DisplayStyle.Flex;
            if (open) { EnterMotion(_lens); if (_selected != null) Render(_selected); }
        }

        // ================= 模态：开场、日志、地图、日终、结局 =================

        VisualElement Card(float width)
        {
            _planPanelOpen = false;
            _modal.Clear();
            var card = new VisualElement
            {
                name = "storyCard", style = { width = width, maxWidth = new Length(92, LengthUnit.Percent),
                    maxHeight = new Length(90, LengthUnit.Percent), paddingLeft = 44, paddingRight = 44,
                    paddingTop = 34, paddingBottom = 28, alignItems = Align.Center, backgroundColor = new Color(0.90f, 0.88f, 0.81f) }
            };
            Border(card, Gold, 1);
            var crest = L("◇   龙 眠 镇   ◇", 13, InkSoft); crest.style.marginBottom = 16; card.Add(crest);
            _modal.Add(card);
            _modal.style.display = DisplayStyle.Flex;
            EnterMotion(card);
            return card;
        }

        bool _craftConfirmation;
        void CloseModal()
        {
            _planPanelOpen = false;
            _modal.style.display = DisplayStyle.None;
            if (_craftConfirmation) { _craftConfirmation = false; Game.DecideCraft(false); }
        }

        public void ShowCraftProposal()
        {
            var card = Card(720); _craftConfirmation = true;
            card.name = "craftProposal";
            card.Add(L("织 造 图 样", 30, Ink, true));
            var description = Wrap(L(Game.Session.PendingCraftDescription, 18, InkSoft));
            description.style.marginTop = 18; description.style.alignSelf = Align.Stretch; card.Add(description);
            var row = Row();
            var confirm = CardButton("按图织造", () => { _craftConfirmation = false; CloseModal(); Game.DecideCraft(true); });
            confirm.name = "confirmCraft"; confirm.style.marginRight = 12;
            var cancel = CardButton("先不织造", CloseModal); cancel.name = "declineCraft";
            row.Add(confirm); row.Add(cancel); card.Add(row);
        }

        Button CardButton(string text, System.Action onClick)
        {
            var b = SkinButton(text, onClick, Skin != null ? Skin.ButtonRed : null, 230, 56);
            b.style.marginTop = 20; b.style.fontSize = 18;
            return b;
        }

        public void ShowIntro() => BuildIllustratedIntro();

        public void ShowDayRecap(DsObjectives.DayRecap r)
        {
            var card = Card(780);
            card.Add(L(r.Title, 32, Ink, true));
            var body = new VisualElement { style = { alignSelf = Align.Stretch, marginTop = 10 } };
            foreach (var x in r.Completed) body.Add(Wrap(L("✓ " + x, 17, new Color(0.18f, 0.35f, 0.12f))));
            foreach (var x in r.Missed) body.Add(Wrap(L("○ " + x, 17, InkSoft)));
            var h = L("夜里，居民们各自行动", 16, Ink, true); h.style.marginTop = 12; body.Add(h);
            foreach (var x in r.Night) body.Add(Wrap(L("· " + x, 16, InkSoft)));
            var wl = Wrap(L(r.Whelp, 18, new Color(0.45f, 0.12f, 0.08f), true)); wl.style.marginTop = 14; body.Add(wl);
            body.Add(L($"今天 {r.AiCalls} 次 AI 调用，规则修正 {r.Corrections} 处（Tab 可逐条回看）", 13, InkSoft));
            card.Add(body);
            card.Add(CardButton("继续", CloseModal));
        }

        public void ShowEnding(DsEndings.Ending e)
        {
            var card = Card(780);
            card.Add(L("第三个黎明", 15, InkSoft));
            card.Add(L(e.Title, 46, Ink, true));
            var t = Wrap(L(e.Text, 18, Ink)); t.style.unityTextAlign = TextAnchor.MiddleCenter; t.style.marginTop = 16;
            card.Add(t);
            card.Add(CardButton("再来一局", () => { CloseModal(); Game.Restart(); ClearLens(); }));
        }

        void ClearLens()
        {
            _timeline.Clear(); _rows.Clear(); _detail.Clear(); _stageRow.Clear(); _selected = null;
            FinishLine(); SetSpeaker(null, ""); _line.text = "新的一局。";
            RenderGems(null);
        }

    }
}
