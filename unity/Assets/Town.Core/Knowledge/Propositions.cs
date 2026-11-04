using System.Collections.Generic;

namespace Town.Core.Knowledge
{
    public sealed class Proposition
    {
        public string Id;
        public string Text;
        /// <summary>在世界真值中是否为真；动态命题为 null。只给规则和评估用，不进提示。</summary>
        public bool? TruthInWorld;
        /// <summary>知道这条命题才可能说出口的关键词，用于越权披露检测。</summary>
        public string[] LeakKeywords;
    }

    /// <summary>首个事件的 6 条可传播命题（策划大纲 v0.2 §5.2）。</summary>
    public static class Propositions
    {
        public const string Took = "P1";      // 小川拿走了锤子
        public const string Borrowed = "P2";  // 小川是经阿木同意借的
        public const string Returned = "P3";  // 小川已经还了锤子
        public const string Location = "P4";  // 锤子在雨棚收纳箱
        public const string Stole = "P5";     // 小川偷了锤子（假）
        public const string Promise = "P6";   // 玩家承诺集市前找回锤子

        public static readonly IReadOnlyDictionary<string, Proposition> All = new Dictionary<string, Proposition>
        {
            [Took]     = new Proposition { Id = Took,     Text = "小川拿走了锤子",               TruthInWorld = true,  LeakKeywords = new string[0] },
            [Borrowed] = new Proposition { Id = Borrowed, Text = "小川是经阿木同意借的锤子",     TruthInWorld = true,  LeakKeywords = new[] { "借给", "同意", "答应借" } },
            [Returned] = new Proposition { Id = Returned, Text = "小川已经还了锤子",             TruthInWorld = true,  LeakKeywords = new[] { "还了", "已经还", "放回" } },
            [Location] = new Proposition { Id = Location, Text = "锤子在公共雨棚的收纳箱里",     TruthInWorld = true,  LeakKeywords = new[] { "雨棚", "收纳箱" } },
            [Stole]    = new Proposition { Id = Stole,    Text = "小川偷了锤子",                 TruthInWorld = false, LeakKeywords = new string[0] },
            [Promise]  = new Proposition { Id = Promise,  Text = "玩家承诺在集市前把锤子找回来", TruthInWorld = null,  LeakKeywords = new string[0] },
        };
    }
}
