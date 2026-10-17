using Blaze.Core;
using EATDF;
using EATDF.Members;
using EATDF.Types;

namespace Impulsum14;

public sealed class URLResponse : Tdf
{
    private static readonly TdfMemberInfo[] __typeInfos =
    [
        new TdfMemberInfo("URL", "mURL", 0xD72B0000, TdfType.String, 0, true), // URL
    ];

    private readonly ITdfMember[] __members;
    private readonly TdfString _url = new(__typeInfos[0]);

    public URLResponse() { __members = [_url]; }

    public override Tdf CreateNew() => new URLResponse();
    public override ITdfMember[] GetMembers() => __members;
    public override TdfMemberInfo[] GetMemberInfos() => __typeInfos;
    public override string GetClassName() => "URLResponse";
    public override string GetFullClassName() => "Blaze::SponsoredEvents::URLResponse";

    public string URL { get => _url.Value; set => _url.Value = value; }
}

internal sealed class SponsoredEventsComponent : BlazeComponent
{
    
    public override ushort Id => 2076;
    public override string Name => "SponsoredEventsComponent";
    public override string GetErrorName(ushort errorCode) => $"0x{errorCode:X4}";

    public SponsoredEventsComponent()
    {
        RegisterCommand(new RpcCommandFunc<EmptyMessage, URLResponse, EmptyMessage>
        {
            Id = 3, // getEventsURL
            Name = "getEventsURL",
            IsSupported = true,
            Func = (req, ctx) =>
            {
                ServerDiagnostics.Write("[SponsoredEvents] getEventsURL -> fifa/sponsoredevents/events_list.xml");
                return Task.FromResult<Tdf>(new URLResponse
                {
                    URL = "fifa/sponsoredevents/events_list.xml"
                });
            },
        });
    }
}
