using UnityEngine;
using System;
using Unity.Jobs;
using Unity.Collections;
using Unity.Networking.Transport;
using NetCode.Relay;

struct ClientUpdateJob : IJob
{
    public NativeList<int> connectionIds;
    public FixedString32 address;
    public ushort port;
    public bool isClient;
    public NetworkDriver driver;
    public NetworkPipeline pipeline;
    public NativeArray<NetworkConnection> connection;
    public NativeArray<byte> done;
    public static event Action<NetworkEndPoint> OnRemoteEndpointReported = (NetworkEndPoint endPoint) =>
    {
        Debug.Log("[INFO] Host started. Your IP address is " + endPoint.Address);
    };

    public void Execute()
    {
        if (!connection[0].IsCreated)
        {
            // Remember that its not a bool anymore.
            if (done[0] != 1)
                Debug.Log("Something went wrong during connect");
            return;
        }
        DataStreamReader stream;
        NetworkEvent.Type cmd;

        while ((cmd = connection[0].PopEvent(driver, out stream)) != NetworkEvent.Type.Empty)
        {
            if (cmd == NetworkEvent.Type.Connect)
            {
                if (isClient)
                {
                    //Connect via relay

                    NativeList<byte> ipv6AddressBuffer = new NativeList<byte>(16, Allocator.Temp);
                    NetworkEndPoint ipAddress = NetworkEndPoint.Parse(address.ToString(), port, NetworkFamily.Ipv6);

                    DataStreamWriter writer;
                    writer = driver.BeginSend(pipeline, connection[0]);

                    // Write the message type
                    writer.WriteByte((byte)MessageType.ConnectToServer);
                    // TODO: Throw if address is not 16 bytes. It should always be
                    // Write the address
                    writer.WriteBytes(ipAddress.GetRawAddressBytes());
                    // Write the port
                    writer.WriteUShort(ipAddress.Port);

                    // Send connect to client
                    driver.EndSend(writer);

                    ipv6AddressBuffer.Dispose();
                }
                else
                {
                    //Register us as a server (Start as a host)
                    DataStreamWriter writer;
                    writer = driver.BeginSend(pipeline, connection[0]);
                    writer.WriteByte((byte)MessageType.StartServer);
                    driver.EndSend(writer);
                }
            }
            else if (cmd == NetworkEvent.Type.Data)
            {
                MessageType messageType = (MessageType)stream.ReadByte();

                switch (messageType)
                {
                    case MessageType.AddressReport:
                        {
                            int connectionId = stream.ReadInt();
                            NativeArray<byte> addressBytes = new NativeArray<byte>(16, Allocator.Temp);
                            stream.ReadBytes(addressBytes);
                            ushort remotePort = stream.ReadUShort();

                            NetworkEndPoint remoteEndPoint = new NetworkEndPoint();
                            remoteEndPoint.SetRawAddressBytes(addressBytes, NetworkFamily.Ipv6);
                            remoteEndPoint.Port = remotePort;

                            connectionIds[0] = connectionId;

                            OnRemoteEndpointReported(remoteEndPoint);
                        }
                        break;
                    case MessageType.ConnectToServer: // Connection approved
                        {
                            int connectionId = stream.ReadInt();
                            if (isClient)
                            {
                                connectionIds[0] = connectionId;
                            }
                            else
                            {
                                connectionIds.Add(connectionId);

                                // Test Ping
                                uint value = 1;
                                DataStreamWriter writer;
                                // Send using the pipeline
                                writer = driver.BeginSend(pipeline, connection[0]);
                                writer.WriteByte((byte)MessageType.Data);
                                writer.WriteInt(connectionIds[1]);
                                writer.WriteUInt(value);
                                driver.EndSend(writer);
                            }
                        }
                        break;
                    case MessageType.Data:
                        {
                            int fromConnectionId = stream.ReadInt();

                            if (isClient)
                            {
                                uint value = stream.ReadUInt();
                                Debug.Log("Got the value = " + value + " back from the server");

                                DataStreamWriter writer;
                                // Send using the pipeline
                                writer = driver.BeginSend(pipeline, connection[0]);
                                writer.WriteByte((byte)MessageType.Data);
                                writer.WriteUInt(value + 1);
                                driver.EndSend(writer);
                            }
                            else
                            {
                                uint value = stream.ReadUInt();
                                Debug.Log("Got the value = " + value + " back from the client");

                                if (value >= 10)
                                {
                                    // disconnect
                                    DataStreamWriter writer;
                                    writer = driver.BeginSend(pipeline, connection[0]);
                                    writer.WriteByte((byte)MessageType.ClientDisconnect);
                                    writer.WriteInt(connectionIds[0]);
                                    driver.EndSend(writer);
                                }
                                else
                                {
                                    // bload cast
                                    foreach (int id in connectionIds)
                                    {
                                        DataStreamWriter writer;
                                        // Send using the pipeline
                                        writer = driver.BeginSend(pipeline, connection[0]);
                                        writer.WriteByte((byte)MessageType.Data);
                                        writer.WriteInt(id);
                                        writer.WriteUInt(value + 1);
                                        driver.EndSend(writer);
                                    }
                                }
                            }
                        }
                        break;
                    case MessageType.ClientDisconnect:
                        {
                            int connectionId = stream.ReadInt();
                            Debug.Log($"[INFO] Client disconnect request (connectionId: {connectionId})");

                            // Delete the connection ID
                            for (int i = 0; i < connectionIds.Length; i++)
                            {
                                if (connectionIds[i].Equals(connectionId))
                                {
                                    connectionIds.RemoveAtSwapBack(i);
                                    break;
                                }
                            }
                        }
                        break;
                }
            }
            else if (cmd == NetworkEvent.Type.Disconnect)
            {
                if (driver.ReceiveErrorCode == 10) Debug.LogError("[MLAPI.Relay] The MLAPI Relay detected a CRC mismatch. This could be due to the maxClients or other connectionConfig settings not being the same");

                Debug.Log("Client got disconnected from server");
                done[0] = 1;
                connection[0] = default(NetworkConnection);
            }
        }
    }
}
