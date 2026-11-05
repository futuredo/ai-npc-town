using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace Town.OpenAI
{
    public interface IRequestBudget
    {
        bool TryReserve(out string reason);
    }

    // 仅由本机验证工具和 Editor 调用；发行版不得携带开发者密钥或读取此文件。
    public static class LocalValidationAccess
    {
        public static string ReadKey(string projectRoot)
        {
            string path = Path.Combine(projectRoot, ".env.local");
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("拒绝读取符号链接密钥文件。");
                foreach (string raw in File.ReadLines(path))
                {
                    string line = raw.Trim();
                    int split = line.IndexOf('=');
                    if (split < 0 || line.Substring(0, split).Trim() != "OPENAI_API_KEY") continue;
                    string value = line.Substring(split + 1).Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            // 本项目文件优先，防止意外使用另一个工程/组织的环境密钥。
            string fromEnvironment = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;
            throw new InvalidOperationException("未配置 OPENAI_API_KEY，请使用安全密钥设置流程。");
        }
    }

    // 每次发起网络请求前持久化预留；失败、超时也占一次，不自动退回或重试。
    public sealed class FileRequestBudget : IRequestBudget
    {
        private readonly string _directory;
        private int _runRequests;
        private readonly object _gate = new object();

        public FileRequestBudget(string projectRoot)
        {
            _directory = Path.Combine(projectRoot, ".local", "openai");
        }

        public bool TryReserve(out string reason)
        {
            lock (_gate)
            {
                reason = "budget_exhausted";
                if (_runRequests >= OpenAiPolicy.MaxRequestsPerRun ||
                    (_runRequests + 1) * OpenAiPolicy.ReservationUsdPerRequest > OpenAiPolicy.MaxRunReservationUsd)
                    return false;
                try
                {
                    Directory.CreateDirectory(_directory);
                    string path = Path.Combine(_directory, "budget-" + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");
                    using (var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        var ledger = File.Exists(path)
                            ? JsonConvert.DeserializeObject<Ledger>(File.ReadAllText(path)) : new Ledger();
                        if (ledger == null || ledger.Requests < 0 || ledger.ReservedUsd < 0)
                        { reason = "invalid_budget_ledger"; return false; }
                        if (ledger.Requests >= OpenAiPolicy.MaxRequestsPerUtcDay ||
                            ledger.ReservedUsd + OpenAiPolicy.ReservationUsdPerRequest > OpenAiPolicy.MaxDayReservationUsd)
                            return false;
                        ledger.Requests++;
                        ledger.ReservedUsd += OpenAiPolicy.ReservationUsdPerRequest;
                        string temp = path + ".pending";
                        File.WriteAllText(temp, JsonConvert.SerializeObject(ledger, Formatting.Indented));
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                        _runRequests++;
                        reason = null;
                        return true;
                    }
                }
                catch (IOException) { reason = "budget_storage_unavailable"; return false; }
                catch (UnauthorizedAccessException) { reason = "budget_storage_unavailable"; return false; }
                catch (JsonException) { reason = "invalid_budget_ledger"; return false; }
            }
        }

        private sealed class Ledger
        {
            public int Requests { get; set; }
            public decimal ReservedUsd { get; set; }
        }
    }
}
