using System;
using System.IO;
using System.Threading.Tasks;
using Town.Research.Execution;

// 独立bootstrap只允许固定T3构建/启动，不引用Research/Core/OpenAI，故三者尚未加载。
internal static class T3LauncherProgram
{
    static async Task<int> Main(string[] args)
    {
        string root = Directory.GetCurrentDirectory();
        if (!File.Exists(Path.Combine(root, "dotnet", "Town.sln")) || args.Length != 1) return 2;
        try
        {
            if (args[0] == "--build") return await T3Snapshot.Build(root);
            if (args[0] == "--offline") return await T3Snapshot.Launch(root, "offline");
            if (args[0] == "--prepare") return await T3Snapshot.Launch(root, "prepare");
            if (args[0] == "--live") return await T3Snapshot.Launch(root, "live");
            return 2;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("T3固定制品入口拒绝/停止；保留构建与实验目录，不输出异常正文，不重试。");
            return 2;
        }
    }
}
