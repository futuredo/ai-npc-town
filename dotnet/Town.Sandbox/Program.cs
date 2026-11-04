using System;
using System.Threading.Tasks;
using Town.Core.Knowledge;
using Town.Core.Runtime;
using Town.Core.World;

internal static class Program
{
    private static async Task<int> Main()
    {
        using var runtime = new TownRuntime();
        Console.WriteLine("离线骨架演示：位置直接回报由测试驱动模拟，不代表 Unity 寻路已验收。");
        Console.WriteLine("阿木：" + (await runtime.TalkAsync(Ids.Npc.Amu, Propositions.Location)).Reply);
        runtime.SetPlayerLocation(Ids.Loc.Shelter);
        if (!runtime.Execute(r => r.InspectShelter()).Success || !runtime.Execute(r => r.TakeHammer()).Success) return 1;
        runtime.SetPlayerLocation(Ids.Loc.Plaza);
        if (!runtime.Execute(r => r.ShowEvidence(Ids.Npc.Amu)).Success || !runtime.Execute(r => r.ReturnHammer()).Success) return 1;
        runtime.Advance(60);
        foreach (var e in runtime.Journal.Entries)
            Console.WriteLine($"{e.Sequence:00} {e.GameTime} [{e.Kind}] {e.Detail}");
        Console.WriteLine("长凳进度：" + runtime.Session.World.GetFlag(Ids.Flag.BenchProgress) + "%");
        return runtime.Session.World.GetFlag(Ids.Flag.BenchProgress) == 100 ? 0 : 1;
    }
}
