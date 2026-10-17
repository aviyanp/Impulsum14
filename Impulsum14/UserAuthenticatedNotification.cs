using Blaze.Core;
using Blaze3SDK.Components;
using EATDF;
using EATDF.Members;
using ProtoFire.Frames;

namespace Impulsum14;

public sealed class NotifyUserAuthenticated : Tdf
{

    // BUID = 0x8B5A6400, SUBS = 0xCF58B300.
    private static readonly TdfMemberInfo[] __typeInfos =
    [
        new TdfMemberInfo("BlazeUserId", "mBlazeUserId", 0x8B5A6400, TdfType.Int64, 0, true), // BUID
        new TdfMemberInfo("Subscribed", "mSUBS", 0xCF58B300, TdfType.Bool, 1, true),          // SUBS
    ];

    private readonly ITdfMember[] __members;
    private readonly TdfInt64 _blazeUserId = new(__typeInfos[0]);
    private readonly TdfBool _subscribed = new(__typeInfos[1]);

    public NotifyUserAuthenticated()
    {
        __members = [_blazeUserId, _subscribed];
    }

    public override Tdf CreateNew() => new NotifyUserAuthenticated();
    public override ITdfMember[] GetMembers() => __members;
    public override TdfMemberInfo[] GetMemberInfos() => __typeInfos;
    public override string GetClassName() => "NotifyUserAuthenticated";
    public override string GetFullClassName() => "Blaze::UserSessions::NotifyUserAuthenticated";

    public long BlazeUserId { get => _blazeUserId.Value; set => _blazeUserId.Value = value; }
    public bool Subscribed { get => _subscribed.Value; set => _subscribed.Value = value; }
}

internal static class UserSessionsNotifications
{
    private const ushort UserAuthenticatedCommand = 8;

    public static Task SendUserAuthenticatedAsync(BlazeRpcConnection connection, long blazeId, bool subscribed = true)
    {
        connection.EnqequeSend(packet =>
        {
            IFireFrame frame = packet.Frame;
            frame.Component = UserSessionsBase.Id; // 30722
            frame.Command = UserAuthenticatedCommand;
            frame.MessageType = MessageType.Notification;
            packet.Data = new NotifyUserAuthenticated { BlazeUserId = blazeId, Subscribed = subscribed };
        });
        return Task.CompletedTask;
    }
}
