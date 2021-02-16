using System;

namespace NetCode.Relay
{
    public struct Client
    {
        public bool IsServer { get; set; }
        public int ConnectionId { get; set; }
        public bool IsInBandwidthGracePeriod => (DateTime.UtcNow - ConnectTime).TotalSeconds >= ServerBehaviour.Config.BandwidthGracePrediodLength;
        public DateTime ConnectTime { get; set; }
        public ulong OutgoingBytes { get; set; }
        public bool IsDisconnect {get; set;}
    }
}
