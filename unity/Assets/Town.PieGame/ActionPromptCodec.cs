using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PieGame.AI.Kernel;
using Town.Core.Dragonsleep;

namespace Town.PieGame
{
    /// <summary>只编码现有的玩家裁剪提示，不读取世界，也不在提供方阶段重新组装提示。</summary>
    public static class ActionPromptCodec
    {
        public static string Binding => "Adjudicate|player|" + Protocol.Version + "|" + DsExecutor.RulesVersion;

        public static string BindingFor(PromptPacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            return packet.ActionPromptVersion == DsActionPrompt.Legacy ? Binding : Binding + "|" + packet.ActionPromptVersion;
        }

        public static FrozenPayload Freeze(PromptPacket packet) => new FrozenPayload(BindingFor(packet), Encode(packet));

        public static byte[] Encode(PromptPacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (packet.Kind != AiTaskKind.Adjudicate || packet.ActorId != "player" || packet.DialogueSnapshot != null ||
                packet.TaskSnapshot == null || packet.TaskSnapshot.Kind != AiTaskKind.Adjudicate || packet.TaskSnapshot.ActorId != "player")
                throw new ArgumentException("action_player_snapshot_required", nameof(packet));
            if (packet.Sections.Any(section => section == null)) throw new ArgumentException("action_section_required", nameof(packet));
            var sections = packet.Sections.Select(section => (object)new Dictionary<string, object>
            { ["name"] = section.Name, ["text"] = section.Text, ["untrusted"] = section.Untrusted }).ToList();
            var data = new Dictionary<string, object>
            {
                ["kind"] = packet.Kind.ToString(), ["actor"] = packet.ActorId,
                ["schema"] = packet.SchemaName, ["instructions"] = packet.Instructions, ["sections"] = sections,
                ["task_snapshot_json"] = MiniJson.Write(Canonical(MiniJson.Parse(packet.TaskSnapshot.Json))),
                ["original_input"] = packet.OriginalPlayerInput
            };
            if (packet.ActionPromptVersion != DsActionPrompt.Legacy)
                data["action_prompt_version"] = packet.ActionPromptVersion;
            return new UTF8Encoding(false, true).GetBytes(MiniJson.Write(Canonical(data)));
        }

        static object Canonical(object value)
        {
            if (value is Dictionary<string, object> map)
            {
                var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var pair in map) sorted.Add(pair.Key, Canonical(pair.Value));
                return sorted;
            }
            if (value is List<object> list) return list.Select(Canonical).ToList();
            return value;
        }
    }
}
