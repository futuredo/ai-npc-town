using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Town.Editor
{
    /// <summary>仅本机 macOS 离线试玩构建；不生成场景、不改项目设置、不授予商业素材分发许可。</summary>
    public static class DragonsleepPlayerBuild
    {
        [Serializable]
        sealed class LocalBuildSummary
        {
            public int schemaVersion = 1;
            public string purpose = "local-macOS-offline-playtest";
            public string licenseNotice = "含本机商业素材，仅供本机试玩；此构建不代表获得上传或再分发许可。";
            public string startedUtc;
            public string finishedUtc;
            public string unityVersion;
            public string sourceScene;
            public string target = "StandaloneOSX";
            public string outputPath;
            public string result = "Failed";
            public string buildResult = "NotStarted";
            public int errors;
            public int warnings;
            public ulong sizeBytes;
            public double buildSeconds;
            public string failure;
        }

        /// <summary>命令行入口：-batchmode -buildTarget StandaloneOSX -executeMethod Town.Editor.DragonsleepPlayerBuild.BuildMacOs -quit。</summary>
        public static void BuildMacOs()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string outputDirectory = Path.Combine(root, ".local", "builds");
            EnsureLocalBuildDirectory(root, outputDirectory);
            string name = "Dragonsleep-macOS-local-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string receiptPath = Path.Combine(outputDirectory, name + ".build.json");
            var summary = new LocalBuildSummary
            {
                startedUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                sourceScene = DragonsleepSceneBuilder.ScenePath,
                outputPath = Path.Combine(outputDirectory, name + ".app")
            };

            try
            {
                RequireBuildPrerequisites(root, summary.outputPath);
                // 明确指定唯一场景与平台，不写 EditorBuildSettings、PlayerSettings 或任何持久构建配置。
                // 不调用 Editor 的 ProviderFactory：其一次性在线授权与密钥读取不属于构建流程。
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { DragonsleepSceneBuilder.ScenePath },
                    locationPathName = summary.outputPath,
                    target = BuildTarget.StandaloneOSX,
                    options = BuildOptions.StrictMode
                };
                summary.buildResult = "Started";
                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report == null) throw new BuildFailedException("BuildPipeline 未返回 BuildReport。");
                var build = report.summary;
                summary.buildResult = build.result.ToString();
                summary.errors = build.totalErrors;
                summary.warnings = build.totalWarnings;
                summary.sizeBytes = build.totalSize;
                summary.buildSeconds = build.totalTime.TotalSeconds;
                if (build.result != BuildResult.Succeeded)
                    throw new BuildFailedException("本机 macOS 构建未成功：" + build.result + "；错误数 " + build.totalErrors);
                if (!Directory.Exists(summary.outputPath)) throw new BuildFailedException("BuildReport 成功，但未找到输出的 .app。");
                summary.result = "Succeeded";
            }
            catch (Exception exception)
            {
                summary.failure = exception.GetType().Name + ": " + exception.Message;
                throw; // batchmode 将异常作为非零退出，不把失败误报为试玩包已就绪。
            }
            finally
            {
                summary.finishedUtc = DateTime.UtcNow.ToString("O");
                using (var stream = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(JsonUtility.ToJson(summary, true));
                Debug.Log("[龙眠镇本机构建] " + summary.result + "；摘要：" + receiptPath);
            }
        }

        static void RequireBuildPrerequisites(string root, string outputPath)
        {
            if (Application.platform != RuntimePlatform.OSXEditor)
                throw new BuildFailedException("此入口仅支持本机 macOS Editor。");
            if (!Application.isBatchMode)
                throw new BuildFailedException("请关闭本工程的交互式 Editor 后使用 -batchmode 命令行构建。");
            if (EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer)
                throw new BuildFailedException("不能在 Play 或另一构建进行期间构建。");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX)
                throw new BuildFailedException("请显式使用 -buildTarget StandaloneOSX；本入口不会切换或保存构建目标。");
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX))
                throw new BuildFailedException("当前 Editor 未安装 macOS 构建支持；本入口不安装或升级 Unity。");
            string sceneFile = Path.Combine(root, "unity", DragonsleepSceneBuilder.ScenePath);
            if (!File.Exists(sceneFile) || AssetDatabase.LoadAssetAtPath<SceneAsset>(DragonsleepSceneBuilder.ScenePath) == null)
                throw new BuildFailedException("缺少已生成的龙眠镇主场景。拒绝自动复制商业源场景或重建：" + DragonsleepSceneBuilder.ScenePath);
            if (File.Exists(outputPath) || Directory.Exists(outputPath))
                throw new BuildFailedException("输出已存在，拒绝覆盖：" + outputPath);
        }

        static void EnsureLocalBuildDirectory(string root, string outputDirectory)
        {
            // 不沿 .local/builds 的符号链接把商业内容意外写到共享位置。
            foreach (string directory in new[] { Path.Combine(root, ".local"), outputDirectory })
            {
                if (File.Exists(directory)) throw new BuildFailedException("本地输出目录被文件占用：" + directory);
                if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new BuildFailedException("本地输出目录不能是符号链接：" + directory);
                Directory.CreateDirectory(directory);
            }
        }
    }
}
