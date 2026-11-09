using System.Linq;

namespace Town.Core.Dragonsleep
{
    /// <summary>第三个黎明的审判：结局由世界真实状态计算，不由旁白宣布。</summary>
    public static class DsEndings
    {
        public sealed class Ending
        {
            public string Id;
            public string Title;
            public string Text;
        }

        public static Ending Evaluate(DsWorld w)
        {
            var wh = w.Whelp;
            bool pactKnown = w.Items.TryGetValue("pact_page", out var page) && page.Holder == "player" || w.Flags.Keys.Any(k => k.StartsWith("shown:pact_page"));
            bool ember = w.Items.Values.Any(i => i.Holder == "player" && i.Tags.Contains("ember"));
            if (wh.CapturedBy == "vesna")
                return new Ending { Id = "far_sea", Title = "远洋", Text = "维丝娜的船已驶出港口。苍穹之母掠过镇子，追向海面——龙眠镇幸存，却永远失去了龙的庇护。" };
            if (wh.CapturedBy == "osric")
                return pactKnown
                    ? new Ending { Id = "iron_cage_saved", Title = "铁笼前的契约", Text = "母龙的火焰已在城门前燃起。你高举契约页，奥斯里克终于打开了铁笼——小镇被烧去半边，但活了下来。" }
                    : new Ending { Id = "iron_cage", Title = "铁笼", Text = "雏龙在王城的铁笼里哀鸣。第三个黎明，龙眠镇在火中度过。" };
            if (!wh.Hidden && wh.TrustPlayer >= 0.4f && (pactKnown || ember))
                return pactKnown && w.Actors["maren"].Trust >= 0.6f
                    ? new Ending { Id = "new_pact", Title = "新约", Text = "梅伦修女当众念出被撕下的契约。母龙低下头，让小烬钻进她的翼下。三百年的谎言，在龙骨岗上结束了。" }
                    : new Ending { Id = "homecoming", Title = "归巢", Text = "小烬循着余烬的气味扑向母亲。苍穹之母看了你很久，然后带着孩子飞回雪峰。" };
            return new Ending { Id = "unresolved", Title = "未竟", Text = "母龙在龙骨岗上等到正午，没有等到她的孩子。她仰天长啸，离去时掀翻了钟楼。" };
        }
    }
}
