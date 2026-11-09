using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    public static partial class DsRules
    {
        static readonly Dictionary<string, string[]> TagSources = new Dictionary<string, string[]>
        {
            ["light"] = new[] { "crystal", "light" }, ["ember"] = new[] { "ember", "ash" }, ["warmth"] = new[] { "ember" },
            ["lure:dragon"] = new[] { "fish", "ember", "smell" }, ["quiet"] = new[] { "cloth", "quiet" },
            ["calm"] = new[] { "fish", "crystal", "cloth" }, ["iron"] = new[] { "iron" }, ["truth"] = new[] { "pact" },
            ["binding"] = new[] { "rope", "iron" }, ["sound"] = new[] { "crystal", "metal", "coin" },
        };

        // 提示与执行共享材料能力表，避免模型只能猜测这个世界允许怎样织造。
        public static string[] CraftEffectsFor(IEnumerable<string> materialTags)
        {
            var tags = new HashSet<string>(materialTags);
            return TagSources.Where(kv => kv.Value.Any(tags.Contains)).Select(kv => kv.Key)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        public static string CraftEffectMeaning(string tag)
        {
            switch(tag)
            {
                case "light": return "照明；使用后照亮当前位置，降低搜索难度";
                case "quiet": return "吸收、减弱声响，包括脚步声；使用后降低当前位置潜行难度";
                case "calm": return "安抚身边的雏龙，减轻恐惧";
                case "lure:dragon": return "气味诱引身边的雏龙现身；食材织成的诱饵还可以喂食";
                case "ember": return "余烬气味属性，可与已发现的龙类规律关联";
                case "warmth": return "温暖属性，目前没有独立主动使用效果";
                case "iron": return "冷铁属性；对龙有恐惧阻碍";
                case "truth": return "契约属性；不能凭此凭空生成事实或线索";
                case "binding": return "绳结束缚属性，目前没有独立主动使用效果";
                case "sound": return "发声属性，不是吸收或减弱声音；目前没有独立主动使用效果";
                default: return tag;
            }
        }

        public static void Craft(DsWorld w, Dictionary<string, object> p, AiTrace t)
        {
            if (w.PlayerPlace != "workshop") { Reject(t, "织言机在附魔工坊，你得先去那里", "craft_place_required"); return; }
            string name = p.Str("name"), look = p.Str("look") ?? "";
            var consume = p.StrList("consume");
            var tags = p.StrList("tags");
            double potency = Clamp(p.Num("potency") ?? 0.3, 0, 1);
            if (string.IsNullOrWhiteSpace(name) || consume == null || tags == null) { Fail(t, "schema_invalid", "缺少 name/consume/tags"); return; }
            var mats = new List<DsItem>();
            foreach (var id in consume.Distinct())
            {
                if (!w.Items.TryGetValue(id, out var it) || !(it.Holder == "player" || it.Holder == "place:workshop") || it.Tags.Contains("pact"))
                { t.ValidationIssues.Add("不可用的材料：" + id); continue; }
                mats.Add(it);
            }
            if (mats.Count == 0) { Reject(t, "织言机需要至少一样可用材料", "craft_material_required"); return; }
            var matTags = new HashSet<string>(mats.SelectMany(m => m.Tags));
            var granted = new List<string>();
            foreach (var tag in tags.Distinct())
            {
                if (!TagSources.TryGetValue(tag, out var sources)) { t.ValidationIssues.Add("不允许的效果标签：" + tag); continue; }
                if (!sources.Any(matTags.Contains)) { t.Verdict.Notes.Add("材料里没有能支撑「" + tag + "」的东西，已剔除"); continue; }
                granted.Add(tag);
            }
            if (granted.Count == 0) { Reject(t, "描述的效果没有一项能由这些材料织成", "craft_effect_unsupported"); return; }
            double cap = Math.Min(0.9, 0.2 + 0.2 * mats.Count);
            if (potency > cap) { t.Verdict.Notes.Add($"强度 {potency:0.00} 超过材料上限 {cap:0.00}，已压低"); potency = cap; }
            int n = w.Items.Values.Count(i => i.Crafted) + 1;
            var item = new DsItem
            {
                Id = "crafted_" + n, Name = name.Length > 16 ? name.Substring(0, 16) : name,
                Holder = "player", Look = look, Crafted = true
            };
            foreach (var g in granted) item.Tags.Add(g);
            if (granted.Contains("lure:dragon")) { if (matTags.Contains("food")) item.Tags.Add("food"); item.Tags.Add("smell"); }
            if (granted.Contains("ember")) item.Tags.Add("ember");
            item.Tags.Add("potency:" + potency.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var m in mats) Move(t, m, "consumed");
            w.Items[item.Id] = item;
            t.Changes.Add(new WorldChange
            {
                Path = "items." + item.Id, Before = "", After = item.Name, Note = string.Join(",", granted) + $" 强度{potency:0.0}"
            });
            t.Verdict.Kind = granted.Count == tags.Count && mats.Count == consume.Count ? VerdictKind.Accepted : VerdictKind.Partial;
            t.Verdict.Reason = $"消耗 {mats.Count} 样材料，织成 {granted.Count} 项效果";
            t.Presented = "织言机的梭子亮起蓝光——「" + item.Name + "」成形了。" + look;
        }
    }
}
