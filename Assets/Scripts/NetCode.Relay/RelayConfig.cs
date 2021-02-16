﻿namespace NetCode.Relay
{
    public class RelayConfig
    {
        public TransportType Transport = TransportType.Ruffles;
        public object TransportConfig;
        public ushort BufferSize = 1024 * 8;
        public bool EnableRuntimeMetaLogging = true;
        public int BandwidthGracePrediodLength = 60;
        public int GracePeriodBandwidthLimit = 4000;
        public int BandwidthLimit = 2000;
        public bool AllowTemporaryAlloc = true;
        public int MaxTemporaryAlloc = 1024 * 64;
        public ushort ListenPort = 9000;
        public uint TicksPerSecond = 64;
    }

    public enum TransportType
    {
        Ruffles,
        UNET
    }
}
