using ProtoBuf;

namespace TheUnknowing
{
    // Server -> client only, sent on transition (entering/leaving any storm's chunk bounds), not
    // every tick. Drives the client's own AmbientModifier fade for in-storm fog.
    [ProtoContract]
    public class InStormPacket
    {
        [ProtoMember(1)]
        public bool InStorm { get; set; }

        [ProtoMember(2)]
        public float FogFadeSeconds { get; set; }
    }
}
