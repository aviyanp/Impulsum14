using Blaze.Core.Internal;
using EATDF.Serialization;
using Microsoft.Extensions.Logging;
using ProtoFire;
using ProtoFire.Tls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Blaze.Core;

public class BlazeServerConfig
{
    public Func<BlazeRpcContext, EATDF.Tdf, Func<Task<EATDF.Tdf>>, Task<EATDF.Tdf>>? CommandMiddleware { get; set; }
    public int MaxConnections { get; set; } = 2048;
    public int MaxFrameBytes { get; set; } = 1048576;
    public int IdleTimeoutSeconds { get; set; } = 120;
    public int SendTimeoutSeconds { get; set; } = 15;
    public int InboundQueueCapacity { get; set; } = 32;
    public int OutboundQueueCapacity { get; set; } = 64;
    public IPEndPoint LocalEndpoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 0);
    public FrameEncoding PacketFrameEncoding { get; set; } = FrameEncoding.Fire;
    public ITdfSerializer Serializer { get; set; } = new Heat2Serializer();
    public IBlazeRouter Router { get; set; } = new BlazeRouter();
    public IBlazeServerCallbacks CallbackHandler { get; set; } = new DefaultServerCallbacks();
    public EndpointType EndpointType { get; set; } = EndpointType.Rpc;
    public bool Secure { get; set; } = false;
    public ProtoSSLCertificate? Certificate { get; set; }
    public int Backlog { get; set; } = -1;
    public BlazePacketLogger? PacketLogger { get; set; }
}
