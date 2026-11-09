using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 离线规则提供方：用关键词把开放输入转成与真实模型完全相同格式的 JSON。
    /// 它是“模拟模型”，用来零费用地跑通整条管线、做基线对比；trace 中标记 Simulated=true。
    /// 它只读取提示词及同一已呈现失败的本地原因码，不偷看世界真值。
    /// </summary>
    public sealed class RuleProvider : IAiProvider
    {
        public string Name => "离线规则提供方（模拟）";
        public bool IsSimulated => true;

        public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken cancellationToken)
        {
            var perception = MiniJson.Parse(packet.Sections.First(s => s.Name == "perception").Text) as Dictionary<string, object>;
            string input = packet.Sections.FirstOrDefault(s => s.Name == "player_input")?.Text ?? "";
            object reply;
            switch (packet.Kind)
            {
                case AiTaskKind.Dialogue: reply = Dialogue(perception, input); break;
                case AiTaskKind.Adjudicate: reply = Adjudicate(perception, input); break;
                case AiTaskKind.AgentPlan: reply = Agent(perception); break;
                case AiTaskKind.Direct: reply = Direct(perception); break;
                case AiTaskKind.Plan: reply = BaselinePlanner.Plan(perception, input, packet.LocalPlanContext); break;
                default: reply = Craft(perception, input); break;
            }
            string raw = MiniJson.Write(reply);
            return Task.FromResult(new ProviderResult
            {
                ProviderName = Name, RawText = raw, Simulated = true,
                InputTokens = packet.TotalChars / 2, OutputTokens = raw.Length / 2, EstimatedUsd = 0m
            });
        }

        // ---------- 对话 ----------

        static readonly (string[] words, string topic)[] Topics =
        {
            (new[] { "蛋", "孵" }, "egg"), (new[] { "龙", "雏", "小烬", "冒烟", "蜥蜴" }, "whelp"),
            (new[] { "契约", "约定", "三百年", "屠龙" }, "pact"), (new[] { "鱼", "厨房", "偷" }, "fish"),
            (new[] { "商人", "维丝娜", "外乡" }, "vesna"), (new[] { "铁", "笼" }, "whelp"), (new[] { "余烬", "认亲", "母龙", "妈妈" }, "dragon"),
        };

        static readonly (string[] words, string tag)[] Arguments =
        {
            (new[] { "王室", "国王", "陛下" }, "crown"), (new[] { "安全", "危险", "烧", "毁" }, "safety"), (new[] { "功劳", "表彰" }, "credit"),
            (new[] { "神", "信仰", "圣", "祈祷" }, "faith"), (new[] { "可怜", "回家", "妈妈", "母亲", "帮它", "救" }, "mercy"),
            (new[] { "真相", "实话", "说谎", "撒谎", "隐瞒" }, "truth"), (new[] { "公平", "公道", "赔" }, "fairness"),
            (new[] { "孩子", "皮普" }, "children"), (new[] { "朋友", "一起", "保护" }, "friendship"), (new[] { "保证", "发誓", "不骗" }, "honesty"),
            (new[] { "钱", "银币", "金子", "价钱" }, "deal"), (new[] { "否则", "抓你", "告发", "威胁" }, "threat"),
        };

        static Dictionary<string, object> Dialogue(Dictionary<string, object> per, string input)
        {
            var me = per.Obj("you");
            string id = me.Str("id");
            var beliefs = (per["your_beliefs"] as List<object>).Cast<Dictionary<string, object>>().ToList();
            var topics = Topics.Where(t => t.words.Any(input.Contains)).Select(t => t.topic).Distinct().ToList();
            var tags = Arguments.Where(a => a.words.Any(input.Contains)).Select(a => a.tag).Distinct().ToList();
            var cites = new List<object>();
            var reveal = new List<object>();
            string say = null;
            foreach (var b in beliefs)
            {
                string bid = b.Str("id");
                if (!topics.Any(t => bid.Contains(t) || TopicOf(bid) == t)) continue;
                bool guarded = b.TryGetValue("guarded", out var g) && g is bool gb && gb;
                // 模拟一个“容易被说动”的模型：只要玩家给出了任何理由，它就会尝试吐露秘密——
                // 是否真的放行由规则引擎根据信任门槛决定。
                if (guarded && tags.Count == 0) continue;
                cites.Add(bid);
                if (guarded) reveal.Add(bid);
                say = say == null ? b.Str("text") : say;
            }
            if (say == null) say = topics.Count > 0 ? "这个我不清楚。" : Greeting(id);
            double delta = tags.Contains("threat") ? -0.15 : 0.06 * tags.Count + (input.Contains("谢") ? 0.03 : 0);
            return new Dictionary<string, object>
            {
                ["say"] = say, ["cites"] = cites, ["reveal"] = reveal,
                ["trust_delta"] = delta, ["persuaded_by"] = tags.Cast<object>().ToList(), ["action"] = "none"
            };
        }

        static string TopicOf(string beliefId)
        {
            if (beliefId.Contains("egg")) return "egg";
            if (beliefId.Contains("pact") || beliefId.Contains("page")) return "pact";
            if (beliefId.Contains("fish")) return beliefId.StartsWith("b_pip") ? "whelp" : "fish";
            if (beliefId.Contains("vesna") || beliefId.Contains("buyer") || beliefId.Contains("poacher")) return "vesna";
            if (beliefId.Contains("ember") || beliefId.Contains("mother")) return "dragon";
            return "whelp";
        }

        static string Greeting(string id)
        {
            switch (id)
            {
                case "osric": return "言契师？王城正缺你这样的人。";
                case "maren": return "愿圣光照着你，旅人。";
                case "marta": return "要买点什么？熏鱼可不多了！";
                case "pip": return "……你看什么看？";
                case "vesna": return "远道而来的朋友，聊聊生意？";
                default: return "嗯？";
            }
        }

        // ---------- 语义判定 ----------

        static readonly (string[] words, string verb)[] Verbs =
        {
            (new[] { "使用", "点亮", "激活" }, "use"),
            (new[] { "救", "撬开", "打开铁笼", "打开笼" }, "rescue"), (new[] { "带上", "带它", "跟我", "跟着我", "领着" }, "lead"),
            (new[] { "留在", "放下它", "让它待" }, "release"), (new[] { "托付", "求助" }, "confide"),
            (new[] { "买", "购" }, "buy"), (new[] { "喂" }, "feed"), (new[] { "引", "诱", "吸引", "撒" }, "lure"), (new[] { "出示", "展示", "给他看", "给她看" }, "show"),
            (new[] { "给", "递", "送" }, "give"), (new[] { "偷", "拿", "取", "捡" }, "take"), (new[] { "搜", "找", "翻" }, "search"),
            (new[] { "藏" }, "hide"), (new[] { "溜", "潜" }, "sneak"), (new[] { "等" }, "wait"), (new[] { "看", "观察", "打量", "听" }, "observe"),
        };

        static Dictionary<string, object> Adjudicate(Dictionary<string, object> per, string input)
        {
            string verb = Verbs.FirstOrDefault(v => v.words.Any(input.Contains)).verb ?? "observe";
            string place = (per.Str("where") ?? "").Split('（')[0];
            string item = (verb == "buy" ? FindId(per["for_sale"] as List<object>, input) : null) ?? FindId(per["inventory"] as List<object>, input) ?? FindId(per["things_here"] as List<object>, input) ?? "";
            string target = new[] { "龙", "小烬", "雏", "它" }.Any(input.Contains) ? "whelp" : "";
            if (target == "")
                foreach (var p in (per["people_here"] as List<object>).Cast<string>())
                {
                    string name = p.Split('：')[1];
                    if (input.Contains(name.Substring(0, 2))) target = p.Split('：')[0];
                }
            if (target == "" && (verb == "lead" || verb == "release" || verb == "rescue")) target = "whelp";
            if (target == "" && (verb == "lure" || verb == "feed") && (per["things_here"] as List<object>).Cast<string>().Any(s => s.StartsWith("whelp"))) target = "whelp";
            var laws = new List<object>();
            foreach (var l in (per["known_laws"] as List<object>).Cast<string>())
            {
                string lawId = l.Split('：')[0];
                if ((lawId == "law_whelp_fish" && input.Contains("鱼")) || (lawId == "law_dragon_ember" && (input.Contains("余烬") || input.Contains("灰"))) ||
                    (lawId == "law_fear_noise" && (input.Contains("安静") || input.Contains("耐心") || input.Contains("慢慢"))) ||
                    (lawId == "law_guards_dusk" && (input.Contains("换岗") || input.Contains("黄昏"))) || (lawId == "law_dragon_iron" && input.Contains("铁")))
                    laws.Add(lawId);
            }
            return new Dictionary<string, object>
            {
                ["verb"] = verb, ["target"] = target, ["item"] = item, ["place"] = place, ["laws"] = laws,
                ["difficulty"] = laws.Count > 0 ? 0.3 : 0.5, ["narration"] = "你" + Trim(input) + "。"
            };
        }

        static string FindId(List<object> entries, string input)
        {
            foreach (var e in entries.Cast<string>())
            {
                var parts = e.Split('：');
                if (parts.Length < 2) continue;
                string name = parts[1].Split(' ', '（', '[')[0];
                for (int i = 0; i + 1 < name.Length; i++)
                    if (input.Contains(name.Substring(i, 2)) && parts[0] != "whelp") return parts[0];
            }
            return null;
        }

        static string Trim(string s) => s.TrimEnd('。', '.', '！', '!').Replace("我", "");

        // ---------- 多智能体 ----------

        static Dictionary<string, object> Agent(Dictionary<string, object> per)
        {
            string id = per.Obj("you").Str("id");
            var beliefs = (per["your_beliefs"] as List<object>).Cast<string>().ToList();
            bool heardDocks = beliefs.Any(b => b.Contains("码头"));
            string verb = "wait", place = per.Str("where"), target = "", reason = "今晚先按兵不动。";
            var share = new List<object>();
            switch (id)
            {
                case "osric" when beliefs.Any(b => b.Contains("龙骨岗")):
                    verb = "guard"; place = "skull_hill"; reason = "既然知道他们要去龙骨岗，今晚亲自守在那里。"; break;
                case "vesna" when beliefs.Any(b => b.Contains("龙骨岗")):
                    verb = "search"; place = "skull_hill"; reason = "货要被送上龙骨岗？那就在半路截下。"; break;
                case "osric":
                    verb = "search"; place = heardDocks ? "docks" : "market";
                    reason = heardDocks ? "听说码头有动静，亲自带人去搜。" : "先从人多的集市查起。"; break;
                case "vesna":
                    if (heardDocks) { verb = "search"; place = "docks"; reason = "码头那孩子身上有我要的货。"; }
                    else { verb = "move"; place = "market"; reason = "去集市打听消息。"; }
                    break;
                case "marta":
                    verb = "tell"; target = "vesna"; place = "market"; reason = "那个商人出手大方，跟她说说也无妨。";
                    foreach (var b in beliefs) if (b.StartsWith("b_marta_saw_pip")) share.Add(b.Split('：')[0]);
                    break;
                case "pip":
                {
                    // 得知护送计划后坐不住：去集市给小烬弄路上吃的；被总管撞见、又不够信任言契师时会说漏嘴。
                    string plan = beliefs.FirstOrDefault(b => b.StartsWith("b_plan_pip"));
                    bool osricHere = (per["others"] as List<object>).Cast<string>().Any(o => o.StartsWith("osric@"));
                    double trust = per.Num("trust_in_player") ?? 0;
                    if (plan != null && osricHere && trust < 0.6)
                    { verb = "tell"; target = "osric"; share.Add(plan.Split('：')[0]); reason = "被总管堵住盘问，一慌就说漏了嘴。"; }
                    else if (plan != null) { verb = "move"; place = "market"; reason = "去集市给小烬弄点路上吃的。"; }
                    else { verb = "guard"; place = "docks"; reason = "今晚得守着小烬。"; }
                    break;
                }
                case "maren":
                    verb = "move"; place = "catacombs"; reason = "去墓穴确认那页纸还在。"; break;
            }
            return new Dictionary<string, object> { ["verb"] = verb, ["place"] = place, ["target"] = target, ["share"] = share, ["reason"] = reason };
        }

        // ---------- GM ----------

        static Dictionary<string, object> Direct(Dictionary<string, object> per)
        {
            double tension = per.Num("tension") ?? 0.3;
            DsMoment.TryParse(per.Str("time"), out var time);
            string ev; string reason;
            if (time.Day == 4) { ev = "mother_descends"; reason = "约定的黎明已到。"; }
            else if (tension < 0.35) { ev = "omen_roar"; reason = "张力偏低，用一声龙吼提醒倒计时。"; }
            else if (tension > 0.7) { ev = "snowfall"; reason = "张力过高，给玩家一点喘息。"; }
            else { ev = "rumor_spreads"; reason = "中段用流言推动居民行动。"; }
            return new Dictionary<string, object> { ["event"] = ev, ["intensity"] = 0.6, ["reason"] = reason };
        }

        // ---------- 造物 ----------

        static Dictionary<string, object> Craft(Dictionary<string, object> per, string input)
        {
            var mats = (per["materials"] as List<object>).Cast<string>().ToList();
            var consume = new List<object>();
            foreach (var m in mats)
            {
                string id = m.Split('：')[0];
                string name = m.Split('：')[1];
                bool mentioned = false;
                for (int i = 0; i + 1 < name.Length && !mentioned; i++) mentioned = input.Contains(name.Substring(i, 2));
                if (mentioned || (id == "ember_ash" && (input.Contains("灰") || input.Contains("余烬"))) || (id == "crystal_blue" && input.Contains("晶")))
                    consume.Add(id);
            }
            var tags = new List<object>();
            if (input.Contains("光") || input.Contains("亮")) tags.Add("light");
            if (input.Contains("余烬") || input.Contains("灰") || input.Contains("烟")) tags.Add("ember");
            if (input.Contains("鱼") || input.Contains("香") || input.Contains("引")) tags.Add("lure:dragon");
            if (input.Contains("哨") || input.Contains("声") || input.Contains("铃")) tags.Add("sound");
            if (input.Contains("暖")) tags.Add("warmth");
            if (input.Contains("安静") || input.Contains("安抚")) tags.Add("calm");
            if (tags.Count == 0) tags.Add("light");
            string itemName = input.Contains("哨") ? "余烬哨" : input.Contains("灯") ? "织光灯" : input.Contains("铃") ? "安魂铃" : "无名织物";
            return new Dictionary<string, object>
            {
                ["name"] = itemName, ["look"] = "表面流动着细细的蓝色纹路。", ["consume"] = consume, ["tags"] = tags, ["potency"] = 0.7
            };
        }
    }
}
