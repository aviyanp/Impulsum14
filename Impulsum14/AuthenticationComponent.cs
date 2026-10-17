using Blaze.Core;
using Blaze3SDK.Blaze;
using Blaze3SDK.Blaze.Authentication;
using Blaze3SDK.Blaze.CensusData;
using Blaze3SDK.Components;
using EATDF.Types;

namespace Impulsum14;

internal sealed class AuthenticationComponent : AuthenticationComponentBase.Server
{
    private static long UserId => PlayerContext.Current.Id;
    private static string PersonaName => PlayerContext.Current.Name;
    private const string Email = "player@fifa14.local";
    private static string SessionKey => PlayerContext.Session.Key;
    private const uint Locale = 1701729619;

    public override Task<LoginResponse> LoginAsync(LoginRequest request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write($"[Auth] Login: {request.Email}");
        return Task.FromResult(new LoginResponse
        {
            UserId = UserId,
            SessionKey = SessionKey,
            NeedsLegalDoc = false,
            IsOfLegalContactAge = true,
            PCLoginToken = SessionKey,
            PersonaDetailsList = new List<PersonaDetails> { MakePersona() },
        });
    }

    public override Task<FullLoginResponse> SilentLoginAsync(SilentLoginRequest request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[Auth] SilentLogin");
        return Task.FromResult(MakeFullLoginResponse(SessionKey));
    }

    public override Task<FullLoginResponse> OriginLoginAsync(OriginLoginRequest request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[Auth] OriginLogin");
        var connection = (BlazeRpcConnection)context.Connection;
        var response = MakeFullLoginResponse(SessionKey);

        _ = PushUserSessionAsync(connection);

        return Task.FromResult(response);
    }

    public override Task<FullLoginResponse> ExpressLoginAsync(ExpressLoginRequest request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write($"[Auth] ExpressLogin: {request.Email}");
        var connection = (BlazeRpcConnection)context.Connection;

        _ = PushUserSessionAsync(connection);

        return Task.FromResult(MakeFullLoginResponse(SessionKey));
    }

    public override async Task<SessionInfo> LoginPersonaAsync(LoginPersonaRequest request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write($"[Auth] LoginPersona: {request.PersonaName}");
        var connection = (BlazeRpcConnection)context.Connection;
        var session = MakeSession();

        await UserSessionsBase.Server.NotifyUserSessionExtendedDataUpdateAsync(connection,
            new UserSessionExtendedDataUpdate
            {
                ExtendedData = new UserSessionExtendedData
                {
                    BestPingSiteAlias = "gva",
                    Country = "GB",
                    HardwareFlags = HardwareFlags.None,
                    LatencyList = new List<int>(),
                    BlazeObjectIdList = new List<ObjectId>(),
                    ClientAttributes = new Dictionary<uint, int>(),
                    DataMap = new Dictionary<uint, long>(),
                },
                UserId = UserId,
            });

        await CensusDataComponentBase.Server.NotifyNotifyServerCensusDataAsync(connection,
            new NotifyServerCensusData { CensusDataList = new List<NotifyServerCensusDataItem>() });

        return session;
    }

    // Pushes the post-login user-session notifications the client expects, in the same order
    // Zamboni3 fires them from ServerManager.AddServerPlayer (all sent immediately):
    //   1. UserSessions::UserAuthenticated   (hand-rolled; absent from this SDK)
    //   2. UserSessions::UserAdded
    //   3. UserSessions::UserSessionExtendedDataUpdate
    //   4. CensusData::ServerCensusData      (advisor flagged this one too)
    // Without these the client times out after sign-in.
    private static async Task PushUserSessionAsync(BlazeRpcConnection connection)
    {
        ServerDiagnostics.Write("[Notify] PushUserSession START");
        await Send("UserAuthenticated", () =>
            UserSessionsNotifications.SendUserAuthenticatedAsync(connection, UserId, subscribed: true));

        await Send("UserAdded", () => UserSessionsBase.Server.NotifyUserAddedAsync(connection,
            new NotifyUserAdded
            {
                ExtendedData = new UserSessionExtendedData(),
                UserInfo = new UserIdentification
                {
                    AccountId = UserId,
                    AccountLocale = Locale,
                    BlazeId = UserId,
                    ExternalId = 0,
                    Name = PersonaName,
                },
            }, sendNow: false));

        await Send("ExtendedDataUpdate", () => UserSessionsBase.Server.NotifyUserSessionExtendedDataUpdateAsync(connection,
            new UserSessionExtendedDataUpdate
            {
                ExtendedData = new UserSessionExtendedData(),
                UserId = UserId,
            }, sendNow: false));

        await Send("ServerCensusData", () => CensusDataComponentBase.Server.NotifyNotifyServerCensusDataAsync(connection,
            new NotifyServerCensusData { CensusDataList = new List<NotifyServerCensusDataItem>() }));
        ServerDiagnostics.Write("[Notify] PushUserSession DONE");
    }
    private static async Task Send(string name, Func<Task> send)
    {
        try { await send(); ServerDiagnostics.Write($"[Notify] sent {name}"); }
        catch (Exception ex) { ServerDiagnostics.Write($"[Notify] FAILED {name}: {ex.GetType().Name}: {ex.Message}"); }
    }

    public override Task<EmptyMessage> LogoutPersonaAsync(EmptyMessage request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[Auth] LogoutPersona");
        return Task.FromResult(new EmptyMessage());
    }

    public override Task<EmptyMessage> LogoutAsync(EmptyMessage request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[Auth] Logout");
        PlayerRepository.Instance.Revoke(PlayerContext.Session);
        context.Connection.State = null;
        return Task.FromResult(new EmptyMessage());
    }

    public override Task<GetTosInfoResponse> GetTosInfoAsync(GetTosInfoRequest request, BlazeRpcContext context)
        => Task.FromResult(new GetTosInfoResponse
        {
            EaMayContact = 0,
            PartnersMayContact = 0,
            PrivacyPolicyUri = "",
            TosHost = "",
            TosUri = "",
        });

    public override Task<Entitlements> ListUserEntitlements2Async(ListUserEntitlements2Request request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write($"[Auth] ListUserEntitlements2: {request.ProjectId}");
        return Task.FromResult(new Entitlements
        {
            mEntitlements = new List<Entitlement>
            {
                new Entitlement
                {
                    EntitlementTag = "FIFA14PC",
                    EntitlementType = EntitlementType.DEFAULT,
                    GroupName = "FIFA14",
                    IsConsumable = false,
                    ProductId = "FIFA14",
                    ProjectId = "FIFA14",
                    Status = EntitlementStatus.ACTIVE,
                    UseCount = 0,
                    Version = 0,
                }
            }
        });
    }

    public override Task<UpdateAccountResponse> UpdateAccountAsync(UpdateAccountRequest request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write($"[Auth] UpdateAccount Email='{request.Email}' Country='{request.Country}' " +
            $"DOB='{request.DOB}' Lang='{request.Language}' GlobalOptin={request.GlobalOptin} " +
            $"ThirdPartyOptin={request.ThirdPartyOptin}");
        // Persist any non-empty fields (empty values from the game's sync are ignored).
        AccountStore.Update(request.Email, request.Country, request.DOB, request.Language,
            (byte)request.GlobalOptin, (byte)request.ThirdPartyOptin);
        return Task.FromResult(new UpdateAccountResponse { PCLoginToken = SessionKey });
    }

    public override Task<Entitlements> ListEntitlementsAsync(ListEntitlementsRequest request, BlazeRpcContext context)
    {
        var groups = request.GroupNameList is { Count: > 0 }
            ? request.GroupNameList
            : new List<string> { "FIFA14PCBoxContent", "FIFA14PCFUTContentUnlocks" };
        ServerDiagnostics.Write($"[Auth] ListEntitlements groups=[{string.Join(",", groups)}]");

        var list = new List<Entitlement>();
        foreach (var g in groups)
            list.Add(new Entitlement
            {
                EntitlementTag = g,
                EntitlementType = EntitlementType.DEFAULT,
                GroupName = g,
                IsConsumable = false,
                ProductId = "FIFA14PC",
                ProjectId = "FIFA14",
                Status = EntitlementStatus.ACTIVE,
                UseCount = 0,
                Version = 0,
            });
        return Task.FromResult(new Entitlements { mEntitlements = list });
    }

    public override Task<AccountInfo> GetAccountAsync(EmptyMessage request, BlazeRpcContext context)
    {
        var acct = AccountStore.Get();
        ServerDiagnostics.Write($"[Auth] GetAccount -> Email='{acct.Email}' Country='{acct.Country}' DOB='{acct.Dob}' Lang='{acct.Language}'");
        return Task.FromResult(new AccountInfo
        {
            AnonymousUser = false,
            AuthenticationSource = "303107",
            Country = acct.Country,
            DOB = acct.Dob,
            DateCreated = "2013-09-25",
            GlobalOptin = acct.GlobalOptin,
            LastAuth = "2014-01-01",
            Language = acct.Language,
            Email = acct.Email,
            Status = AccountStatus.ACTIVE,
            EmailStatus = EmailStatus.VERIFIED,
            TosVersion = "1",
            ThirdPartyOptin = acct.ThirdPartyOptin,
            UnderageUser = false,
            UserId = UserId,
        });
    }

    public override Task<ListPersonasResponse> ListPersonasAsync(EmptyMessage request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[Auth] ListPersonas");
        return Task.FromResult(new ListPersonasResponse { List = new List<PersonaDetails> { MakePersona() } });
    }

    public override Task<EmptyMessage> HasEntitlementAsync(HasEntitlementRequest request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write($"[Auth] HasEntitlement: {request.EntitlementTag}");
        return Task.FromResult(new EmptyMessage());
    }

    public override Task<EmptyMessage> AcceptTosAsync(AcceptTosRequest request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[Auth] AcceptTos");
        return Task.FromResult(new EmptyMessage());
    }

    public override Task<GetLegalDocsInfoResponse> GetLegalDocsInfoAsync(GetLegalDocsInfoRequest request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[Auth] GetLegalDocsInfo");

        return Task.FromResult(new GetLegalDocsInfoResponse
        {
            EaMayContact = 1,
            PartnersMayContact = 1,
        });
    }

    public override Task<EmptyMessage> CheckSinglePlayerLoginAsync(EmptyMessage request, BlazeRpcContext context)
    {
        ServerDiagnostics.Write("[Auth] CheckSinglePlayerLogin");
        return Task.FromResult(new EmptyMessage());
    }

    private static FullLoginResponse MakeFullLoginResponse(string pcLoginToken) => new()
    {
        SessionInfo = MakeSession(),
        CanAgeUp = false,
        NeedsLegalDoc = false,
        IsOfLegalContactAge = true,
        PCLoginToken = pcLoginToken,
        LegalDocHost = "",
        PrivacyPolicyUri = "",
        TermsOfServiceUri = "",
        TosHost = "",
        TosUri = "",
    };

    private static SessionInfo MakeSession() => new()
    {
        BlazeUserId = UserId,
        IsFirstLogin = false,
        SessionKey = SessionKey,
        LastLoginDateTime = 0,
        Email = AccountStore.Get().Email,
        PersonaDetails = MakePersona(),
        UserId = UserId,
    };

    private static PersonaDetails MakePersona() => new()
    {
        DisplayName = PersonaName,
        LastAuthenticated = 0,
        PersonaId = UserId,
        Status = PersonaStatus.ACTIVE,
    };
}
