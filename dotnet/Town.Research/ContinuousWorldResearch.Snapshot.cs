using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Town.Research.Execution
{
    // 仅T3-2使用：固定项目、固定制品目录、固定入口；不接受任意DLL/类型/脚本。
    internal static class T3Snapshot
    {
        internal const string Suite = "ds-continuous-world-t3-2";
        internal const string Format = "t3-isolated-build-1";
        internal static string Folder(string root) => Path.Combine(root, ".local", "continuous-world-build", Suite);
        internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        static string TextHash(string s) => Digest(Encoding.UTF8.GetBytes(s));
        static readonly string[] Projects = { "Town.Research", "Town.Core", "Town.OpenAI", "Town.PieGame", "PieGame.AI.Kernel" };
        static readonly string[] Trees = { "dotnet/Town.Research", "dotnet/Town.Research.T3Launcher",
            "unity/Assets/Town.Core", "unity/Assets/Town.OpenAI", "unity/Assets/Town.PieGame", "unity/Assets/PieGame.AI.Kernel" };
        static bool Generated(string path) => path.Replace('\\', '/').Split('/').Any(p => p == "bin" || p == "obj");

        internal static SortedDictionary<string,string> Sources(string root)
        {
            var result = new SortedDictionary<string,string>(StringComparer.Ordinal);
            foreach (string tree in Trees)
            {
                string dir = Path.Combine(root, tree);
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => !Generated(Path.GetRelativePath(dir, f)) &&
                        (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal) ||
                            Path.GetFileName(f) == "Directory.Build.props" || Path.GetFileName(f) == "Directory.Build.targets")))
                    result.Add(Path.GetRelativePath(root, f).Replace('\\', '/'), Digest(File.ReadAllBytes(f)));
            }
            foreach (string project in Projects.Skip(1))
            {
                string p = "dotnet/" + project + "/" + project + ".csproj";
                if (File.Exists(Path.Combine(root, p))) result[p] = Digest(File.ReadAllBytes(Path.Combine(root, p)));
            }
            // 仅构建配置，不枚举UI、资源、测试工程或预算。当前仓库没有共享props/targets，也覆盖以后新增。
            foreach (string dir in new[] { "", "dotnet", "unity", "unity/Assets" }.Concat(Trees)
                .Concat(Projects.Select(p => "dotnet/" + p)).Distinct(StringComparer.Ordinal))
            foreach (string name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config", "nuget.config", "global.json" })
            {
                string p = Path.Combine(dir, name).Replace('\\', '/');
                if (File.Exists(Path.Combine(root, p))) result[p] = Digest(File.ReadAllBytes(Path.Combine(root, p)));
            }
            return result;
        }

        internal static bool Same(SortedDictionary<string,string> a, SortedDictionary<string,string> b) =>
            a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out string value) && value == p.Value);
        internal static void Need(bool ok, string code) { if (!ok) throw new InvalidOperationException(code); }
        static void NewFile(string path, string value)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] bytes = Encoding.UTF8.GetBytes(value); stream.Write(bytes); stream.Flush(true);
        }
        static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

        internal sealed class Manifest
        {
            public Manifest() { }
            public string Format { get; set; }
            public string Suite { get; set; }
            public string Sdk { get; set; }
            public SortedDictionary<string,string> Sources { get; set; }
            public SortedDictionary<string,string> Files { get; set; }
            public SortedDictionary<string,string[]> Compile { get; set; }
        }

        // 唯一生成凭据的路径：新目录内复制受控源码，调用固定项目非增量publish；没有为现有DLL补凭据的入口。
        internal static async Task<int> Build(string root)
        {
            string dir = Folder(root);
            Need(!Directory.Exists(dir), "snapshot_exists_no_overwrite");
            Directory.CreateDirectory(dir);
            NewFile(Path.Combine(dir, "BUILD_STARTED"), DateTime.UtcNow.ToString("O"));
            var sources = Sources(root);
            foreach (string p in Projects) Need(sources.ContainsKey("dotnet/" + p + "/" + p + ".csproj"), "project_missing");
            string stage = Path.Combine(dir, "source");
            foreach (var p in sources)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(root, p.Key));
                Need(Digest(bytes) == p.Value, "source_changed_while_copying");
                string target = Path.Combine(stage, p.Key); Directory.CreateDirectory(Path.GetDirectoryName(target));
                using var f = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); f.Write(bytes);
            }
            string output = Path.Combine(dir, "payload");
            string compileDir = Path.Combine(dir, "compile"); Directory.CreateDirectory(compileDir);
            string targets = Path.Combine(dir, "t3.capture.targets");
            NewFile(targets, "<Project><Target Name=\"T3Capture\" BeforeTargets=\"CoreCompile\"><WriteLinesToFile File=\"$(T3CompileDirectory)/$(MSBuildProjectName).txt\" Lines=\"@(Compile->'%(FullPath)')\" Overwrite=\"true\" /></Target></Project>");
            string sdk = await Dotnet(root, dir, "sdk", "--version");
            // 仅本机已安装包缓存；不访问远程NuGet源。离线缺包应失败，不自动下载。
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            await Dotnet(stage, dir, "publish", "publish", "dotnet/Town.Research/Town.Research.csproj", "-c", "Release",
                "-o", output, "--no-self-contained", "-p:NoIncremental=true", "-p:UseSharedCompilation=false",
                "-p:RestoreSources=" + packages, "-p:NuGetAudit=false", "-p:CustomAfterMicrosoftCommonTargets=" + targets,
                "-p:T3CompileDirectory=" + compileDir);
            Need(Same(sources, Sources(root)) && sources.All(p => Digest(File.ReadAllBytes(Path.Combine(stage, p.Key))) == p.Value),
                "source_changed_during_build");
            var compile = new SortedDictionary<string,string[]>(StringComparer.Ordinal);
            foreach (string project in Projects)
            {
                string[] files = File.ReadAllLines(Path.Combine(compileDir, project + ".txt"))
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(f => Path.GetRelativePath(stage, f).Replace('\\', '/'))
                    .OrderBy(s => s, StringComparer.Ordinal).ToArray();
                Need(files.Length > 0 && files.All(f => !f.StartsWith("../", StringComparison.Ordinal) &&
                    (sources.ContainsKey(f) || Generated(f))), "unregistered_compile_source");
                string tree = project == "Town.Research" ? "dotnet/Town.Research/" : "unity/Assets/" + project + "/";
                Need(sources.Keys.Where(p => p.StartsWith(tree, StringComparison.Ordinal) && p.EndsWith(".cs", StringComparison.Ordinal))
                    .All(p => files.Contains(p, StringComparer.Ordinal)), "source_not_compiled");
                compile.Add(project, files);
            }
            var artifacts = new SortedDictionary<string,string>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(output).Where(f => f.EndsWith(".dll", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal)))
                artifacts.Add(Path.GetFileName(file), Digest(File.ReadAllBytes(file)));
            foreach (string name in Projects) Need(artifacts.ContainsKey(name + ".dll"), "required_artifact_missing");
            var manifest = new Manifest { Format = Format, Suite = Suite, Sdk = sdk.Trim(), Sources = sources, Files = artifacts, Compile = compile };
            string text = Json(manifest);
            NewFile(Path.Combine(dir, "build.json"), text);
            NewFile(Path.Combine(dir, "build.sha256"), TextHash(text) + "\n");
            Console.WriteLine("T3固定快照：" + dir);
            return 0;
        }

        static async Task<string> Dotnet(string cwd, string dir, string label, params string[] args)
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = cwd, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var p = Process.Start(start) ?? throw new InvalidOperationException("dotnet_not_started");
            Task<string> stdout = p.StandardOutput.ReadToEndAsync(), stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(); string text = await stdout;
            NewFile(Path.Combine(dir, label + ".stdout.log"), text);
            NewFile(Path.Combine(dir, label + ".stderr.log"), await stderr);
            Need(p.ExitCode == 0, "isolated_build_failed"); return text;
        }

        internal sealed class Loaded : AssemblyLoadContext
        {
            readonly Dictionary<string,byte[]> buffers;
            readonly Dictionary<Assembly,string> hashes = new Dictionary<Assembly,string>();
            readonly HashSet<string> platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
                .Split(Path.PathSeparator).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
            internal Manifest Manifest { get; }
            internal string ManifestText { get; }
            readonly string root;
            internal Loaded(string root) : base("t3-fixed-snapshot", isCollectible: true)
            {
                this.root = root;
                string dir = Folder(root);
                ManifestText = File.ReadAllText(Path.Combine(dir, "build.json"));
                Need(TextHash(ManifestText) == File.ReadAllText(Path.Combine(dir, "build.sha256")).Trim(), "build_manifest_changed");
                Manifest = JsonSerializer.Deserialize<Manifest>(ManifestText);
                Need(Manifest?.Format == Format && Manifest.Suite == Suite && Same(Manifest.Sources, Sources(root)), "build_sources_changed");
                buffers = new Dictionary<string,byte[]>(StringComparer.Ordinal);
                foreach (var item in Manifest.Files)
                {
                    Need(Path.GetFileName(item.Key) == item.Key, "invalid_artifact_name");
                    byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "payload", item.Key));
                    Need(Digest(bytes) == item.Value, "artifact_changed");
                    if (item.Key.EndsWith(".dll", StringComparison.Ordinal)) buffers.Add(Path.GetFileNameWithoutExtension(item.Key), bytes);
                }
                foreach (string name in Projects) Need(buffers.ContainsKey(name), "required_artifact_missing");
            }
            protected override Assembly Load(AssemblyName name)
            {
                if (buffers.TryGetValue(name.Name, out byte[] bytes))
                {
                    // SHA与LoadFromStream来自同一缓冲区；不以Location/MVID猜测加载身份。
                    using var stream = new MemoryStream(bytes, writable: false);
                    Assembly assembly = LoadFromStream(stream); hashes[assembly] = Digest(bytes); return assembly;
                }
                if (platform.Contains(name.Name)) return null;
                throw new FileNotFoundException("dependency_not_in_snapshot");
            }
            internal Assembly Research => LoadFromAssemblyName(new AssemblyName("Town.Research"));
            internal string ActualHash(Assembly assembly) => hashes.TryGetValue(assembly, out string h) ? h : null;
            internal void VerifyUnchanged()
            {
                Need(ManifestText == File.ReadAllText(Path.Combine(Folder(root), "build.json")) && Same(Manifest.Sources, Sources(root)), "snapshot_or_sources_changed");
                foreach (var item in Manifest.Files)
                    Need(Digest(File.ReadAllBytes(Path.Combine(Folder(root), "payload", item.Key))) == item.Value, "snapshot_artifact_changed");
            }
        }

        internal static async Task<int> Launch(string root, string mode)
        {
            Need(new[] { "offline", "prepare", "live" }.Contains(mode), "invalid_t3_mode");
            using var lease = new FileStream(Path.Combine(Folder(root), "snapshot.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var loaded = new Loaded(root);
            try
            {
                var method = loaded.Research.GetType("T3SnapshotEntry", throwOnError: true).GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                return await (Task<int>)method.Invoke(null, new object[] { root, mode, loaded.ManifestText,
                    (Func<Assembly,string>)loaded.ActualHash, (Action)loaded.VerifyUnchanged });
            }
            finally { loaded.Unload(); }
        }
    }
}
