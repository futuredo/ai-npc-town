namespace Town.Core.Dragonsleep
{
    /// <summary>《龙眠镇》开局内容：地点、居民、认知、规律、物件。设计依据见 docs/龙眠镇_世界观玩法与AI架构_v6.md。</summary>
    public static class DsContent
    {
        public static string ItemTagName(string tag)
        {
            switch (tag)
            {
                case "light": return "微光照明"; case "ember": return "余烬气息"; case "lure:dragon": return "吸引雏龙";
                case "quiet": return "消减声响"; case "calm": return "安抚"; case "warmth": return "温暖";
                case "iron": return "冷铁"; case "truth": return "契约真相"; case "binding": return "束缚";
                case "sound": return "发声"; case "food": return "可食用"; case "smell": return "气味";
                default: return tag.StartsWith("potency:") ? "强度 " + tag.Substring(8) : tag;
            }
        }
        public static DsWorld NewWorld(int seed = 20261108)
        {
            var w = new DsWorld { Seed = seed };

            w.Places["market"] = "集市广场";
            w.Places["cathedral"] = "大教堂";
            w.Places["catacombs"] = "墓穴";
            w.Places["docks"] = "码头旧船屋";
            w.Places["castle"] = "王城";
            w.Places["workshop"] = "附魔工坊";
            w.Places["skull_hill"] = "龙骨岗";

            Actor(w, "osric", "奥斯里克·灰岩", "王室总管", "market",
                "精明、讲体面、习惯发号施令；说话简短，常提“王室的利益”。",
                "抓住雏龙关进王城铁笼，当作与母龙谈判的筹码和自己的功劳。",
                new[] { "王室威望", "秩序", "个人功劳" }, new[] { "crown", "safety", "credit" }, 0.2f);
            Actor(w, "maren", "梅伦修女", "大教堂看守", "cathedral",
                "温和但固执，敬畏古老的东西；回避“教会犯过错”这个话题。",
                "让雏龙回到墓穴“圣所”，维护教会的名誉。",
                new[] { "信仰", "教会名誉", "怜悯" }, new[] { "faith", "mercy", "truth" }, 0.3f);
            Actor(w, "marta", "玛尔妲", "集市厨娘", "market",
                "嗓门大、热心、爱抱怨；什么都看在眼里。",
                "找出偷她熏鱼的小贼。",
                new[] { "勤劳", "公道", "邻里" }, new[] { "fairness", "food", "children" }, 0.35f);
            Actor(w, "pip", "皮普", "码头孤儿", "docks",
                "机灵、警惕、嘴硬心软；不信大人。",
                "保护“小烬”，谁也不告诉。",
                new[] { "朋友", "自由", "不被骗" }, new[] { "friendship", "mercy", "honesty" }, 0.1f);
            Actor(w, "vesna", "维丝娜", "外乡商人", "market",
                "笑容可掬、话里有话；总在估价。",
                "买下或偷走雏龙，运到海外卖个天价。",
                new[] { "利润", "机会", "自保" }, new[] { "profit", "deal", "safety" }, 0.25f);

            // ---- 认知（每人只知道自己的部分）----
            B(w, "b_osric_egg_moved", "osric", "egg", "是我下令把龙蛋从墓穴搬进王城库房，靠炉火烘着——没想到它会孵出来。", 0.75f);
            B(w, "b_osric_iron", "osric", "whelp", "老卫兵说过，龙崽碰到冷铁就会浑身发软。我已经让铁匠赶制铁笼。", 0.35f);
            B(w, "b_osric_order", "osric", "whelp", "我已下令卫兵在第二天搜城，找到雏龙一律送进王城。", 0.0f);
            B(w, "b_maren_pact", "maren", "pact", "三百年前死的是母龙的配偶，不是母龙。她与镇子立约：我们守护她的蛋，她不复仇。", 0.65f);
            B(w, "b_maren_page", "maren", "pact", "契约那一页被教会撕下，藏在墓穴最深处的圣物龛里。", 0.85f);
            B(w, "b_maren_egg_gone", "maren", "egg", "龙蛋原本供奉在墓穴里，七天前不见了，我不知道是谁拿走的。", 0.0f);
            B(w, "b_marta_fish", "marta", "fish", "这几天我的熏鱼每晚都少几条。", 0.0f);
            B(w, "b_marta_saw_pip", "marta", "whelp", "前天半夜我看见皮普抱着一团冒烟的毯子往码头跑。", 0.2f);
            B(w, "b_marta_vesna", "marta", "vesna", "那个外乡商人到处打听谁见过“会冒烟的蜥蜴”。", 0.1f);
            B(w, "b_pip_whelp_place", "pip", "whelp", "小烬藏在码头旧船屋里。", 0.8f);
            B(w, "b_pip_fish", "pip", "whelp", "小烬最爱吃熏鱼，一闻到就会探出头。", 0.45f);
            B(w, "b_pip_iron", "pip", "whelp", "小烬碰过一次铁钩，吓得一整天发抖。", 0.5f);
            B(w, "b_pip_mother", "pip", "whelp", "小烬每天晚上都朝雪山那边叫。", 0.2f);
            B(w, "b_vesna_buyer", "vesna", "vesna", "海那边的伯爵愿意为一条活龙付一船金子。", 0.9f);
            B(w, "b_vesna_poachers", "vesna", "vesna", "我雇的人已经在镇外林地布好了网。", 0.95f);
            B(w, "b_vesna_ember", "vesna", "dragon", "龙认亲靠“余烬的气味”——古书里这么写，我收过一本。", 0.4f);

            // ---- 世界规律（语义判定依据）----
            L(w, "law_whelp_fish", "雏龙抵挡不了熏鱼的气味。", new[] { "fish", "food", "lure:dragon" }, null, false);
            L(w, "law_dragon_iron", "龙厌恶冷铁，碰到会虚弱、恐惧。", new[] { "iron" }, new[] { "iron" }, false);
            L(w, "law_dragon_ember", "龙靠余烬气味辨认亲族。", new[] { "ember", "lure:dragon" }, null, false);
            L(w, "law_fear_noise", "受惊的动物会躲得更深；安静和耐心让它们靠近。", new[] { "quiet", "patience" }, new[] { "noise", "force" }, true);
            L(w, "law_guards_dusk", "卫兵在黄昏换岗，城门和各处岗哨有片刻无人。", new[] { "sneak", "rescue" }, null, false);
            L(w, "law_loom", "织言机只织造与材料相称的东西：材料越少，造物越弱。", null, null, true);

            // ---- 物件 ----
            I(w, "coin_purse", "钱袋", "player", "coin");
            I(w, "travel_cloak", "旅行斗篷", "player", "cloth", "quiet");
            I(w, "smoked_fish", "一串熏鱼", "marta", "fish", "food", "smell", "price:3");
            I(w, "iron_lock", "冷铁笼锁", "vesna", "iron", "metal", "price:8");
            I(w, "ember_ash", "一包余烬灰", "place:workshop", "ember", "ash");
            I(w, "crystal_blue", "蓝晶体", "place:workshop", "crystal", "light", "magic");
            I(w, "pact_page", "撕下的契约页", "place:catacombs", "pact", "paper", "truth");
            I(w, "rope", "粗麻绳", "place:docks", "rope");

            w.Chronicle.Add("三天前，墓穴里的龙蛋孵化了。今晨，有人看见苍穹之母在雪峰上空盘旋。");
            return w;
        }

        static void Actor(DsWorld w, string id, string name, string role, string place, string persona, string goal,
            string[] values, string[] persuadable, float trust)
        {
            w.Actors[id] = new DsActor
            {
                Id = id, Name = name, Role = role, Place = place, Persona = persona, Goal = goal,
                Values = values, Persuadable = persuadable, Trust = trust
            };
        }

        static void B(DsWorld w, string id, string owner, string topic, string text, float revealTrust) =>
            w.Beliefs.Add(new DsBelief { Id = id, Owner = owner, Topic = topic, Text = text, RevealTrust = revealTrust });

        static void L(DsWorld w, string id, string text, string[] support, string[] hinder, bool common) =>
            w.Laws[id] = new DsLaw
            {
                Id = id, Text = text, Common = common,
                SupportTags = support ?? System.Array.Empty<string>(), HinderTags = hinder ?? System.Array.Empty<string>()
            };

        static void I(DsWorld w, string id, string name, string holder, params string[] tags)
        {
            var item = new DsItem { Id = id, Name = name, Holder = holder };
            foreach (var t in tags) item.Tags.Add(t);
            w.Items[id] = item;
        }

        /// <summary>某条认知被说出后，玩家可引用的规律（谈话→可用于判定）。</summary>
        public static string LawUnlockedBy(string beliefId)
        {
            switch (beliefId)
            {
                case "b_pip_fish": return "law_whelp_fish";
                case "b_osric_iron":
                case "b_pip_iron": return "law_dragon_iron";
                case "b_vesna_ember": return "law_dragon_ember";
                default: return null;
            }
        }
    }
}
