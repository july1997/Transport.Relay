using System;
using UnityEngine.Assertions;
using Unity.Jobs;
using Unity.Collections;
using Unity.Networking.Transport;

namespace NetCode.Relay
{
    struct ServerUpdateJob : IJob
    {
        public NetworkDriver driver;
        [ReadOnly] public NetworkPipeline pipeline;
        public NativeList<NetworkConnection> connections;
        public NativeList<Room> rooms;
        public NativeMultiHashMap<int, Client> connectedClients;
        public NativeHashMap<int, int> connectionIndexToRoomID;
        public NativeHashMap<FixedString32, int> serverAddressToRoomID;
        public NativeList<Room> ReleasedRooms;

        public unsafe void Execute()
        {
            for (int index = 0; index < connections.Length; index++)
            {
                DataStreamReader stream;
                if (!connections[index].IsCreated) continue;

                NetworkEvent.Type cmd;
                while ((cmd = driver.PopEventForConnection(connections[index], out stream)) !=
                NetworkEvent.Type.Empty)
                {
                    if (cmd == NetworkEvent.Type.Data)
                    {
                        // First byte is the messageType
                        MessageType messageType = (MessageType)stream.ReadByte();

                        switch (messageType)
                        {
                            case MessageType.StartServer:
                                {
                                    // Check if they are already connected or perhaps are already hosting, if so return
                                    if (HasPeer(index) || connectionIndexToRoomID.ContainsKey(index))
                                    {
                                        return;
                                    }

                                    Client client = new Client
                                    {
                                        ConnectionId = index,
                                        IsServer = true,
                                        OutgoingBytes = 0,
                                        ConnectTime = DateTime.UtcNow
                                    };
                                    Room room = new Room
                                    {
                                        RoomId = GenerateRoomId(),
                                        Server = client,
                                    };

                                    rooms.Add(room);
                                    connectionIndexToRoomID.Add(index, room.RoomId);

                                    NativeList<byte> ipv6AddressBuffer = new NativeList<byte>(16, Allocator.Temp);
                                    NetworkEndPoint ipAddress = driver.RemoteEndPoint(connections[index]);

                                    if (ipAddress.Family == NetworkFamily.Ipv6)
                                    {
                                        string ipv6Address = ipAddress.Address.Split(':')[0];
                                        foreach (string n in ipv6Address.Split('.'))
                                        {
                                            ipv6AddressBuffer.Add(Byte.Parse(n));
                                        }
                                    }
                                    else if (ipAddress.Family == NetworkFamily.Ipv4)
                                    {
                                        string ipv4Address = ipAddress.Address.Split(':')[0];
                                        foreach (byte b in new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 255 })
                                        {
                                            ipv6AddressBuffer.Add(b);
                                        }
                                        foreach (string n in ipv4Address.Split('.'))
                                        {
                                            ipv6AddressBuffer.Add(Byte.Parse(n));
                                        }
                                    }
                                    else
                                    {
                                        // TODO: Throw wrong type
                                        ipv6AddressBuffer = default(NativeList<byte>);
                                    }

                                    ipAddress.SetRawAddressBytes(ipv6AddressBuffer, NetworkFamily.Ipv6);
                                    serverAddressToRoomID.Add(ipAddress.Address, room.RoomId);

                                    if (ServerBehaviour.Config.EnableRuntimeMetaLogging) Console.WriteLine("[INFO] Server started from " + ipAddress.Address);

                                    DataStreamWriter writer;
                                    writer = driver.BeginSend(pipeline, connections[index]);

                                    // Write the message type
                                    writer.WriteByte((byte)MessageType.AddressReport);
                                    // Write the Connection Id
                                    writer.WriteInt(index);
                                    // TODO: Throw if address is not 16 bytes. It should always be
                                    // Write the address
                                    writer.WriteBytes(ipv6AddressBuffer);
                                    // Write the port
                                    writer.WriteUShort(ipAddress.Port);

                                    // Send connect to client
                                    driver.EndSend(writer);

                                    ipv6AddressBuffer.Dispose();
                                }
                                break;
                            case MessageType.ConnectToServer:
                                {
                                    // Check if they are already connected or perhaps are already hosting, if so return
                                    if (HasPeer(index) || connectionIndexToRoomID.ContainsKey(index))
                                    {
                                        return;
                                    }

                                    NativeArray<byte> addressBytes = new NativeArray<byte>(16, Allocator.Temp);
                                    stream.ReadBytes(addressBytes);
                                    ushort remotePort = stream.ReadUShort();

                                    NetworkEndPoint endpoint = new NetworkEndPoint();
                                    endpoint.SetRawAddressBytes(addressBytes, NetworkFamily.Ipv6);
                                    endpoint.Port = remotePort;

                                    if (ServerBehaviour.Config.EnableRuntimeMetaLogging) Console.WriteLine("[INFO] Connection requested to address " + endpoint.Address);
                                    if (serverAddressToRoomID.ContainsKey(endpoint.Address))
                                    {
                                        if (ServerBehaviour.Config.EnableRuntimeMetaLogging) Console.WriteLine("[INFO] Connection approved");

                                        // Get the room they want to join
                                        Room room = GetRoom(serverAddressToRoomID[endpoint.Address]);

                                        // Create a client for them
                                        Client client = new Client
                                        {
                                            ConnectionId = index,
                                            IsServer = false,
                                            OutgoingBytes = 0,
                                            ConnectTime = DateTime.UtcNow
                                        };

                                        // Handle the connect
                                        HandleClientConnect(room, client);
                                    }
                                }
                                break;
                            case MessageType.Data:
                                {
                                    foreach (Room room in rooms)
                                    {
                                        if (HasPeer(room, index, out bool isServer))
                                        {
                                            // Found a matching client in room
                                            if (isServer)
                                            {
                                                // The server is sending data

                                                int destination = stream.ReadInt();

                                                // Safety check. Make sure who they want to send to ACTUALLY belongs to their room
                                                if (HasPeer(room, destination, out isServer) && !isServer)
                                                {
                                                    Send(room, destination, index, stream);
                                                }
                                            }
                                            else
                                            {
                                                // A client is sending data

                                                Send(room, room.ServerConnectionId, index, stream);
                                            }
                                        }
                                    }
                                }
                                break;
                            case MessageType.ClientDisconnect:
                                {
                                    int clientConnectionId = stream.ReadInt();

                                    if (ServerBehaviour.Config.EnableRuntimeMetaLogging) Console.WriteLine("[INFO] Client disconnect request");

                                    foreach (Room room in rooms)
                                    {
                                        if (room.ServerConnectionId == clientConnectionId && HandleClientDisconnect(room, clientConnectionId, true))
                                        {
                                            // Only disconnect one. A peer can only be in 1 room
                                            break;
                                        }
                                    }
                                }
                                break;
                        }
                    }
                    else if (cmd == NetworkEvent.Type.Disconnect)
                    {
                        Console.WriteLine("[INFO] Client disconnected from server");
                        connections[index] = default(NetworkConnection);
                    }
                }
            }
        }

        public int GenerateRoomId()
        {
            if (ReleasedRooms.Length > 0)
            {
                for (int i = 0; i < ReleasedRooms.Length; i++)
                {
                    int room_id = ReleasedRooms[i].RoomId;
                    if (!RoomContains(room_id))
                    {
                        ReleasedRooms.RemoveAt(i);
                        return room_id;
                    }
                }
            }
            return rooms.Length + 1;
        }


        public unsafe bool HandleClientDisconnect(Room room, int connectionId, bool serverDisconnect = false)
        {
            room.ValidityCheck();

            if (room.Server.ConnectionId == connectionId)
            {
                // The server just disconnected, tell all the clients in the room
                foreach (Client client in connectedClients.GetValuesForKey(room.RoomId))
                {
                    // Disconnects the client
                    connections[client.ConnectionId].Disconnect(driver);
                    connections[client.ConnectionId] = default(NetworkConnection);
                }

                NativeArray<FixedString32> ServerAddressToRoomKeys = serverAddressToRoomID.GetKeyArray(Allocator.Temp);
                foreach (FixedString32 key in ServerAddressToRoomKeys)
                {
                    if (serverAddressToRoomID[key].Equals(room))
                    {
                        // Remove ourself from the reverse lookup table
                        serverAddressToRoomID.Remove(key);
                        break;
                    }
                }
                ServerAddressToRoomKeys.Dispose();

                // Delete the room
                for (int i = 0; i < rooms.Length; i++)
                {
                    if (rooms[i].RoomId == room.RoomId)
                    {
                        rooms.RemoveAtSwapBack(i);
                        break;
                    }
                }
                connectedClients.Remove(room.RoomId);
                // Release roomId since the room should be considered useless
                ReleasedRooms.AddNoResize(room);
                room.IsNotValid = true;

                return true;
            }
            else if (ContainsConnectedID(room.RoomId, connectionId))
            {
                // A client is attempting to disconnect
                Client client = default(Client);
                foreach (Client c in connectedClients.GetValuesForKey(room.RoomId))
                {
                    if (c.ConnectionId == connectionId)
                    {
                        client = c;
                    }
                }

                if (serverDisconnect)
                {
                    // The server requested this disconnect. Just throw them out.
                    connections[connectionId].Disconnect(driver);
                }
                else
                {
                    // The client was the one that disconnected. Notify the server!

                    DataStreamWriter writer;
                    writer = driver.BeginSend(pipeline, connections[connectionId]);

                    // Write the message type suffixed
                    writer.WriteByte((byte)MessageType.ClientDisconnect);

                    // Write the connectionId of the client that disconnected at the beginning of the buffer.
                    writer.WriteInt(connectionId);

                    // Send the message to the server
                    driver.EndSend(writer);
                }

                // Remove the disconnected client from the list
                client.IsDisconnect = true;

                return true;
            }

            return false;
        }

        public bool Send(Room room, int toConnectionId, int fromConnectionId, DataStreamReader data)
        {
            if (ServerBehaviour.Config.BandwidthLimit > 0)
            {
                // Bandwidth control logic
                int taxedId = room.Server.ConnectionId == fromConnectionId ? toConnectionId : fromConnectionId;

                if (ContainsConnectedID(room.RoomId, taxedId))
                {
                    Client clientToBeTaxed = default(Client);
                    foreach (Client c in connectedClients.GetValuesForKey(room.RoomId))
                    {
                        if (c.ConnectionId == taxedId)
                        {
                            clientToBeTaxed = c;
                        }
                    }

                    int bandwidthLimit = clientToBeTaxed.IsInBandwidthGracePeriod ? ServerBehaviour.Config.GracePeriodBandwidthLimit : ServerBehaviour.Config.BandwidthLimit;

                    if (clientToBeTaxed.OutgoingBytes / (DateTime.UtcNow - clientToBeTaxed.ConnectTime).TotalSeconds > bandwidthLimit)
                    {
                        // Client used too much bandwidth. Disconnect them
                        Console.WriteLine("[INFO] Bandwidth exceeded, client disconnected for overdue. The client is " + (clientToBeTaxed.IsInBandwidthGracePeriod ? "" : "not ") + "on grace period");
                        HandleClientDisconnect(room, taxedId, true);

                        return false;
                    }

                    // This includes relay overhead!!
                    // TODO: Strip overhead
                    clientToBeTaxed.OutgoingBytes += (ulong)data.Length;
                }
            }

            // Send the data
            DataStreamWriter writer;
            writer = driver.BeginSend(pipeline, connections[toConnectionId]);
            writer.WriteByte((byte)MessageType.Data);
            writer.WriteInt(fromConnectionId);
            unsafe
            {
                // Copy data from reader to writer
                byte* buffer;
                int len = data.Length - data.GetBytesRead();
                data.ReadBytes((byte*)&buffer, len);
                writer.WriteBytes((byte*)&buffer, len);
            }
            driver.EndSend(writer);

            return true;
        }

        public unsafe void HandleClientConnect(Room room, Client client)
        {
            room.ValidityCheck();

            // Inform server of new connection
            DataStreamWriter client_writer, server_writer;
            client_writer = driver.BeginSend(pipeline, connections[client.ConnectionId]);
            server_writer = driver.BeginSend(pipeline, connections[room.ServerConnectionId]);

            // Write the messageType
            client_writer.WriteByte((byte)MessageType.ConnectToServer);  // Event type to send to both server and client

            // Write the connectionId
            client_writer.WriteInt(client.ConnectionId);

            // Send event to client
            driver.EndSend(client_writer);

            // Write the messageType
            server_writer.WriteByte((byte)MessageType.ConnectToServer);

            // Write the connectionId
            server_writer.WriteInt(client.ConnectionId);

            // Send connect to client
            driver.EndSend(server_writer);

            // Add client to active clients list
            connectedClients.Add(room.RoomId, client);
        }

        public bool HasPeer(Room room, int connectionId, out bool isServer)
        {
            room.ValidityCheck();

            if (isServer = room.Server.ConnectionId == connectionId)
            {
                return true;
            }

            return ContainsConnectedID(room.RoomId, connectionId);
        }

        private bool HasPeer(int connectionId)
        {
            foreach (Room room in rooms)
            {
                if (HasPeer(room, connectionId, out _))
                {
                    return true;
                }
            }

            return false;
        }

        private bool ContainsConnectedID(int roomid, int connectionId)
        {
            foreach (var c in connectedClients.GetValuesForKey(roomid))
            {
                if (c.ConnectionId == connectionId)
                {
                    if (c.IsDisconnect) return false;
                    return true;
                }
            }
            return false;
        }

        private bool RoomContains(int room_id)
        {
            foreach (Room room in rooms)
            {
                if (room.RoomId == room_id) return true;
            }
            return false;
        }

        private Room GetRoom(int room_id)
        {
            foreach (Room room in rooms)
            {
                if (room.RoomId == room_id) return room;
            }
            return default(Room);
        }
    }
    struct ServerUpdateConnectionsJob : IJob
    {
        public NetworkDriver driver;
        public NativeList<NetworkConnection> connections;

        public void Execute()
        {
            // Clean up connections
            for (int i = 0; i < connections.Length; i++)
            {
                if (!connections[i].IsCreated)
                {
                    connections.RemoveAtSwapBack(i);
                    --i;
                }
            }
            // Accept new connections
            NetworkConnection c;
            while ((c = driver.Accept()) != default(NetworkConnection))
            {
                connections.Add(c);
            }
        }
    }
}
