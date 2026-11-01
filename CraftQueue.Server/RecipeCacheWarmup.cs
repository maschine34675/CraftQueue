using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using System.Threading;
using System.Threading.Tasks;

namespace CraftQueue.Server
{
    [Injectable(TypePriority = OnUpdateOrder.DialogueCallbacks - 1)]
    public sealed class RecipeCacheWarmup(WebServer webServer) : IOnUpdate
    {
        private int started;

        public Task<bool> OnUpdateAsync(long secondsSinceLastRun, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                return Task.FromResult(false);

            webServer.StartRecipeWarmup();
            return Task.FromResult(true);
        }
    }
}
