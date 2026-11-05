using System;
using System.Linq;
using System.Threading.Tasks;
using Town.Core.Knowledge;
using Town.Core.Outcome;
using Town.Core.Runtime;
using Town.Core.Social;
using Town.Core.World;
using N = Town.Core.World.Ids.Npc;
using L = Town.Core.World.Ids.Loc;
using P = Town.Core.Knowledge.Propositions;

/// <summary>
/// 《回音镇》离线演示：用规则对话跑两条路线并打印结算。位置由脚本直接回报，不代表 Unity 寻路已验收。
/// 用法：dotnet run --project dotnet/Town.Sandbox [best|rumor]
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string route = args.FirstOrDefault() ?? "best";
        using var rt = new TownRuntime();
        Console.WriteLine($"《回音镇：誓钟响起之前》离线演示 · 路线：{route}");
        Console.WriteLine();

        if (route == "rumor") await RumorRoute(rt);
        else await BestRoute(rt);

        Goto(rt, 18, 1);
        Print(rt);
        return rt.Rules.Bell.Rung ? 0 : 1;
    }

    /// <summary>最好的路线：看井、让芬恩亲手还锤、牵线拜师、把灯灵的真相说给两个独立的人。</summary>
    private static async Task BestRoute(TownRuntime rt)
    {
        Say(rt, "去旧井看一眼", () => { rt.SetPlayerLocation(L.Well); return rt.Execute(r => r.InspectWell()).Message; });
        Goto(rt, 10, 5);
        rt.SetPlayerLocation(L.Plaza);
        Say(rt, "在广场请芬恩亲手还锤", () => rt.Execute(r => r.AskFinnToReturnHammer()).Message);
        Goto(rt, 10, 15);
        Say(rt, "问芬恩的心事", async () => (await rt.TalkAsync(N.Xiaochuan, P.WantsToLearn)).Reply);
        Say(rt, "把芬恩的心事告诉布伦", () => rt.Execute(r => r.Tell(N.Amu, P.WantsToLearn, TellMode.Faithful)).Message);
        Say(rt, "告诉布伦：我亲眼看见井里是灯灵", () => rt.Execute(r => r.Tell(N.Amu, P.WellWisp, TellMode.ClaimSeen)).Message);
        Goto(rt, 11, 0);
        rt.SetPlayerLocation(L.Store);
        Say(rt, "告诉梅芙：我亲眼看见井里是灯灵", () => rt.Execute(r => r.Tell(N.Ahe, P.WellWisp, TellMode.ClaimSeen)).Message);
        await Task.CompletedTask;
    }

    /// <summary>流言路线：把梅芙的猜测说成亲眼所见，看谣言如何变成诅咒。</summary>
    private static async Task RumorRoute(TownRuntime rt)
    {
        rt.SetPlayerLocation(L.Store);
        Say(rt, "问梅芙：芬恩是不是偷了锤子", async () => (await rt.TalkAsync(N.Ahe, P.Stole)).Reply);
        rt.SetPlayerLocation(L.Plaza);
        Say(rt, "如实转述给布伦（梅芙说的）", () => rt.Execute(r => r.Tell(N.Amu, P.Stole, TellMode.Faithful)).Message);
        Say(rt, "改口：说成你亲眼看见", () => rt.Execute(r => r.Tell(N.Amu, P.Stole, TellMode.ClaimSeen)).Message);
        Say(rt, "顺便把梅芙说的恶灵也说成亲眼所见", () => rt.Execute(r => r.Tell(N.Amu, P.WellEvil, TellMode.ClaimSeen)).Message);
        await Task.CompletedTask;
    }

    private static void Goto(TownRuntime rt, int hour, int minute)
    {
        int delta = GameTime.At(1, hour, minute).TotalMinutes - rt.Session.World.Now.TotalMinutes;
        if (delta > 0) rt.Advance(delta);
    }

    private static void Say(TownRuntime rt, string action, Func<string> run) =>
        Console.WriteLine($"{rt.Session.World.Now.Hour:00}:{rt.Session.World.Now.Minute:00} 【{action}】{run()}");

    private static void Say(TownRuntime rt, string action, Func<Task<string>> run) =>
        Console.WriteLine($"{rt.Session.World.Now.Hour:00}:{rt.Session.World.Now.Minute:00} 【{action}】{run().GetAwaiter().GetResult()}");

    private static void Print(TownRuntime rt)
    {
        var rules = rt.Rules;
        Console.WriteLine();
        Console.WriteLine("—— 闲聊与人际变化 ——");
        foreach (var e in rt.Journal.Entries.Where(e => e.Kind == "gossip" || e.Kind == "relation" || e.Kind == "apprentice" || e.Kind == "help"))
            Console.WriteLine($"  {e.GameTime.Substring(e.GameTime.Length - 5)} {e.Detail}");
        Console.WriteLine();
        Console.WriteLine("—— 🔔 誓钟敲响 ——");
        foreach (var m in rules.Bell.Manifested) Console.WriteLine("  显化：" + P.All[m.PropositionId].ManifestEffect);
        if (rules.Bell.Manifested.Count == 0) Console.WriteLine("  什么也没有显化");
        Console.WriteLine($"  拱门修复：{rt.Session.World.GetFlag(Ids.Flag.BenchProgress)}%   拜师：{rules.Apprentice.State}");
        Console.WriteLine();
        Console.WriteLine("—— 结局 ——");
        foreach (var e in FestivalOutcome.Endings(rules)) Console.WriteLine($"  {e.Title}：{e.Description}");
        Console.WriteLine();
        Console.WriteLine("—— 《回音镇灯节小报》 ——");
        foreach (var h in FestivalOutcome.Headlines(rules)) Console.WriteLine("  · " + h);
        Console.WriteLine();
        Console.WriteLine("—— 流言溯源 ——");
        foreach (var line in FestivalOutcome.Trace(rules)) Console.WriteLine("  " + line);
    }
}
