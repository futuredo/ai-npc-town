using Town.Core.World;

namespace Town.Core.Social
{
    public enum CommitmentState { Pending, Kept, Broken }

    /// <summary>
    /// 一条可检查的约定：谁对谁、承诺什么、何时到期。
    /// 只有规则能判定兑现或违约，且只判定一次。
    /// </summary>
    public sealed class Commitment
    {
        public string Id { get; }
        public string Debtor { get; }
        public string Creditor { get; }
        public string Description { get; }
        public GameTime CreatedAt { get; }
        public GameTime Deadline { get; }
        public CommitmentState State { get; internal set; } = CommitmentState.Pending;
        public GameTime? ResolvedAt { get; internal set; }

        public Commitment(string id, string debtor, string creditor, string description, GameTime createdAt, GameTime deadline)
        {
            Id = id; Debtor = debtor; Creditor = creditor; Description = description;
            CreatedAt = createdAt; Deadline = deadline;
        }
    }
}
