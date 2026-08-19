using ProtoBuf;

namespace TheUnknowing
{
    // Server -> client only, sent on transition (entering/leaving any storm's chunk bounds), not
    // every tick - see UnknowingStormManager.OnGameTick. Drives the client pushing/popping its own
    // AmbientModifier for real fog (see TheUnknowingModSystem.StartClientSide) - the client has no
    // other way to know storm bounds, since storm state currently lives server-side only.
    [ProtoContract]
    public class InStormPacket
    {
        [ProtoMember(1)]
        public bool InStorm { get; set; }

        // How many real-time seconds the client should take to fade its fog effect fully in/out
        // in response to this transition - see TheUnknowingModSystem.OnFogFadeTick.
        [ProtoMember(2)]
        public float FogFadeSeconds { get; set; }
    }
}
