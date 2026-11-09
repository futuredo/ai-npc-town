using System.Collections.Generic;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 龙眠镇居民的脱离世界对象的只读感知快照。只有 Capture 能构造；
    /// 不持有 DsWorld，不包含 True、他人认知或全镇事件日志。
    /// </summary>
    public sealed class NpcPerceptionView
    {
        public string ActorId { get; }
        public string Json { get; }

        private NpcPerceptionView(string actorId, string json) { ActorId = actorId; Json = json; }

        public static NpcPerceptionView Capture(DsWorld world, string actorId) =>
            new NpcPerceptionView(actorId, MiniJson.Write(ContextBuilder.DialoguePerception(world, actorId)));

        public Dictionary<string, object> ToDictionary() => (Dictionary<string, object>)MiniJson.Parse(Json);
    }
}
