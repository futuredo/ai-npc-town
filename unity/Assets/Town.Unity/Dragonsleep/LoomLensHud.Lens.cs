using System.Collections;
using Town.Core.Dragonsleep;
using UnityEngine;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class LoomLensHud
    {
        void AddRow(AiTrace t)
        {
            if (_rows.TryGetValue(t, out var previous)) { previous.RemoveFromHierarchy(); _rows.Remove(t); }
            var row = Row();
            row.style.alignItems = Align.Center; row.style.paddingTop = 3; row.style.paddingBottom = 3; row.style.paddingLeft = 6;
            var dot = Diamond(9, Color.clear); dot.style.backgroundColor = MechColor(t.Kind); dot.style.marginRight = 8;
            row.Add(dot);
            row.Add(L($"#{t.Sequence}  {AiTrace.MechanismLabel(t.Kind)} · {t.ActorName.Split('（')[0]} · {VerdictText(t)}", 13,
                t.FailureCode != null ? new Color(1f, 0.55f, 0.5f) : Paper));
            row.RegisterCallback<ClickEvent>(_ => Select(t, animate: false));
            _timeline.Add(row);
            _rows[t] = row;
            _timeline.schedule.Execute(() =>
            {
                var scroll = (ScrollView)_timeline;
                if (scroll.contentContainer.Contains(row)) scroll.ScrollTo(row);
            }).StartingIn(30);
        }

        void Select(AiTrace t, bool animate)
        {
            _selected = t;
            foreach (var kv in _rows) kv.Value.style.backgroundColor = kv.Key == t ? new Color(1f, 1f, 1f, 0.1f) : Color.clear;
            if (_reveal != null) StopCoroutine(_reveal);
            if (animate && isActiveAndEnabled) _reveal = StartCoroutine(Reveal(t));
            else { _revealed = 7; RenderGems(t); Render(t); }
        }

        IEnumerator Reveal(AiTrace t)
        {
            // 离线提供方几乎瞬时返回；逐颗点亮只是让七个阶段的顺序可读（真实耗时见织言镜各阶段 ms）。
            for (_revealed = 0; _revealed <= 7; _revealed++)
            {
                RenderGems(t);
                if (_lens.style.display == DisplayStyle.Flex) Render(t);
                yield return new WaitForSecondsRealtime(DsUiMotion.Reduced ? 0f : StageRevealSeconds);
            }
            _revealed = 7;
            RenderGems(t);
            Render(t);
            _reveal = null;
        }

        void Render(AiTrace t)
        {
            _stageRow.Clear();
            for (int i = 0; i < t.Stages.Count; i++)
            {
                var s = t.Stages[i];
                bool lit = i < _revealed;
                _stageRow.Add(new Label((i + 1) + " " + AiTrace.StageLabel(s.Stage))
                {
                    style =
                    {
                        fontSize = 13, paddingLeft = 7, paddingRight = 7, paddingTop = 3, paddingBottom = 3, marginRight = 4, marginBottom = 4,
                        backgroundColor = lit ? StatusColor(s.Status) : new Color(1, 1, 1, 0.06f),
                        color = lit ? new Color(0.08f, 0.07f, 0.06f) : Muted,
                    }
                });
            }
            _detail.Clear();
            Section($"{AiTrace.MechanismLabel(t.Kind)} · {t.ActorName} · {t.Clock} · {t.RequestId}", MechColor(t.Kind));
            if (!string.IsNullOrEmpty(t.PlayerInput)) Body("玩家输入（作为不可信数据发送）：" + t.PlayerInput, new Color(1f, 0.85f, 0.6f));
            for (int i = 0; i < t.Stages.Count && i < _revealed; i++)
            {
                var s = t.Stages[i];
                Body($"{i + 1}. {AiTrace.StageLabel(s.Stage)}［{StatusText(s.Status)}］{s.Millis:0.0}ms　{s.Detail}",
                    Color.Lerp(StatusColor(s.Status), Paper, 0.45f));
                if (s.Stage == AiStage.Context && t.Perception != null) Code(Trim(MiniJson.Write(t.Perception, true), 900));
                if (s.Stage == AiStage.Prompt && t.Prompt != null)
                    foreach (var sec in t.Prompt.Sections) Body($"　▪ {sec.Name}　{sec.Text?.Length ?? 0} 字{(sec.Untrusted ? "　⚠ 不可信数据" : "")}", Muted);
                if (s.Stage == AiStage.Provider && t.Provider?.RawText != null) Code(Trim(t.Provider.RawText, 700));
                if (s.Stage == AiStage.Validate) foreach (var issue in t.ValidationIssues) Body("　⚠ " + issue, new Color(1f, 0.78f, 0.45f));
                if (s.Stage == AiStage.Adjudicate && t.Verdict.Roll.HasValue && t.Verdict.Difficulty.HasValue)
                    RollBar(t.Verdict.Roll.Value, t.Verdict.Difficulty.Value);
                if (s.Stage == AiStage.Commit) foreach (var c in t.Changes) Body("　✎ " + c, new Color(0.62f, 0.95f, 0.7f));
            }
            if (_revealed >= 7) { Section("呈现给玩家", Gold); Body(t.Presented, Paper); }
        }

        void RollBar(double roll, double difficulty)
        {
            var bar = new VisualElement
            {
                style = { height = 12, marginLeft = 14, marginRight = 6, marginTop = 2, marginBottom = 4, backgroundColor = new Color(1, 1, 1, 0.08f) }
            };
            bar.Add(new VisualElement
            {
                style =
                {
                    position = Position.Absolute, left = 0, top = 0, bottom = 0,
                    width = new Length((float)difficulty * 100f, LengthUnit.Percent), backgroundColor = new Color(0.85f, 0.35f, 0.3f, 0.55f)
                }
            });
            bar.Add(new VisualElement
            {
                style =
                {
                    position = Position.Absolute, top = -2, bottom = -2, width = 3,
                    left = new Length((float)roll * 100f, LengthUnit.Percent), backgroundColor = Gold
                }
            });
            _detail.Add(bar);
            Body($"　难度（红）{difficulty:0.00} · 掷骰（金）{roll:0.00}", Muted);
        }

        void Section(string text, Color color)
        { var l = Wrap(L(text, 15, color, true)); l.style.marginTop = 8; l.style.marginBottom = 2; _detail.Add(l); }
        void Body(string text, Color color) { var l = Wrap(L(text, 13, color)); l.style.marginBottom = 2; _detail.Add(l); }
        void Code(string text)
        {
            var l = Wrap(L(text, 11, new Color(0.75f, 0.85f, 0.95f)));
            l.style.backgroundColor = new Color(0, 0, 0, 0.35f);
            l.style.paddingLeft = 8; l.style.paddingRight = 8; l.style.paddingTop = 5; l.style.paddingBottom = 5;
            l.style.marginLeft = 14; l.style.marginBottom = 4;
            _detail.Add(l);
        }
    }
}
