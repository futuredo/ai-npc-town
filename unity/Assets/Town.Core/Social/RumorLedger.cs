using System.Collections.Generic;
using System.Linq;
using Town.Core.World;

namespace Town.Core.Social
{
    /// <summary>一条话是怎么传过去的。</summary>
    public enum TellMode
    {
        Answer,            // 居民回答玩家的提问
        Faithful,          // 玩家如实转述（注明原话出自谁）
        ClaimSeen,         // 玩家说成自己亲眼所见（玩家成为新源头）
        Exaggerate,        // 玩家添油加醋
        Gossip,            // 居民之间闲聊
        GossipExaggerated, // 居民闲聊时添油加醋
        Overheard,         // 玩家偷听到居民闲聊
        Rebuttal,          // 被指控 / 听到错话时的反驳
        Evidence,          // 出示或亲眼看到证据
        Firsthand,         // 亲身经历（例如亲手接过锤子）
    }

    /// <summary>流言账本里的一条记录：谁在什么时候对谁说了什么、对方信没信。用于溯源图和结局。</summary>
    public sealed class Transmission
    {
        public int Sequence;
        public GameTime At;
        public string Teller;
        public string Receiver;
        public string PropositionId;
        public bool Stance;
        public TellMode Mode;
        /// <summary>这条说法追溯到的原始源头。</summary>
        public string OriginId;
        public bool Adopted;
        /// <summary>听者的反应（给界面显示）。</summary>
        public string Reaction;
        /// <summary>玩家说了自己并不确知的事（说成亲眼所见但没看到、添油加醋）。</summary>
        public bool IsLie;
        /// <summary>听者早就知道（亲身知道或同一源头听过），这次传话没有带来新东西。</summary>
        public bool AlreadyKnew;
    }

    public sealed class RumorLedger
    {
        private readonly List<Transmission> _items = new List<Transmission>();
        public IReadOnlyList<Transmission> All => _items;

        public Transmission Add(Transmission t)
        {
            t.Sequence = _items.Count + 1;
            _items.Add(t);
            return t;
        }

        /// <summary>某人听到过的、关于某命题某立场的所有不同源头（不论是否采信）。</summary>
        public IEnumerable<string> OriginsHeard(string receiver, string propositionId, bool stance) =>
            _items.Where(t => t.Receiver == receiver && t.PropositionId == propositionId && t.Stance == stance)
                  .Select(t => t.OriginId).Distinct();

        public IEnumerable<Transmission> About(string propositionId) => _items.Where(t => t.PropositionId == propositionId);

        public IEnumerable<Transmission> PlayerLies => _items.Where(t => t.Teller == Ids.Player && t.IsLie);
    }
}
