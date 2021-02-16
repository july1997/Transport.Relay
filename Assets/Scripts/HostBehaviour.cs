using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Networking.Transport;

public class HostBehaviour : MonoBehaviour
{
    public NetworkDriver m_Driver;
    public NetworkPipeline m_Pipeline;
    const int k_PacketSize = 256;
    public NativeList<int> m_ConnectionIds;
    public NativeArray<NetworkConnection> m_Connection;
    public NativeArray<byte> m_Done;
    public JobHandle ClientJobHandle;

    void Start()
    {
        // Driver can be used as normal
        m_Driver = NetworkDriver.Create();
        // Driver now knows about this pipeline and can explicitly be asked to send packets through it (by default it sends directly)
        m_Pipeline = m_Driver.CreatePipeline(typeof(UnreliableSequencedPipelineStage));

        m_ConnectionIds = new NativeList<int>(1, Allocator.Persistent);
        m_ConnectionIds.Add(0);
        m_Connection = new NativeArray<NetworkConnection>(1, Allocator.Persistent);
        m_Done = new NativeArray<byte>(1, Allocator.Persistent);

        var endpoint = NetworkEndPoint.Parse("127.0.0.1", 9000); // Relay server IP&Port

        m_Connection[0] = m_Driver.Connect(endpoint);
    }

    public void OnDestroy()
    {
        ClientJobHandle.Complete();

        m_ConnectionIds.Dispose();
        m_Connection.Dispose();
        m_Driver.Dispose();
        m_Done.Dispose();
    }

    void Update()
    {
        ClientJobHandle.Complete();

        var job = new ClientUpdateJob
        {
            connectionIds = m_ConnectionIds,
            address = "",
            port = 0,
            isClient = false,
            driver = m_Driver,
            pipeline = m_Pipeline,
            connection = m_Connection,
            done = m_Done
        };

        ClientJobHandle = m_Driver.ScheduleUpdate();
        ClientJobHandle = job.Schedule(ClientJobHandle);
    }
}
