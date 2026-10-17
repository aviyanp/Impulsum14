using Blaze.Core;
using Blaze3SDK.Blaze;
using Blaze3SDK.Blaze.Association;
using Blaze3SDK.Blaze.Stats;
using Blaze3SDK.Components;
using EATDF.Types;

namespace Impulsum14;

// UserSessions (30722): client sends updateNetworkInfo right after login (retried).
internal sealed class UserSessionsComponent : UserSessionsBase.Server
{
    public override Task<EmptyMessage> UpdateNetworkInfoAsync(NetworkInfo request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[UserSessions] updateNetworkInfo");
        return Task.FromResult(new EmptyMessage());
    }
}

// CensusData (10): subscribe/unsubscribe to live population counts.
internal sealed class CensusDataComponent : CensusDataComponentBase.Server
{
    public override Task<EmptyMessage> SubscribeToCensusDataAsync(EmptyMessage request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[CensusData] subscribe");
        return Task.FromResult(new EmptyMessage());
    }

    public override Task<EmptyMessage> UnsubscribeFromCensusDataAsync(EmptyMessage request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[CensusData] unsubscribe");
        return Task.FromResult(new EmptyMessage());
    }
}

// AssociationLists (25): friends / OSDKPreferredPlayerList / OSDKAvoidPlayerList.
internal sealed class AssociationListsComponent : AssociationListsComponentBase.Server
{
    public override Task<Lists> GetListsAsync(GetListsRequest request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[AssocLists] getLists");
        return Task.FromResult(new Lists { ListMembersVector = new List<ListMembers>() });
    }
}

// Messaging (15): fetch broadcast/MOTD messages.
internal sealed class MessagingComponent : MessagingComponentBase.Server
{
    public override Task<EmptyMessage> FetchMessagesAsync(EmptyMessage request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[Messaging] fetchMessages");
        return Task.FromResult(new EmptyMessage());
    }

    public override Task<EmptyMessage> GetMessagesAsync(EmptyMessage request, BlazeRpcContext context)
        => Task.FromResult(new EmptyMessage());
}

// Rooms (21): view/category update subscriptions.
internal sealed class RoomsComponent : RoomsComponentBase.Server
{
    public override Task<EmptyMessage> SelectViewUpdatesAsync(EmptyMessage request, BlazeRpcContext context)
        => Task.FromResult(new EmptyMessage());

    public override Task<EmptyMessage> SelectCategoryUpdatesAsync(EmptyMessage request, BlazeRpcContext context)
        => Task.FromResult(new EmptyMessage());

    public override Task<EmptyMessage> ToggleJoinedRoomNotificationsAsync(EmptyMessage request, BlazeRpcContext context)
        => Task.FromResult(new EmptyMessage());
}

// Stats (7): client asks for the keyscope map early.
internal sealed class StatsComponent : StatsComponentBase.Server
{
    public override Task<KeyScopes> GetKeyScopesMapAsync(EmptyMessage request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[Stats] getKeyScopesMap");
        return Task.FromResult(new KeyScopes { KeyScopesMap = new Dictionary<string, KeyScopeItem>() });
    }

    public override Task<StatGroupList> GetStatGroupListAsync(EmptyMessage request, BlazeRpcContext context)
        => Task.FromResult(new StatGroupList { Groups = new List<StatGroupSummary>() });

    public override Task<PeriodIds> GetPeriodIdsAsync(EmptyMessage request, BlazeRpcContext context)
        => Task.FromResult(new PeriodIds());
}

// Clubs (11): component settings probe.
internal sealed class ClubsComponent : ClubsComponentBase.Server
{
    public override Task<EmptyMessage> GetClubsComponentSettingsAsync(EmptyMessage request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[Clubs] getClubsComponentSettings");
        return Task.FromResult(new EmptyMessage());
    }

    public override Task<EmptyMessage> GetInvitationsAsync(EmptyMessage request, BlazeRpcContext context)
        => Task.FromResult(new EmptyMessage());
}
