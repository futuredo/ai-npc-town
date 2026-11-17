using System;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Town.Core.Dragonsleep;
using Town.OpenAI;
using Town.Research.Execution;

internal static class ContinuousWorldArtifacts
{
    static JObject identity;
    static string installedRoot;
    static Action verify;
    internal static JObject Require(string root)
    {
        if (identity == null || installedRoot != root) throw new InvalidOperationException("fixed_snapshot_launcher_required");
        verify();
        return (JObject)identity.DeepClone();
    }
    internal static void ValidateLoaded(JObject manifest, Func<Assembly,string> actual)
    {
        foreach (Assembly assembly in new[] { typeof(ContinuousWorldResearch).Assembly,
            typeof(DragonsleepSession).Assembly, typeof(OpenAiDragonsleepProvider).Assembly })
        {
            string expected = (string)manifest["Files"]?[assembly.GetName().Name + ".dll"];
            if (expected == null || actual(assembly) != expected) throw new InvalidOperationException("actual_loaded_artifact_mismatch");
        }
    }
    internal static void Install(string root, string text, Func<Assembly,string> actual, Action check)
    {
        if (identity != null) throw new InvalidOperationException("snapshot_identity_already_installed");
        var manifest = JObject.Parse(text);
        if ((string)manifest["Format"] != T3Snapshot.Format || (string)manifest["Suite"] != ContinuousWorldResearch.Suite)
            throw new InvalidOperationException("wrong_snapshot_suite");
        check(); ValidateLoaded(manifest, actual);
        installedRoot = root; verify = check;
        identity = new JObject { ["build_manifest_sha256"] = T3Snapshot.Digest(System.Text.Encoding.UTF8.GetBytes(text)),
            ["files"] = manifest["Files"].DeepClone(), ["compile"] = manifest["Compile"].DeepClone(),
            ["sdk"] = manifest["Sdk"].DeepClone() };
    }
}

// 只能由固定bootstrap传入同buffer加载身份；Program普通T3入口没有安装身份，因此--no-build旧DLL拒绝。
internal static class T3SnapshotEntry
{
    public static async Task<int> Run(string root, string mode, string manifest, Func<Assembly,string> actual, Action verify)
    {
        ContinuousWorldArtifacts.Install(root, manifest, actual, verify);
        if (mode == "prepare") return ContinuousWorldResearch.Prepare(root);
        if (mode == "offline" || mode == "live") return await ContinuousWorldResearch.Run(root, mode == "live");
        throw new ArgumentException("invalid_snapshot_mode");
    }
}
