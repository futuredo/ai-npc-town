using System;
using Town.Core.Rules;
using Town.Core.Social;

namespace Town.Core.Runtime
{
    // 持久化 ID：只能追加，不要调整已有枚举值。未知命令必须拒绝，不做猜测性迁移。
    public enum TownCommandKind
    {
        InspectShelter = 1, TakeHammer = 2, InspectWell = 3, ShowEvidence = 4,
        ReturnHammer = 5, Tell = 6, AskFinnToReturn = 7, Promise = 8, Apologize = 9,
        MeetKeeper = 10, ReadTablet = 11, ReadLastPage = 12
    }

    /// <summary>UI、存档与测试共用的显式玩法命令。没有任意代码、反射或模型动作执行入口。</summary>
    public sealed class TownCommand
    {
        public TownCommandKind Kind { get; }
        public string NpcId { get; }
        public string PropositionId { get; }
        public TellMode Mode { get; }
        public TownCommand(TownCommandKind kind, string npcId = null, string propositionId = null, TellMode mode = TellMode.Faithful)
        { Kind = kind; NpcId = npcId; PropositionId = propositionId; Mode = mode; }

        internal CommandResult Apply(HammerEventRules rules)
        {
            switch (Kind)
            {
                case TownCommandKind.InspectShelter: return rules.InspectShelter();
                case TownCommandKind.TakeHammer: return rules.TakeHammer();
                case TownCommandKind.InspectWell: return rules.InspectWell();
                case TownCommandKind.ShowEvidence: return rules.ShowEvidence(NpcId);
                case TownCommandKind.ReturnHammer: return rules.ReturnHammer();
                case TownCommandKind.Tell: return rules.Tell(NpcId, PropositionId, Mode);
                case TownCommandKind.AskFinnToReturn: return rules.AskFinnToReturnHammer();
                case TownCommandKind.Promise: return rules.PromiseToBrunn();
                case TownCommandKind.Apologize: return rules.ApologizeToFinn();
                case TownCommandKind.MeetKeeper: return rules.MeetKeeper();
                case TownCommandKind.ReadTablet: return rules.Story.ReadTablet(out _);
                case TownCommandKind.ReadLastPage: return rules.ReadLastPage();
                default: throw new ArgumentException("未知玩法命令。");
            }
        }
    }
}
