using System;
using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Networking.Transport;

namespace NetCode.Relay
{
    public class ServerBehaviour : MonoBehaviour
    {
        public NetworkDriver m_Driver;
        public NetworkPipeline m_Pipeline;
        public NativeList<NetworkConnection> m_Connections;
        public NativeList<Room> m_Room;
        public NativeMultiHashMap<int, Client> m_ConnectedClients;
        public NativeHashMap<int, int> m_ConnectionIndexToRoomID;
        public NativeHashMap<FixedString32, int> m_ServerAddressToRoomID;
        public NativeList<Room> m_ReleasedRoomIds;
        private JobHandle ServerJobHandle;
        public static RelayConfig Config = new RelayConfig();

        void Start()
        {
            m_Connections = new NativeList<NetworkConnection>(16, Allocator.Persistent);
            m_Room = new NativeList<Room>(16, Allocator.Persistent);
            m_ConnectedClients = new NativeMultiHashMap<int, Client>(16, Allocator.Persistent);
            m_ConnectionIndexToRoomID = new NativeHashMap<int, int>(1, Allocator.Persistent);
            m_ServerAddressToRoomID = new NativeHashMap<FixedString32, int>(1, Allocator.Persistent);
            m_ReleasedRoomIds = new NativeList<Room>(Allocator.Persistent);

            // Driver can be used as normal
            m_Driver = NetworkDriver.Create();
            // Driver now knows about this pipeline and can explicitly be asked to send packets through it (by default it sends directly)
            m_Pipeline = m_Driver.CreatePipeline(typeof(UnreliableSequencedPipelineStage));

            var endpoint = NetworkEndPoint.AnyIpv4;
            endpoint.Port = Config.ListenPort;
            if (m_Driver.Bind(endpoint) != 0)
                Console.WriteLine($"[ERROR]Failed to bind to port {Config.ListenPort}");
            else
                m_Driver.Listen();
        }

        void OnDestroy()
        {
            // Make sure we run our jobs to completion before exiting.
            ServerJobHandle.Complete();
            m_Connections.Dispose();
            m_Room.Dispose();
            m_ConnectedClients.Dispose();
            m_ConnectionIndexToRoomID.Dispose();
            m_ServerAddressToRoomID.Dispose();
            m_ReleasedRoomIds.Dispose();
            m_Driver.Dispose();
        }

        void Update()
        {
            ServerJobHandle.Complete();

            var connectionJob = new ServerUpdateConnectionsJob
            {
                driver = m_Driver,
                connections = m_Connections,
            };

            var serverUpdateJob = new ServerUpdateJob
            {
                driver = m_Driver,
                pipeline = m_Pipeline,
                connections = m_Connections,
                rooms = m_Room,
                connectedClients = m_ConnectedClients,
                connectionIndexToRoomID = m_ConnectionIndexToRoomID,
                serverAddressToRoomID = m_ServerAddressToRoomID,
                ReleasedRooms = m_ReleasedRoomIds,
            };

            ServerJobHandle = m_Driver.ScheduleUpdate();
            ServerJobHandle = connectionJob.Schedule(ServerJobHandle);
            ServerJobHandle = serverUpdateJob.Schedule(ServerJobHandle);
        }
    }
}
