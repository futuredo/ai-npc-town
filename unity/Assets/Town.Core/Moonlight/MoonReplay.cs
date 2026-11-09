using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace Town.Core.Moonlight
{
    [DataContract]
    public sealed class MoonRecord
    {
        [DataMember] public string Kind;
        [DataMember] public string RequestId;
        [DataMember] public string Text;
        [DataMember] public MoonCandidate Candidate;
        [DataMember] public double X;
        [DataMember] public double Z;
        [DataMember] public double Seconds;
        [DataMember] public string Code;
        [DataMember] public bool Flag;
        public MoonRecord Copy() => new MoonRecord { Kind = Kind, RequestId = RequestId, Text = Text,
            Candidate = Candidate?.Copy(), X = X, Z = Z, Seconds = Seconds, Code = Code, Flag = Flag };
    }

    /// <summary>独立的单切片录制。只回放已录候选和引擎采样，不调用候选源或旧 TownSave。</summary>
    public static class MoonReplay
    {
        [DataContract]
        sealed class Tape
        {
            [DataMember] public string Scenario;
            [DataMember] public MoonRecord[] Commands;
            [DataMember] public string Hash;
        }
        const int MaxBytes = 2 * 1024 * 1024;
        public static byte[] Encode(MoonWorkshop workshop)
        {
            if (workshop.HasPending) throw new InvalidOperationException("请求进行中不能存档。");
            var commands = workshop.Commands;
            var tape = new Tape { Scenario = MoonWorkshop.Scenario, Commands = commands, Hash = Hash(commands) };
            var bytes = Serialize(tape);
            if (bytes.Length > MaxBytes) throw new InvalidDataException("切片存档超过2MiB。");
            return bytes;
        }

        public static MoonWorkshop Restore(byte[] bytes, int generation)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("切片存档大小无效。");
            try
            {
                Tape tape;
                using (var stream = new MemoryStream(bytes)) tape = (Tape)new DataContractJsonSerializer(typeof(Tape)).ReadObject(stream);
                if (tape?.Scenario != MoonWorkshop.Scenario || tape.Commands == null || tape.Commands.Length > 10000 || tape.Hash != Hash(tape.Commands))
                    throw new InvalidDataException("切片版本或损坏校验不匹配。");
                var workshop = new MoonWorkshop(generation);
                MoonRequest pending = null;
                foreach (var command in tape.Commands)
                {
                    if (command == null) throw new InvalidDataException("空指令。");
                    switch (command.Kind)
                    {
                        case "request": pending = workshop.Begin(command.Text); break;
                        case "receive": workshop.Receive(pending, command.Candidate); pending = null; break;
                        case "cancel_request": workshop.CancelRequest(); pending = null; break;
                        case "confirm": workshop.Confirm(); break;
                        case "light": workshop.SetLight(command.X == 1); break;
                        case "pose": workshop.Observe(command.X, command.Z, command.Seconds, command.Flag); break;
                        case "stop": workshop.Stop(command.X == 1); break;
                        default: throw new InvalidDataException("未知切片指令。");
                    }
                }
                // 输入均需通过同一执行核，不能靠伪造完成状态跳过路线和同意。
                if (workshop.HasPending || Hash(workshop.Commands) != tape.Hash) throw new InvalidDataException("回放未能重建合法的执行序列。");
                return workshop;
            }
            catch (Exception e) when (e is SerializationException || e is ArgumentException || e is InvalidOperationException)
            { throw new InvalidDataException("切片存档无法读取。", e); }
        }

        static string Hash(MoonRecord[] commands)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Serialize(commands)));
        }
        static byte[] Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); return stream.ToArray(); }
        }
    }
}
