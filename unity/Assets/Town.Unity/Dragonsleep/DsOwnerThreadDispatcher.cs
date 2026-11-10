using System;
using System.Threading.Tasks;
using PieGame.AI.Unity;
using Town.Core.Dragonsleep;

namespace Town.Unity.Dragonsleep
{
    /// <summary>只保存纯托管同步块接口；后台不持有或访问 Unity 组件。</summary>
    public sealed class DsOwnerThreadDispatcher : IDsOwnerThreadDispatcher
    {
        readonly IOwnerThreadDispatcher _dispatcher;

        public DsOwnerThreadDispatcher(IOwnerThreadDispatcher dispatcher)
        { _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)); }

        public Task<T> InvokeAsync<T>(Func<T> synchronousBlock) => _dispatcher.InvokeAsync(synchronousBlock);
    }
}
