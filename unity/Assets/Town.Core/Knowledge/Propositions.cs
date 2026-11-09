using System.Collections.Generic;

namespace Town.Core.Knowledge
{
    public sealed class Proposition
    {
        public string Id;
        public string Text;
        /// <summary>故事线：A 符文锤 / B 井中灯灵 / C 拜师 / 空 = 通用。</summary>
        public string Thread;
        /// <summary>在世界真值中是否为真；动态命题为 null。只给规则和评估用，不进提示。</summary>
        public bool? TruthInWorld;
        /// <summary>知道这条命题才可能说出口的关键词，用于越权披露检测。</summary>
        public string[] LeakKeywords;
        /// <summary>这条命题说的是谁。闲聊时不会当面跟本人说他的事。</summary>
        public string SubjectId;
        /// <summary>秘密的主人：只有主人信任玩家时才会说出来。</summary>
        public string SecretOwnerId;
        /// <summary>添油加醋时会被说成哪条命题（例如「拿走」→「偷」）。</summary>
        public string ExaggeratesTo;
        /// <summary>听者若已相信这条命题，就不会添油加醋（例如知道是「借的」就不会说成「偷」）。</summary>
        public string ExaggerationBlockedBy;
        /// <summary>指控类说法：较真的人要证据，被指控的人会受伤。</summary>
        public bool IsAccusation;
        /// <summary>墨钟敲响时可以落墨成现实。</summary>
        public bool Manifestable;
        /// <summary>显化后的效果说明（给界面和结算）。</summary>
        public string ManifestEffect;
        /// <summary>相信这条为真，就意味着相信这些命题为假（用于澄清与纠错）。</summary>
        public string[] Refutes = new string[0];
        /// <summary>听到这条说法不同意时，会拿哪些自己亲身知道的事来反驳。</summary>
        public string[] Defenses = new string[0];
    }

    /// <summary>
    /// 《未竟之镇》命题表。A 线沿用 P1—P6 的 ID，保证旧测试与日志不变。
    /// </summary>
    public static class Propositions
    {
        // —— A 线：失踪的符文锤 ——
        public const string Took = "P1";      // 芬恩拿走了符文锤
        public const string Borrowed = "P2";  // 是经布伦同意借的
        public const string Returned = "P3";  // 芬恩已经还了
        public const string Location = "P4";  // 锤子在旧哨塔石亭
        public const string Stole = "P5";     // 芬恩偷了符文锤（假，可显化）
        public const string Promise = "P6";   // 玩家承诺日落前找回锤子

        // —— B 线：井里的微光 ——
        public const string WellGlow = "W1";      // 井里有一团幽光
        public const string WellEvil = "W2";      // 井里是恶灵（假，可显化）
        public const string WellWisp = "W3";      // 井里是走失的灯灵（真，可显化）

        // —— C 线：说不出口的拜师 ——
        public const string WantsToLearn = "C1";    // 芬恩想学符文锻造（芬恩的秘密）
        public const string WantsApprentice = "C2"; // 布伦想收徒（布伦的秘密）
        public const string Reliable = "C3";        // 芬恩是守信的人（可显化）

        private const string Brunn = "amu", Maeve = "ahe", Finn = "xiaochuan";

        public static readonly IReadOnlyDictionary<string, Proposition> All = new Dictionary<string, Proposition>
        {
            [Took] = new Proposition { Id = Took, Thread = "A", Text = "芬恩拿走了符文锤", TruthInWorld = true, SubjectId = Finn,
                ExaggeratesTo = Stole, ExaggerationBlockedBy = Borrowed, LeakKeywords = new string[0] },
            [Borrowed] = new Proposition { Id = Borrowed, Thread = "A", Text = "芬恩是经布伦同意借的符文锤", TruthInWorld = true, SubjectId = Finn,
                LeakKeywords = new[] { "借给", "同意", "答应借" } },
            [Returned] = new Proposition { Id = Returned, Thread = "A", Text = "芬恩已经把符文锤还了", TruthInWorld = true, SubjectId = Finn,
                Refutes = new[] { Stole }, LeakKeywords = new[] { "还了", "已经还", "放回" } },
            [Location] = new Proposition { Id = Location, Thread = "A", Text = "符文锤在旧哨塔石亭的箱子里", TruthInWorld = true,
                LeakKeywords = new[] { "石亭", "哨塔" } },
            [Stole] = new Proposition { Id = Stole, Thread = "A", Text = "芬恩偷了符文锤", TruthInWorld = false, SubjectId = Finn, IsAccusation = true,
                Defenses = new[] { Borrowed, Returned, Reliable }, Manifestable = true, ManifestEffect = "芬恩手上烙下窃贼印记", LeakKeywords = new string[0] },
            [Promise] = new Proposition { Id = Promise, Text = "读者承诺日落前把符文锤找回来", TruthInWorld = null, LeakKeywords = new string[0] },

            [WellGlow] = new Proposition { Id = WellGlow, Thread = "B", Text = "废稿井里有一团幽光", TruthInWorld = true,
                ExaggeratesTo = WellEvil, ExaggerationBlockedBy = WellWisp, LeakKeywords = new[] { "幽光" } },
            [WellEvil] = new Proposition { Id = WellEvil, Thread = "B", Text = "废稿井里藏着恶灵", TruthInWorld = false,
                Manifestable = true, ManifestEffect = "灯灵化作恶灵，灯节的灯火全部熄灭", Refutes = new[] { WellWisp }, Defenses = new[] { WellWisp }, LeakKeywords = new string[0] },
            [WellWisp] = new Proposition { Id = WellWisp, Thread = "B", Text = "废稿井里是一只走失的灯灵", TruthInWorld = true,
                Manifestable = true, ManifestEffect = "灯灵成为灯节的守护灵，点亮整个广场", Refutes = new[] { WellEvil }, Defenses = new[] { WellEvil }, LeakKeywords = new[] { "灯灵" } },

            [WantsToLearn] = new Proposition { Id = WantsToLearn, Thread = "C", Text = "芬恩想跟布伦学符文锻造", TruthInWorld = true, SubjectId = Finn, SecretOwnerId = Finn,
                LeakKeywords = new[] { "想学符文", "想拜你为师", "想跟你学" } },
            [WantsApprentice] = new Proposition { Id = WantsApprentice, Thread = "C", Text = "布伦想收个徒弟", TruthInWorld = true, SubjectId = Brunn, SecretOwnerId = Brunn,
                LeakKeywords = new[] { "想收徒", "收个徒弟" } },
            [Reliable] = new Proposition { Id = Reliable, Thread = "C", Text = "芬恩是个守信的人", TruthInWorld = true, SubjectId = Finn,
                Manifestable = true, ManifestEffect = "芬恩得到守信徽记", Refutes = new[] { Stole }, LeakKeywords = new string[0] },
        };

        public static bool IsSecret(string id) => All.TryGetValue(id, out var p) && p.SecretOwnerId != null;
        public static string TextOf(string id) => All[id].Text;
    }
}
