using System;
using UnityEngine;

namespace Town.Unity.Dragonsleep
{
    /// <summary>龙眠镇界面皮肤：引用 Medieval Kingdom UI 套件的白金皮肤贴图与人物头像（由场景生成器填充）。</summary>
    [CreateAssetMenu(menuName = "Town/Dragonsleep UI Skin")]
    public sealed class DsUiSkin : ScriptableObject
    {
        public Texture2D TitlePlate;      // Title_frame_medium：红底银框标题牌
        public Texture2D TitlePlateEpic;  // Title_frame_medium_epic：预警
        public Texture2D Parchment;       // Main_bar_story_hori：目标卡
        public Texture2D PaperWindow;     // Popup_window_paper：开场/日终/结局/日志/地图
        public Texture2D DarkPanel;       // Main_bar：对话框
        public Texture2D NavyBar;         // dialog_frame_a：输入框、织言条
        public Texture2D NavyWindow;      // Popup_window：织言镜
        public Texture2D RoundFrame;      // icon_frame_round：头像框
        public Texture2D ButtonDark;      // Button_middle
        public Texture2D ButtonRed;       // Button_middle_red
        public Texture2D ButtonRound;     // Button_round
        public Texture2D ButtonRoundRed;  // Button_round_red
        public Portrait[] Portraits = Array.Empty<Portrait>();

        [Serializable]
        public struct Portrait
        {
            public string ActorId;
            public Texture2D Image;
        }

        public Texture2D PortraitOf(string actorId)
        {
            foreach (var p in Portraits) if (p.ActorId == actorId) return p.Image;
            return null;
        }
    }
}
