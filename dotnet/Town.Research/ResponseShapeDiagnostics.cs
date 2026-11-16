using System;
using System.IO;
using Newtonsoft.Json;
using Town.Core.Dragonsleep;
using Town.OpenAI;

/// <summary>只写研究目录的响应形态摘要；不写公开trace、提示词、请求头或原文。</summary>
internal static class ResponseShapeDiagnostics
{
    internal static void Attach(OpenAiDragonsleepProvider provider, string directory)
    {
        if (provider == null) return;
        provider.ResponseShapeObserved += (packet, result, shape) => Append(directory, packet, result, shape);
    }

    internal static void Append(string directory, PromptPacket packet, ProviderResult result, OpenAiResponseShape shape)
    {
        if (shape == null || result == null ||
            (result.Error != "missing_output" && result.Error != "incomplete_response" && result.Error != "refusal")) return;
        Directory.CreateDirectory(directory);
        var row = new
        {
            utc = DateTime.UtcNow,
            kind = packet?.Kind.ToString(),
            actor = packet?.ActorId,
            failure = result.Error,
            response_shape = shape
        };
        File.AppendAllText(Path.Combine(directory, "response-shapes.jsonl"),
            JsonConvert.SerializeObject(row, Formatting.None) + "\n");
    }
}
