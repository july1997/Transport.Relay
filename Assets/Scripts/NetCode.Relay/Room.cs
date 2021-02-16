using System;

namespace NetCode.Relay
{
    public struct Room
    {
        public bool IsNotValid { get; set; }
        public int ServerConnectionId { get => !IsNotValid ? Server.ConnectionId : 0; }
        public int RoomId { get; set; }
        public Client Server { get; set; }
        public void ValidityCheck()
        {
            if (IsNotValid) throw new ObjectDisposedException("Attempt to use an invalid room!");
        }
    }
}
