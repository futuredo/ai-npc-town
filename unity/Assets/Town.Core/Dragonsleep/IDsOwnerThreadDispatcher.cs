using System;
using System.Threading.Tasks;

namespace Town.Core.Dragonsleep
{
    /// <summary>将同步块交给所有者线程执行；关闭时拒绝或取消，不得回退到工作线程执行。</summary>
    public interface IDsOwnerThreadDispatcher
    {
        Task<T> InvokeAsync<T>(Func<T> synchronousBlock);
    }
}
