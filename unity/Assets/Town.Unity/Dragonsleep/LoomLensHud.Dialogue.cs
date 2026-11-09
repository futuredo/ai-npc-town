using System.Collections.Generic;
using Town.Core.Dragonsleep;
using UnityEngine;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class LoomLensHud
    {
        Button ModeButton(string text, LoomMode mode)
        {
            var b = SkinButton(text, () => SetMode(mode), Skin != null ? Skin.ButtonRound : null, 46, 46);
            b.style.fontSize = 19; b.style.marginRight = 4;
            b.tooltip = mode == LoomMode.Speak ? "言：对身边的人说话" : mode == LoomMode.Act ? "行：描述一个行动" : "造：在附魔工坊织造物件";
            return b;
        }

        void SetMode(LoomMode mode)
        {
            _mode = mode;
            for (int i = 0; i < _modes.Length; i++)
            {
                bool on = (int)mode == i;
                var tex = Skin == null ? null : on ? Skin.ButtonRoundRed : Skin.ButtonRound;
                _modes[i].style.backgroundImage = StyleKeyword.None;
                _modes[i].style.backgroundColor = on ? new Color(0.26f, 0.31f, 0.32f) : new Color(0.06f, 0.10f, 0.13f, 0.94f);
                _modes[i].style.color = on ? Gold : Muted;
                _modes[i].style.borderBottomColor = on ? Gold : Color.clear;
                _modes[i].style.borderBottomWidth = 2;
            }
            if (_input != null)
                _input.textEdition.placeholder = mode == LoomMode.Speak ? "对身边的人说一句话……"
                    : mode == LoomMode.Act ? "我想……（描述你要尝试的行动）"
                    : "描述想织造的物件和要用的材料……";
        }

        void Send()
        {
            string text = _input.value;
            if (string.IsNullOrWhiteSpace(text) || Game.Busy) return;
            if (_mode == LoomMode.Speak && Game.NearestActorId == null)
            { Toast("身边没有人——已切换为【行】，再按一次 Enter 发送。"); SetMode(LoomMode.Act); return; }
            _lastSubmitted = text;
            _input.value = "";
            _input.Blur();
            FinishLine();
            _line.text = "话语落进织机，丝线正在回应……";
            RenderGems(null, pending: true);
            Game.Submit(_mode, text);
        }

        public void Present(AiTrace t)
        {
            if (t.FailureCode != null && string.IsNullOrEmpty(_input.value)) _input.value = _lastSubmitted ?? "";
            _conversationUntil = Time.unscaledTime + 22f;
            EnterMotion(_dialog);
            int split = t.Presented.IndexOf('：');
            bool npc = t.Kind == AiTaskKind.Dialogue && split > 0 && t.FailureCode == null;
            SetSpeaker(npc ? t.ActorId : "player", npc ? Game.ActorName(t.ActorId).Split('（')[0] : "言契师");
            AnimateLine(npc ? t.Presented.Substring(split + 1) : t.Presented);
            AddRow(t);
            Select(t, animate: true);
        }

        public void PresentNight(List<AiTrace> traces)
        {
            _conversationUntil = Time.unscaledTime + 22f;
            foreach (var t in traces) AddRow(t);
            SetSpeaker(null, "夜里发生的事");
            FinishLine();
            _line.text = "夜里的回响已写入织言镜。天亮后，去看看小镇的变化。";
            if (traces.Count > 0) Select(traces[traces.Count - 1], animate: true);
        }

        void SetSpeaker(string actorId, string name)
        {
            _speaker.text = name;
            var img = _portrait.Q("img");
            var tex = actorId != null && Skin != null ? Skin.PortraitOf(actorId) : null;
            img.style.backgroundImage = tex;
            img.style.backgroundColor = tex == null ? new Color(0.1f, 0.12f, 0.2f) : Color.clear;
            _portrait.style.display = actorId == null ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
