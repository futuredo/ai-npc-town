using System;
using System.Collections.Generic;
using System.Linq;
using Town.Core.Dragonsleep;

/// <summary>闭环修复后新增的状态迁移集；不改写 M2 冻结集。第一次真实请求前冻结，结果独立报告。</summary>
internal static class PlanTransferCases
{
    public static List<PlanCase> All()
    {
        PlanCase C(string id, string source, string input, string expect, Action<DragonsleepSession> change) => new PlanCase
        {
            Id = id, Group = "transfer", Scenario = id, Input = input, Expect = expect,
            Setup = s => { PlanCases.Scenarios[source](s); change(s); },
            Check = r => r.Default.ReachesGoal,
        };
        return new List<PlanCase>
        {
            C("t01_no_money", "fresh", "钱已经花光了，仍想让小烬平安回家，想个不花钱的办法。", "无钱起点；首案覆盖团聚", s => s.World.Coins = 0),
            C("t02_scared_no_food", "bonded_docks", "鱼已经吃完，小烬吓坏了不肯跟我走。先安抚，再送它回家。", "没有可用食物且恐惧最高；首案覆盖团聚", s =>
            { s.World.Items["smoked_fish"].Holder = "consumed"; s.World.Whelp.Fear = 1; }),
            C("t03_last_dusk_cage", "caged", "已经是最后一个黄昏，小烬还在笼里。尽力救它并赶上团聚。", "最后黄昏4点时间；首案覆盖团聚", s =>
            { s.World.Day = 3; s.World.Phase = DayPhase.Dusk; }),
            C("t04_last_half_phase", "cloaked_workshop", "小烬已经裹好跟着我，最后黄昏只剩一点准备时间，赶紧送它回家。", "最后黄昏2点时间；首案覆盖团聚", s =>
            {
                s.World.Day = 3; s.World.Phase = DayPhase.Dusk;
                s.RunStepAsync(new DsPlanStep { Verb = "observe" }.ToProposal("workshop"), "离线设置已花两点时间").GetAwaiter().GetResult();
            }),
            C("t05_left_without_cloak", "left_workshop", "斗篷弄丢了，小烬还藏在工坊，去接上它再带它回家。", "留置雏龙、无斗篷；首案覆盖团聚", s =>
            { s.World.Items["travel_cloak"].Holder = "place:castle"; }),
            C("t06_low_bond_no_food", "bonded_docks", "今天已经第三天了，没鱼也没钱，小烬还不太信我，继续想办法送它回去。", "第三天信任0.2且无食物；首案覆盖团聚", s =>
            { s.World.Day = 3; s.World.Phase = DayPhase.Dawn; s.World.Coins = 0; s.World.Items["smoked_fish"].Holder = "consumed"; s.World.Whelp.TrustPlayer = 0.2f; }),
        };
    }
}
