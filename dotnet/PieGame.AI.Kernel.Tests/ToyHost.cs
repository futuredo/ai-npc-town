using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PieGame.AI.Kernel;

namespace PieGame.AI.Kernel.Tests
{
    // Only host-specific rules live here. Transaction, cancellation, confirmation and replay stay in the kernel.
    internal sealed class WarehouseState
    {
        public int Minute { get; set; }
        public int Parts { get; set; } = 10;
        public int Spent { get; set; }
        public bool StationOccupied { get; set; }
        public string KeeperSecret { get; set; } = "supplier-code-PRIVATE-719";
        public string Json() => JsonSerializer.Serialize(this);
        public static WarehouseState Parse(string json) => JsonSerializer.Deserialize<WarehouseState>(json);
    }

    internal sealed class WarehousePort : IStatePort
    {
        private string _state = new WarehouseState().Json();
        public Action BeforeWrite;
        public int Writes;
        public string Read() => _state;
        public void Write(string state)
        {
            BeforeWrite?.Invoke();
            _state = state;
            Writes++;
        }
    }

    internal sealed class RepairTask : ITaskHandler
    {
        public Candidate Prepare(string before, string raw)
        {
            try
            {
                using var json = JsonDocument.Parse(raw);
                var root = json.RootElement;
                if (root.GetRawText() == "null" || root.ValueKind != JsonValueKind.Object) throw new FormatException();
                int count = 0;
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name != "parts" && property.Name != "minutes" && property.Name != "cost") throw new FormatException();
                    count++;
                }
                if (count != 3) throw new FormatException();
                int parts = root.GetProperty("parts").GetInt32();
                int minutes = root.GetProperty("minutes").GetInt32();
                int cost = root.GetProperty("cost").GetInt32();
                if (parts <= 0 || minutes <= 0 || cost < 0) throw new HostRejection();
                var state = WarehouseState.Parse(before);
                if (state.Parts < parts || state.StationOccupied) throw new HostRejection();
                state.Parts -= parts;
                state.Minute += minutes;
                state.Spent += cost;
                return new Candidate(Protocol.Hash(raw), state.Json(), cost > 50);
            }
            catch (JsonException) { throw new FormatException(); }
            catch (KeyNotFoundException) { throw new FormatException(); }
            catch (InvalidOperationException) { throw new FormatException(); }
        }
    }

    internal sealed class ScriptProvider : IProvider
    {
        public const string Repair = "{\"parts\":2,\"minutes\":17,\"cost\":20}";
        public readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Wait;
        public bool IgnoreCancellation;
        public string Raw = Repair;
        public byte[] Sent;
        public async Task<ProviderReply> CompleteAsync(FrozenPayload payload, CancellationToken cancellationToken)
        {
            Sent = payload.Bytes;
            Entered.TrySetResult(true);
            if (Wait)
            {
                if (IgnoreCancellation) await Release.Task;
                else await Release.Task.WaitAsync(cancellationToken);
            }
            if (!IgnoreCancellation) cancellationToken.ThrowIfCancellationRequested();
            return new ProviderReply(Raw);
        }
    }

    internal static class ToyHost
    {
        public const string Rules = "warehouse-1";
        public static Kernel Create(WarehousePort port)
        {
            var kernel = new Kernel(port, Rules);
            kernel.Register("repair", new RepairTask());
            return kernel;
        }
        public static FrozenPayload Freeze(string state)
        {
            var visible = WarehouseState.Parse(state);
            return new FrozenPayload("public-dispatcher", Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { minute = visible.Minute, parts = visible.Parts, stationOccupied = visible.StationOccupied })));
        }
        public static Request Begin(Kernel kernel, string id = "r1") => kernel.Begin(id, "repair", Freeze(kernel.Capture()));
    }
}
