using System;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 仅供 Session.EnterPlace 的同步场景事务。调用方已持有会话锁，不能跨帧保留 write。
    /// 实现必须在同一 compare/write 临界区中恰好调用一次 Apply；失败不得调用。
    /// 此接口不迁移模型提交、跨夜推进或造物确认，也不保证绕过会话的直接世界写入。
    /// </summary>
    public interface IDsSceneRuleTransaction
    {
        string Commit(string requestId, DsWorldSnapshot before, DsWorldSnapshot after, DsSceneRuleWrite write);
    }

    /// <summary>仅执行器可以创建的单次写入能力，不接受外部委托、Unity 副作用或再次执行规则。</summary>
    public sealed class DsSceneRuleWrite
    {
        readonly DsWorld _prepared;
        readonly DsWorld _live;
        bool _active = true;
        public bool Applied { get; private set; }

        internal DsSceneRuleWrite(DsWorld prepared, DsWorld live) { _prepared = prepared; _live = live; }

        public void Apply()
        {
            if (!_active || Applied) throw new InvalidOperationException("scene_write_not_active");
            // 反序列化与规则执行早已完成；这里只用内置集合覆盖，不触发宿主回调。
            DsWorldSnapshot.CopyInto(_prepared, _live);
            Applied = true;
        }

        internal void Close() => _active = false;
    }
}
