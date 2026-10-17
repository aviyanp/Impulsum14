using EATDF.Types;
using Blaze.Core;
using Blaze3SDK.Blaze;
using Blaze3SDK.Blaze.GameReporting;
using Blaze3SDK.Components;

namespace Impulsum14;

internal sealed class GameReportingComponent : GameReportingComponentBase.Server
{
    public override Task<EmptyMessage> SubmitOfflineGameReportAsync(SubmitGameReportRequest request, BlazeRpcContext context) => Finish(request, context);
    public override Task<EmptyMessage> SubmitGameReportAsync(SubmitGameReportRequest request, BlazeRpcContext context) => Finish(request, context);
    private static Task<EmptyMessage> Finish(SubmitGameReportRequest request, BlazeRpcContext context)
    {
        // Terminal notification completes the native post-match wait. Wallet settlement is
        // exclusively /match/end, so duplicate Blaze reports can never award additional coins.
        ulong id = request.GameReport?.GameReportingId ?? 0;
        GameReportingComponentBase.Server.NotifyResultNotificationAsync((BlazeRpcConnection)context.Connection,
            new ResultNotification { GameReportingId = id, GameHistoryId = id, FinalResult = true, BlazeError = 0 });
        return Task.FromResult(new EmptyMessage());
    }
}
