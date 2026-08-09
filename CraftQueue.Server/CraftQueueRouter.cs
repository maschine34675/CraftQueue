using CraftQueue.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;
using System.Threading.Tasks;

namespace CraftQueue.Server
{
    [Injectable(TypePriority = OnLoadOrder.Routers + 1)]
    public class CraftQueueRouter(
        JsonUtil jsonUtil,
        CraftQueueService craftQueueService,
        WebServer webServer)
        : StaticRouter(jsonUtil,
        [
            new RouteAction(
                "/craftqueue/ChannelInfo",
                (url, requestData, sessionId, output, cancellationToken) =>
                    ValueTask.FromResult<object>(jsonUtil.Serialize(webServer.GetChannelInfo(sessionId))!)),
            new RouteAction<ClientPollRequestData>(
                "/craftqueue/Poll",
                (url, requestData, sessionId, output, cancellationToken) =>
                    ValueTask.FromResult(jsonUtil.Serialize(craftQueueService.Poll(sessionId, requestData))!)),
            new RouteAction<QueueAckRequestData>(
                "/craftqueue/Ack",
                (url, requestData, sessionId, output, cancellationToken) =>
                    ValueTask.FromResult(jsonUtil.Serialize(craftQueueService.AcknowledgeClaim(sessionId, requestData))!)),
            new RouteAction<ClientCommandResultRequestData>(
                "/craftqueue/CommandResult",
                (url, requestData, sessionId, output, cancellationToken) =>
                    ValueTask.FromResult(jsonUtil.Serialize(craftQueueService.CompleteClientCommand(sessionId, requestData))!))
        ])
    {
    }
}
