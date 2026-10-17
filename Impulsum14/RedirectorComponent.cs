using Blaze.Core;
using Blaze3SDK.Blaze.Redirector;
using Blaze3SDK.Components;
using Microsoft.Extensions.Logging;

namespace Impulsum14;

internal sealed class RedirectorComponent : RedirectorComponentBase.Server
{
    private readonly int _blazePort;
    private readonly ILogger _log;
    private const uint LoopbackIp = (127u << 24) | 1u; // 0x7F000001 = 2130706433

    public RedirectorComponent(int blazePort, ILogger log) { _blazePort = blazePort; _log = log; }

    public override Task<ServerInstanceInfo> GetServerInstanceAsync(ServerInstanceRequest request, BlazeRpcContext context)
    {
        _log.LogInformation("getServerInstance: name='{0}' client='{1}' sku='{2}' ver='{3}' env='{4}' sdk='{5}' dirty='{6}' profile='{7}' plat='{8}'",
            request.Name, request.ClientName, request.ClientSkuId, request.ClientVersion,
            request.Environment, request.BlazeSDKVersion, request.DirtySDKVersion,
            request.ConnectionProfile, request.Platform);

        return Task.FromResult(new ServerInstanceInfo
        {
            Address = new ServerAddress
            {
                IpAddress = new IpAddress
                {
                    Hostname = ServerOptions.Current.AdvertisedAddress,
                    Ip = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(System.Net.IPAddress.Parse(ServerOptions.Current.AdvertisedAddress).GetAddressBytes()),
                    Port = (ushort)_blazePort,
                },
            },

            Secure = false,
            DefaultDnsAddress = 0,
            Messages = new List<string>(),
        });
    }
}
