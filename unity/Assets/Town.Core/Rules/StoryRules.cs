using System;
using System.Collections.Generic;
using Town.Core.Content;
using Town.Core.Outcome;
using Town.Core.Tasks;
using Town.Core.World;

namespace Town.Core.Rules
{
    public enum StoryChapter { BlankPage, LostHammer, Fermentation, Closing, Inkfall }

    /// <summary>已经发生的剧情提示。独立保留，不随有界审计日志淘汰；不属于居民认知。</summary>
    public sealed class StoryBeat
    {
        public string Id { get; }
        public GameTime At { get; }
        public string Text { get; }

        internal StoryBeat(string id, GameTime at, string text) { Id = id; At = at; Text = text; }
    }

    /// <summary>
    /// 第七章的文字骨架。只改变剧情进度，不替居民相信、不传话、不接管日程。
    /// 霍勒斯是固定的规则引导，不加入居民列表、闲聊或墨钟计数。
    /// </summary>
    public sealed class StoryRules
    {
        private readonly HammerEventRules _rules;
        private readonly Action<string, string> _note;
        private readonly HashSet<string> _consumed = new HashSet<string>();
        private readonly List<StoryBeat> _beats = new List<StoryBeat>();
        private readonly IReadOnlyList<StoryBeat> _history;
        private string _sealedLastPage;
        private WorldState World => _rules.World;

        public IReadOnlyList<StoryBeat> History => _history;
        public bool HasBookmark { get; private set; }
        public bool HasReadLastPage { get; private set; }
        public string LastPageText => HasReadLastPage ? _sealedLastPage : null;
        public StoryChapter Chapter { get; private set; }
        public string ChapterTitle
        {
            get
            {
                switch (Chapter)
                {
                    case StoryChapter.LostHammer: return "第一幕 · 失物";
                    case StoryChapter.Fermentation: return "第二幕 · 发酵";
                    case StoryChapter.Closing: return "第三幕 · 收束";
                    case StoryChapter.Inkfall: return "落墨 · 第七章正文";
                    default: return "开场 · 空白页";
                }
            }
        }

        public StoryRules(HammerEventRules rules, Action<string, string> note)
        {
            _rules = rules;
            _note = note ?? ((_, __) => { });
            _history = _beats.AsReadOnly();
        }

        public void Tick()
        {
            Chapter = _rules.Bell.Rung ? StoryChapter.Inkfall :
                World.Now >= At(15, 0) ? StoryChapter.Closing :
                World.Now >= At(12, 0) ? StoryChapter.Fermentation :
                World.Now >= At(9, 10) ? StoryChapter.LostHammer : StoryChapter.BlankPage;

            Cue("blank_page", 9, 0, "你掉进了一本没写完的书。口袋里有封着墨印的最后一页：等钟响之后再读。先去钟楼，找守钟人霍勒斯。");
            Cue("missing_hammer", 9, 10, World.RequireItem(Ids.Item.Hammer).HolderId == Ids.Npc.Amu
                ? "第一幕 · 失物。符文锤已经回到布伦手中；他正赶着让拱门重新上色。"
                : "第一幕 · 失物。布伦在广场求助：『我的符文锤不见了。日落前修不好，拱门就上不了色。』");
            Cue("finn_morning", 11, 40, "页边批注：芬恩又往铁匠铺去了，半边身子的铅笔线轻轻发抖。他似乎有话说。",
                _rules.Apprentice.State != ApprenticeState.Formal && !_rules.Session.Schedule.IsTaskControlled(Ids.Npc.Xiaochuan));
            Cue("fermentation", 12, 0, "第二幕 · 发酵。上午说出口的话，正在别人的故事里变样。");
            Cue("finn_afternoon", 13, 40, "页边批注：芬恩又在惦记铁匠铺。信任你的人，才会说出没有写在介绍里的心事。",
                _rules.Apprentice.State != ApprenticeState.Formal && !_rules.Session.Schedule.IsTaskControlled(Ids.Npc.Xiaochuan));
            Cue("closing", 15, 0, "第三幕 · 收束。离落墨还有三个小时；真话写得慢，谎话写得快。");
            Cue("arch_deadline", 16, 50, "布伦望着拱门：『修符文要一个小时。锤子再不回来，就来不及了。』",
                _rules.Repair.State == RepairTaskState.WaitingForHammer);
            Cue("preparation_bell", 17, 0, "霍勒斯敲响预备钟：『还有一个时辰，天要落墨了。一个人的话，转述一百遍，也仍是一个源头。』");

            if (_rules.Bell.Rung && _sealedLastPage == null)
            {
                // 钟响时封存，之后的对话、调查不能改写作者对这一局的判词。
                _sealedLastPage = FestivalOutcome.LastPage(_rules);
                Emit("inkfall", "墨钟响了三声，被共同相信的说法写成了正文。回钟楼找霍勒斯，拆开最后一页。");
            }
        }

        public CommandResult MeetKeeper()
        {
            if (World.PlayerLocationId != Ids.Loc.BellTower) return CommandResult.Reject("请先走到钟楼，找守钟人霍勒斯。");
            if (HasBookmark) return CommandResult.Ok("霍勒斯：『一人之信不落墨：至少两位居民相信；一人之言不落墨：至少两个源头；读者之信不落墨：你自己相信不算。书签还在你手里，去读石板吧。』");
            HasBookmark = true;
            World.Commit("story:bookmark", _ => { });
            const string guidance = "霍勒斯：『又一个从页外来的人。一人之信不落墨：至少两位居民相信；一人之言不落墨：至少两个源头；读者之信不落墨：你自己相信不算。』他递给你书签：『拿着它读石板。最后一页，等钟响之后再读。』";
            Emit("bookmark", guidance);
            return CommandResult.Ok(guidance);
        }

        public CommandResult ReadLastPage()
        {
            if (World.PlayerLocationId != Ids.Loc.BellTower) return CommandResult.Reject("请回钟楼，在霍勒斯面前拆开最后一页。");
            if (!_rules.Bell.Rung || _sealedLastPage == null) return CommandResult.Reject("墨印还没有松开：等钟响之后再读。");
            if (!HasReadLastPage)
            {
                HasReadLastPage = true;
                World.Commit("story:last_page", _ => { });
                Emit("last_page", "你在霍勒斯面前拆开了最后一页。");
            }
            return CommandResult.Ok(_sealedLastPage);
        }

        public CommandResult ReadTablet(out IReadOnlyList<BellReading> readings)
        {
            readings = null;
            if (World.PlayerLocationId != Ids.Loc.BellTower) return CommandResult.Reject("请先走到钟楼读石板。");
            if (!HasBookmark) return CommandResult.Reject("先和霍勒斯交谈，拿到阅读石板的书签。");
            // 钟响后读封存读数；读前不暴露内容，读取也不改变任何认知。
            readings = _rules.Bell.Rung ? _rules.Bell.FinalReadings : _rules.Bell.Read();
            return CommandResult.Ok("石板上写着：每条说法有几人相信、来自几个源头。霍勒斯替你读出页边的批注。");
        }

        private static GameTime At(int hour, int minute) => GameTime.At(1, hour, minute);

        private void Cue(string id, int hour, int minute, string text, bool applicable = true)
        {
            if (World.Now < At(hour, minute) || !_consumed.Add(id)) return;
            if (applicable) Emit(id, text);
        }

        private void Emit(string id, string text)
        {
            _beats.Add(new StoryBeat(id, World.Now, text));
            _note("story", id + ":" + text);
        }
    }
}
