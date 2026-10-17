using EATDF.Visitors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EATDF.Serialization;

public class Heat2Serializer : TdfSerializer
{
    public override string Name => "Heat2";
    public ITdfRegistry Registry { get; }
    public bool Heat1BackCompatibility { get; set; }
    public Heat2Serializer(ITdfRegistry registry, bool heat1BackCompatibility)
    {
        Registry = registry;
        Heat1BackCompatibility = heat1BackCompatibility;
    }

    public Heat2Serializer()
    {
        Registry = new TdfRegistry();
        Heat1BackCompatibility = true;
    }

    public override bool Deserialize(Stream stream, Tdf tdf)
    {
        try
        {
            Heat2Decoder decoder = new Heat2Decoder(stream, Registry, Heat1BackCompatibility);
            decoder.VisitTdf(tdf);
            return decoder.StreamValid;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or ArgumentException or InvalidOperationException or NotSupportedException or System.Reflection.TargetInvocationException) { return false; }
    }

    public override void Serialize(Stream stream, Tdf tdf)
    {
        Heat2Encoder encoder = new Heat2Encoder(stream, Registry, Heat1BackCompatibility);
        encoder.VisitTdf(tdf);
    }
}

