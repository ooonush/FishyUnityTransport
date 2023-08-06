using System;
using System.Collections.Generic;
using UnityEngine;
using Networking = Unity.Networking;
using TransportNetworkEvent = Unity.Networking.Transport.NetworkEvent;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.Utilities;
#if UTP_TRANSPORT_2_0_ABOVE
using Unity.Networking.Transport.TLS;
#endif

#if !UTP_TRANSPORT_2_0_ABOVE
using NetworkEndpoint = Unity.Networking.Transport.NetworkEndPoint;
#endif

namespace FishNet.Transporting.UTP
{
    [DisallowMultipleComponent]
    [AddComponentMenu("FishNet/Transport/FishyUnityTransport")]
    public partial class FishyUnityTransport : Transport, INetworkStreamDriverConstructor
    {
        /// <summary>
        /// Enum type stating the type of protocol
        /// </summary>
        public enum ProtocolType
        {
            /// <summary>
            /// Unity Transport Protocol
            /// </summary>
            UnityTransport,
            /// <summary>
            /// Unity Transport Protocol over Relay
            /// </summary>
            RelayUnityTransport,
        }

        /// <summary>
        /// The default maximum (receive) packet queue size
        /// </summary>
        private const int InitialMaxPacketQueueSize = 128;

        /// <summary>
        /// The default maximum payload size
        /// </summary>
        private const int InitialMaxPayloadSize = 6 * 1024;

        // Maximum reliable throughput, assuming the full reliable window can be sent on every
        // frame at 60 FPS. This will be a large over-estimation in any realistic scenario.
        private const int k_MaxReliableThroughput = (NetworkParameterConstants.MTU * 64 * 60) / 1000; // bytes per millisecond

        private static ConnectionAddressData s_DefaultConnectionAddressData = new ConnectionAddressData { Address = "127.0.0.1", Port = 7777, ServerListenAddress = string.Empty };

        private INetworkStreamDriverConstructor m_DriverConstructor;

        public INetworkStreamDriverConstructor DriverConstructor
        {
            get => m_DriverConstructor ?? this;
            set => m_DriverConstructor = value;
        }

        [Tooltip("Which protocol should be selected (Relay/Non-Relay).")]
        [SerializeField]
        private ProtocolType m_ProtocolType;

#if UTP_TRANSPORT_2_0_ABOVE
        [Tooltip("Per default the client/server will communicate over UDP. Set to true to communicate with WebSocket.")]
        [SerializeField]
        private bool m_UseWebSockets = false;

        public bool UseWebSockets
        {
            get => m_UseWebSockets;
            set => m_UseWebSockets = value;
        }

        /// <summary>
        /// Per default the client/server communication will not be encrypted. Select true to enable DTLS for UDP and TLS for Websocket.
        /// </summary>
        [Tooltip("Per default the client/server communication will not be encrypted. Select true to enable DTLS for UDP and TLS for Websocket.")]
        [SerializeField]
        private bool m_UseEncryption = false;
        public bool UseEncryption
        {
            get => m_UseEncryption;
            set => m_UseEncryption = value;
        }
#endif

        [Tooltip("The maximum amount of packets that can be in the internal send/receive queues. Basically this is how many packets can be sent/received in a single update/frame.")]
        [SerializeField]
        private int m_MaxPacketQueueSize = InitialMaxPacketQueueSize;

        /// <summary>The maximum amount of packets that can be in the internal send/receive queues.</summary>
        /// <remarks>Basically this is how many packets can be sent/received in a single update/frame.</remarks>
        public int MaxPacketQueueSize
        {
            get => m_MaxPacketQueueSize;
            set => m_MaxPacketQueueSize = value;
        }

        [Tooltip("The maximum size of an unreliable payload that can be handled by the transport.")]
        [SerializeField]
        private int m_MaxPayloadSize = InitialMaxPayloadSize;

        /// <summary>The maximum size of an unreliable payload that can be handled by the transport.</summary>
        public int MaxPayloadSize
        {
            get => m_MaxPayloadSize;
            set => m_MaxPayloadSize = value;
        }

        private int m_MaxSendQueueSize = 0;

        /// <summary>The maximum size in bytes of the transport send queue.</summary>
        /// <remarks>
        /// The send queue accumulates messages for batching and stores messages when other internal
        /// send queues are full. Note that there should not be any need to set this value manually
        /// since the send queue size is dynamically sized based on need.
        ///
        /// This value should only be set if you have particular requirements (e.g. if you want to
        /// limit the memory usage of the send queues). Note however that setting this value too low
        /// can easily lead to disconnections under heavy traffic.
        /// </remarks>
        public int MaxSendQueueSize
        {
            get => m_MaxSendQueueSize;
            set => m_MaxSendQueueSize = value;
        }

        [Tooltip("Timeout in milliseconds after which a heartbeat is sent if there is no activity.")]
        [SerializeField]
        private int m_HeartbeatTimeoutMS = NetworkParameterConstants.HeartbeatTimeoutMS;

        /// <summary>Timeout in milliseconds after which a heartbeat is sent if there is no activity.</summary>
        public int HeartbeatTimeoutMS
        {
            get => m_HeartbeatTimeoutMS;
            set => m_HeartbeatTimeoutMS = value;
        }

        [Tooltip("Timeout in milliseconds indicating how long we will wait until we send a new connection attempt.")]
        [SerializeField]
        private int m_ConnectTimeoutMS = NetworkParameterConstants.ConnectTimeoutMS;

        /// <summary>
        /// Timeout in milliseconds indicating how long we will wait until we send a new connection attempt.
        /// </summary>
        public int ConnectTimeoutMS
        {
            get => m_ConnectTimeoutMS;
            set => m_ConnectTimeoutMS = value;
        }

        [Tooltip("The maximum amount of connection attempts we will try before disconnecting.")]
        [SerializeField]
        private int m_MaxConnectAttempts = NetworkParameterConstants.MaxConnectAttempts;

        /// <summary>The maximum amount of connection attempts we will try before disconnecting.</summary>
        public int MaxConnectAttempts
        {
            get => m_MaxConnectAttempts;
            set => m_MaxConnectAttempts = value;
        }

        [Tooltip("Inactivity timeout after which a connection will be disconnected. The connection needs to receive data from the connected endpoint within this timeout. Note that with heartbeats enabled, simply not sending any data will not be enough to trigger this timeout (since heartbeats count as connection events).")]
        [SerializeField]
        private int m_DisconnectTimeoutMS = NetworkParameterConstants.DisconnectTimeoutMS;

        /// <summary>Inactivity timeout after which a connection will be disconnected.</summary>
        /// <remarks>
        /// The connection needs to receive data from the connected endpoint within this timeout.
        /// Note that with heartbeats enabled, simply not sending any data will not be enough to
        /// trigger this timeout (since heartbeats count as connection events).
        /// </remarks>
        public int DisconnectTimeoutMS
        {
            get => m_DisconnectTimeoutMS;
            set => m_DisconnectTimeoutMS = value;
        }

        /// <summary>
        /// Structure to store the address to connect to
        /// </summary>
        [Serializable]
        public struct ConnectionAddressData
        {
            /// <summary>
            /// IP address of the server (address to which clients will connect to).
            /// </summary>
            [Tooltip("IP address of the server (address to which clients will connect to).")]
            [SerializeField]
            public string Address;

            /// <summary>
            /// UDP port of the server.
            /// </summary>
            [Tooltip("UDP port of the server.")]
            [SerializeField]
            public ushort Port;

            /// <summary>
            /// IP address the server will listen on. If not provided, will use localhost.
            /// </summary>
            [Tooltip("IP address the server will listen on. If not provided, will use localhost.")]
            [SerializeField]
            public string ServerListenAddress;

            private static NetworkEndpoint ParseNetworkEndpoint(string ip, ushort port, bool silent = false)
            {
                NetworkEndpoint endpoint = default;

                if (!NetworkEndpoint.TryParse(ip, port, out endpoint, NetworkFamily.Ipv4) &&
                    !NetworkEndpoint.TryParse(ip, port, out endpoint, NetworkFamily.Ipv6))
                {
                    if (!silent)
                    {
                        Debug.LogError($"Invalid network endpoint: {ip}:{port}.");
                    }
                }

                return endpoint;
            }

            /// <summary>
            /// Endpoint (IP address and port) clients will connect to.
            /// </summary>
            public NetworkEndpoint ServerEndPoint => ParseNetworkEndpoint(Address, Port);

            /// <summary>
            /// Endpoint (IP address and port) server will listen/bind on.
            /// </summary>
            public NetworkEndpoint ListenEndPoint
            {
                get
                {
                    if (string.IsNullOrEmpty(ServerListenAddress))
                    {
                        var ep = NetworkEndpoint.LoopbackIpv4;

                        // If an address was entered and it's IPv6, switch to using ::1 as the
                        // default listen address. (Otherwise we always assume IPv4.)
                        if (!string.IsNullOrEmpty(Address) && ServerEndPoint.Family == NetworkFamily.Ipv6)
                        {
                            ep = NetworkEndpoint.LoopbackIpv6;
                        }

                        return ep.WithPort(Port);
                    }
                    else
                    {
                        return ParseNetworkEndpoint(ServerListenAddress, Port);
                    }
                }
            }

            public bool IsIpv6 => !string.IsNullOrEmpty(Address) && ParseNetworkEndpoint(Address, Port, true).Family == NetworkFamily.Ipv6;
        }


        /// <summary>
        /// The connection (address) data for this <see cref="UnityTransport"/> instance.
        /// This is where you can change IP Address, Port, or server's listen address.
        /// <see cref="ConnectionAddressData"/>
        /// </summary>
        public ConnectionAddressData ConnectionData = s_DefaultConnectionAddressData;

        /// <summary>
        /// Parameters for the Network Simulator
        /// </summary>
        [Serializable]
        public struct SimulatorParameters
        {
            /// <summary>
            /// Delay to add to every send and received packet (in milliseconds). Only applies in the editor and in development builds. The value is ignored in production builds.
            /// </summary>
            [Tooltip("Delay to add to every send and received packet (in milliseconds). Only applies in the editor and in development builds. The value is ignored in production builds.")]
            [SerializeField]
            public int PacketDelayMS;

            /// <summary>
            /// Jitter (random variation) to add/substract to the packet delay (in milliseconds). Only applies in the editor and in development builds. The value is ignored in production builds.
            /// </summary>
            [Tooltip("Jitter (random variation) to add/substract to the packet delay (in milliseconds). Only applies in the editor and in development builds. The value is ignored in production builds.")]
            [SerializeField]
            public int PacketJitterMS;

            /// <summary>
            /// Percentage of sent and received packets to drop. Only applies in the editor and in the editor and in developments builds.
            /// </summary>
            [Tooltip("Percentage of sent and received packets to drop. Only applies in the editor and in the editor and in developments builds.")]
            [SerializeField]
            public int PacketDropRate;
        }

        /// <summary>
        /// Can be used to simulate poor network conditions such as:
        /// - packet delay/latency
        /// - packet jitter (variances in latency, see: https://en.wikipedia.org/wiki/Jitter)
        /// - packet drop rate (packet loss)
        /// </summary>
#if UTP_TRANSPORT_2_0_ABOVE
        [HideInInspector]
        [Obsolete("DebugSimulator is no longer supported and has no effect. Use Network Simulator from the Multiplayer Tools package.", false)]
#endif
        public SimulatorParameters DebugSimulator = new SimulatorParameters
        {
            PacketDelayMS = 0,
            PacketJitterMS = 0,
            PacketDropRate = 0
        };

        private NetworkDriver m_Driver;
        private NetworkSettings m_NetworkSettings;
        private ulong m_ServerClientId;

        private NetworkPipeline m_UnreliableFragmentedPipeline;
        private NetworkPipeline m_UnreliableSequencedFragmentedPipeline;
        private NetworkPipeline m_ReliableSequencedPipeline;

        public ProtocolType Protocol => m_ProtocolType;

        private RelayServerData m_RelayServerData;

        /// <summary>
        /// SendQueue dictionary is used to batch events instead of sending them immediately.
        /// </summary>
        private readonly Dictionary<SendTarget, BatchedSendQueue> m_SendQueue = new Dictionary<SendTarget, BatchedSendQueue>();

        // Since reliable messages may be spread out over multiple transport payloads, it's possible
        // to receive only parts of a message in an update. We thus keep the reliable receive queues
        // around to avoid losing partial messages.
        private readonly Dictionary<ulong, BatchedReceiveQueue> m_ReliableReceiveQueues = new Dictionary<ulong, BatchedReceiveQueue>();

        private void InitDriver()
        {
            DriverConstructor.CreateDriver(
                this,
                out m_Driver,
                out m_UnreliableFragmentedPipeline,
                out m_UnreliableSequencedFragmentedPipeline,
                out m_ReliableSequencedPipeline);
        }

        private void DisposeInternals()
        {
            if (m_Driver.IsCreated)
            {
                m_Driver.Dispose();
            }

            m_NetworkSettings.Dispose();

            foreach (BatchedSendQueue queue in m_SendQueue.Values)
            {
                queue.Dispose();
            }

            m_SendQueue.Clear();
            DisposeClientHost();
        }

        private NetworkPipeline SelectSendPipeline(Channel channel)
        {
            switch (channel)
            {
                case Channel.Unreliable:
                    return m_UnreliableFragmentedPipeline;

                case Channel.Reliable:
                    return m_ReliableSequencedPipeline;

                default:
                    Debug.LogError($"Unknown {nameof(Channel)} value: {channel}");
                    return NetworkPipeline.Null;
            }
        }

        private void SetProtocol(ProtocolType inProtocol)
        {
            m_ProtocolType = inProtocol;
        }

        /// <summary>Set the relay server data for the server.</summary>
        /// <param name="ipv4Address">IP address or hostname of the relay server.</param>
        /// <param name="port">UDP port of the relay server.</param>
        /// <param name="allocationIdBytes">Allocation ID as a byte array.</param>
        /// <param name="keyBytes">Allocation key as a byte array.</param>
        /// <param name="connectionDataBytes">Connection data as a byte array.</param>
        /// <param name="hostConnectionDataBytes">The HostConnectionData as a byte array.</param>
        /// <param name="isSecure">Whether the connection is secure (uses DTLS).</param>
        public void SetRelayServerData(string ipv4Address, ushort port, byte[] allocationIdBytes, byte[] keyBytes, byte[] connectionDataBytes, byte[] hostConnectionDataBytes = null, bool isSecure = false)
        {
            var hostConnectionData = hostConnectionDataBytes ?? connectionDataBytes;
            SetRelayServerData(new RelayServerData(ipv4Address, port, allocationIdBytes, connectionDataBytes, hostConnectionData, keyBytes, isSecure));
        }

        /// <summary>Set the relay server data (using the lower-level Unity Transport data structure).</summary>
        /// <param name="serverData">Data for the Relay server to use.</param>
        public void SetRelayServerData(RelayServerData serverData)
        {
            if (m_ServerState == LocalConnectionState.Starting || m_ServerState == LocalConnectionState.Started)
            {
                NetworkManager.LogWarning("It looks like you are trying to connect as a host to the " +
                                          "Relay server. Since the local server is already running, " +
                                          "calling SetRelayServerData() for the client is unnecessary. " +
                                          "It doesn't cause errors, you can ignore it if you're sure " +
                                          "you're doing it right.");
            }

            m_RelayServerData = serverData;
            SetProtocol(ProtocolType.RelayUnityTransport);
        }

        /// <summary>Set the relay server data for the host.</summary>
        /// <param name="ipAddress">IP address or hostname of the relay server.</param>
        /// <param name="port">UDP port of the relay server.</param>
        /// <param name="allocationId">Allocation ID as a byte array.</param>
        /// <param name="key">Allocation key as a byte array.</param>
        /// <param name="connectionData">Connection data as a byte array.</param>
        /// <param name="isSecure">Whether the connection is secure (uses DTLS).</param>
        public void SetHostRelayData(string ipAddress, ushort port, byte[] allocationId, byte[] key, byte[] connectionData, bool isSecure = false)
        {
            SetRelayServerData(ipAddress, port, allocationId, key, connectionData, null, isSecure);
        }

        /// <summary>Set the relay server data for the host.</summary>
        /// <param name="ipAddress">IP address or hostname of the relay server.</param>
        /// <param name="port">UDP port of the relay server.</param>
        /// <param name="allocationId">Allocation ID as a byte array.</param>
        /// <param name="key">Allocation key as a byte array.</param>
        /// <param name="connectionData">Connection data as a byte array.</param>
        /// <param name="hostConnectionData">Host's connection data as a byte array.</param>
        /// <param name="isSecure">Whether the connection is secure (uses DTLS).</param>
        public void SetClientRelayData(string ipAddress, ushort port, byte[] allocationId, byte[] key, byte[] connectionData, byte[] hostConnectionData, bool isSecure = false)
        {
            SetRelayServerData(ipAddress, port, allocationId, key, connectionData, hostConnectionData, isSecure);
        }

        /// <summary>
        /// Sets IP and Port information. This will be ignored if using the Unity Relay and you should call <see cref="SetRelayServerData"/>
        /// </summary>
        /// <param name="ipv4Address">The remote IP address (despite the name, can be an IPv6 address)</param>
        /// <param name="port">The remote port</param>
        /// <param name="listenAddress">The local listen address</param>
        public void SetConnectionData(string ipv4Address, ushort port, string listenAddress = null)
        {
            ConnectionData = new ConnectionAddressData
            {
                Address = ipv4Address,
                Port = port,
                ServerListenAddress = listenAddress ?? ipv4Address
            };

            SetProtocol(ProtocolType.UnityTransport);
        }

        /// <summary>
        /// Sets IP and Port information. This will be ignored if using the Unity Relay and you should call <see cref="SetRelayServerData"/>
        /// </summary>
        /// <param name="endPoint">The remote end point</param>
        /// <param name="listenEndPoint">The local listen endpoint</param>
        public void SetConnectionData(NetworkEndpoint endPoint, NetworkEndpoint listenEndPoint = default)
        {
            string serverAddress = endPoint.Address.Split(':')[0];

            string listenAddress = string.Empty;
            if (listenEndPoint != default)
            {
                listenAddress = listenEndPoint.Address.Split(':')[0];
                if (endPoint.Port != listenEndPoint.Port)
                {
                    Debug.LogError($"Port mismatch between server and listen endpoints ({endPoint.Port} vs {listenEndPoint.Port}).");
                }
            }

            SetConnectionData(serverAddress, endPoint.Port, listenAddress);
        }

        /// <summary>Set the parameters for the debug simulator.</summary>
        /// <param name="packetDelay">Packet delay in milliseconds.</param>
        /// <param name="packetJitter">Packet jitter in milliseconds.</param>
        /// <param name="dropRate">Packet drop percentage.</param>
#if UTP_TRANSPORT_2_0_ABOVE
        [Obsolete("SetDebugSimulatorParameters is no longer supported and has no effect. Use Network Simulator from the Multiplayer Tools package.", false)]
#endif
        public void SetDebugSimulatorParameters(int packetDelay, int packetJitter, int dropRate)
        {
            if (m_Driver.IsCreated)
            {
                Debug.LogError("SetDebugSimulatorParameters() must be called before StartClient() or StartServer().");
                return;
            }

            DebugSimulator = new SimulatorParameters
            {
                PacketDelayMS = packetDelay,
                PacketJitterMS = packetJitter,
                PacketDropRate = dropRate
            };
        }

        [BurstCompile]
        private struct SendBatchedMessagesJob : IJob
        {
            public NetworkDriver.Concurrent Driver;
            public SendTarget Target;
            public BatchedSendQueue Queue;
            public NetworkPipeline ReliablePipeline;

            public void Execute()
            {
                var clientId = Target.ClientId;
                var connection = ParseClientId(clientId);
                var pipeline = Target.NetworkPipeline;

                while (!Queue.IsEmpty)
                {
                    var result = Driver.BeginSend(pipeline, connection, out var writer);
                    if (result != (int)Networking.Transport.Error.StatusCode.Success)
                    {
                        Debug.LogError($"Error sending message: {ErrorUtilities.ErrorToFixedString(result, clientId)}");
                        return;
                    }

                    // We don't attempt to send entire payloads over the reliable pipeline. Instead we
                    // fragment it manually. This is safe and easy to do since the reliable pipeline
                    // basically implements a stream, so as long as we separate the different messages
                    // in the stream (the send queue does that automatically) we are sure they'll be
                    // reassembled properly at the other end. This allows us to lift the limit of ~44KB
                    // on reliable payloads (because of the reliable window size).
                    var written = pipeline == ReliablePipeline ? Queue.FillWriterWithBytes(ref writer) : Queue.FillWriterWithMessages(ref writer);

                    result = Driver.EndSend(writer);
                    if (result == written)
                    {
                        // Batched message was sent successfully. Remove it from the queue.
                        Queue.Consume(written);
                    }
                    else
                    {
                        // Some error occured. If it's just the UTP queue being full, then don't log
                        // anything since that's okay (the unsent message(s) are still in the queue
                        // and we'll retry sending them later). Otherwise log the error and remove the
                        // message from the queue (we don't want to resend it again since we'll likely
                        // just get the same error again).
                        if (result != (int)Networking.Transport.Error.StatusCode.NetworkSendQueueFull)
                        {
                            Debug.LogError($"Error sending the message: {ErrorUtilities.ErrorToFixedString(result, clientId)}");
                            Queue.Consume(written);
                        }

                        return;
                    }
                }
            }
        }

        // Send as many batched messages from the queue as possible.
        private void SendBatchedMessages(SendTarget sendTarget, BatchedSendQueue queue)
        {
            if (!m_Driver.IsCreated)
            {
                return;
            }
            new SendBatchedMessagesJob
            {
                Driver = m_Driver.ToConcurrent(),
                Target = sendTarget,
                Queue = queue,
                ReliablePipeline = m_ReliableSequencedPipeline
            }.Run();
        }

        private bool AcceptConnection()
        {
            var connection = m_Driver.Accept();

            if (connection == default)
            {
                return false;
            }

            if (NetworkManager.ServerManager.Clients.Count >= GetMaximumClients())
            {
                DisconnectRemoteClient(ParseClientId(connection));
            }
            else
            {
                HandleRemoteConnectionState(RemoteConnectionState.Started, ParseClientId(connection));
            }

            return true;
        }

        private void ReceiveMessages(ulong clientId, NetworkPipeline pipeline, DataStreamReader dataReader)
        {
            BatchedReceiveQueue queue;
            if (pipeline == m_ReliableSequencedPipeline)
            {
                if (m_ReliableReceiveQueues.TryGetValue(clientId, out queue))
                {
                    queue.PushReader(dataReader);
                }
                else
                {
                    queue = new BatchedReceiveQueue(dataReader);
                    m_ReliableReceiveQueues[clientId] = queue;
                }
            }
            else
            {
                queue = new BatchedReceiveQueue(dataReader);
            }

            while (!queue.IsEmpty)
            {
                var message = queue.PopMessage();
                if (message == default)
                {
                    // Only happens if there's only a partial message in the queue (rare).
                    break;
                }

                Channel channel = SelectSendChannel(pipeline);
                if (m_ServerState == LocalConnectionState.Started)
                {
                    int connectionId = TransportIdToClientId(clientId);
                    HandleServerReceivedDataArgs(new ServerReceivedDataArgs(message, channel, connectionId, Index));
                }
                else
                {
                    HandleClientReceivedDataArgs(new ClientReceivedDataArgs(message, channel, Index));
                }
            }
        }

        private bool ProcessEvent()
        {
            var eventType = m_Driver.PopEvent(out var networkConnection, out var reader, out var pipeline);
            var clientId = ParseClientId(networkConnection);

            switch (eventType)
            {
                case TransportNetworkEvent.Type.Connect:
                {
                    SetClientConnectionState(LocalConnectionState.Started);
                    return true;
                }
                case TransportNetworkEvent.Type.Disconnect:
                {
                    // Handle cases where we're a client receiving a Disconnect event. The
                    // meaning of the event depends on our current state. If we were connected
                    // then it means we got disconnected. If we were disconnected means that our
                    // connection attempt has failed.
                    if (m_ServerState == LocalConnectionState.Started)
                    {
                        HandleRemoteConnectionState(RemoteConnectionState.Stopped, clientId);
                        m_ReliableReceiveQueues.Remove(clientId);
                        ClearSendQueuesForClientId(clientId);
                    }
                    else
                    {
                        SetClientConnectionState(LocalConnectionState.Stopping);
                        m_ServerClientId = default;
                        m_ReliableReceiveQueues.Remove(clientId);
                        ClearSendQueuesForClientId(clientId);
                        SetClientConnectionState(LocalConnectionState.Stopped);
                    }

                    return true;
                }
                case TransportNetworkEvent.Type.Data:
                {
                    ReceiveMessages(clientId, pipeline, reader);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Processes data to be sent by the socket.
        /// </summary>
        public void IterateOutgoing()
        {
            if (m_Driver.IsCreated)
            {
                foreach (var kvp in m_SendQueue)
                {
                    SendBatchedMessages(kvp.Key, kvp.Value);
                }
            }
        }

        private void IterateIncoming()
        {
            if (m_Driver.IsCreated)
            {
                m_Driver.ScheduleUpdate().Complete();

                if (m_ProtocolType == ProtocolType.RelayUnityTransport && m_Driver.GetRelayConnectionStatus() == RelayConnectionStatus.AllocationInvalid)
                {
                    Debug.LogError("Transport failure! Relay allocation needs to be recreated, and NetworkManager restarted. " +
                                   "Use NetworkManager.OnTransportFailure to be notified of such events programmatically.");

                    // TODO
                    // InvokeOnTransportEvent(TransportFailure);
                    return;
                }

                while (AcceptConnection() && m_Driver.IsCreated)
                {
                    ;
                }

                while (ProcessEvent() && m_Driver.IsCreated)
                {
                    ;
                }
            }
        }

        private int ExtractRtt(NetworkConnection networkConnection)
        {
            if (m_Driver.GetConnectionState(networkConnection) != NetworkConnection.State.Connected)
            {
                return 0;
            }

            m_Driver.GetPipelineBuffers(m_ReliableSequencedPipeline,
#if UTP_TRANSPORT_2_0_ABOVE
                NetworkPipelineStageId.Get<ReliableSequencedPipelineStage>(),
#else
                NetworkPipelineStageCollection.GetStageId(typeof(ReliableSequencedPipelineStage)),
#endif
                networkConnection,
                out _,
                out _,
                out var sharedBuffer);

            unsafe
            {
                var sharedContext = (ReliableUtility.SharedContext*)sharedBuffer.GetUnsafePtr();

                return sharedContext->RttInfo.LastRtt;
            }
        }

        private static unsafe ulong ParseClientId(NetworkConnection connection)
        {
            return *(ulong*)&connection;
        }

        private static unsafe NetworkConnection ParseClientId(ulong clientId)
        {
            return *(NetworkConnection*)&clientId;
        }

        private void ClearSendQueuesForClientId(ulong clientId)
        {
            // NativeList and manual foreach avoids any allocations.
            using var keys = new NativeList<SendTarget>(16, Allocator.Temp);
            foreach (var key in m_SendQueue.Keys)
            {
                if (key.ClientId == clientId)
                {
                    keys.Add(key);
                }
            }

            foreach (var target in keys)
            {
                m_SendQueue[target].Dispose();
                m_SendQueue.Remove(target);
            }
        }

        private void FlushSendQueuesForClientId(ulong clientId)
        {
            foreach (var kvp in m_SendQueue)
            {
                if (kvp.Key.ClientId == clientId)
                {
                    SendBatchedMessages(kvp.Key, kvp.Value);
                }
            }
        }

        /// <summary>
        /// Gets the current RTT for a specific client
        /// </summary>
        /// <param name="clientId">The client RTT to get</param>
        /// <returns>The RTT</returns>
        public ulong GetCurrentRtt(int clientId)
        {
            ulong transportId = ClientIdToTransportId(clientId);

            return (ulong)ExtractRtt(ParseClientId(transportId));
        }

        private void InitializeNetworkSettings()
        {
            m_NetworkSettings = new NetworkSettings(Allocator.Persistent);

            // If the user sends a message of exactly m_MaxPayloadSize in length, we need to
            // account for the overhead of its length when we store it in the send queue.
            var fragmentationCapacity = m_MaxPayloadSize + BatchedSendQueue.PerMessageOverhead;
            m_NetworkSettings.WithFragmentationStageParameters(payloadCapacity: fragmentationCapacity);

            m_NetworkSettings.WithReliableStageParameters(windowSize: 64);

#if !UTP_TRANSPORT_2_0_ABOVE && !UNITY_WEBGL
            m_NetworkSettings.WithBaselibNetworkInterfaceParameters(
                receiveQueueCapacity: m_MaxPacketQueueSize,
                sendQueueCapacity: m_MaxPacketQueueSize);
#endif
        }

        /// <summary>
        /// Send a payload to the specified clientId, data and networkDelivery.
        /// </summary>
        /// <param name="clientId">The clientId to send to</param>
        /// <param name="payload">The data to send</param>
        /// <param name="channel"></param>
        private void Send(ulong clientId, ArraySegment<byte> payload, Channel channel)
        {
            var pipeline = SelectSendPipeline(channel);

            if (pipeline != m_ReliableSequencedPipeline && payload.Count > m_MaxPayloadSize)
            {
                Debug.LogError($"Unreliable payload of size {payload.Count} larger than configured 'Max Payload Size' ({m_MaxPayloadSize}).");
                return;
            }

            var sendTarget = new SendTarget(clientId, pipeline);
            if (!m_SendQueue.TryGetValue(sendTarget, out var queue))
            {
                // The maximum size of a send queue is determined according to the disconnection
                // timeout. The idea being that if the send queue contains enough reliable data that
                // sending it all out would take longer than the disconnection timeout, then there's
                // no point storing even more in the queue (it would be like having a ping higher
                // than the disconnection timeout, which is far into the realm of unplayability).
                //
                // The throughput used to determine what consists the maximum send queue size is
                // the maximum theoritical throughput of the reliable pipeline assuming we only send
                // on each update at 60 FPS, which turns out to be around 2.688 MB/s.
                //
                // Note that we only care about reliable throughput for send queues because that's
                // the only case where a full send queue causes a connection loss. Full unreliable
                // send queues are dealt with by flushing it out to the network or simply dropping
                // new messages if that fails.
                var maxCapacity = m_MaxSendQueueSize > 0 ? m_MaxSendQueueSize : m_DisconnectTimeoutMS * k_MaxReliableThroughput;

                queue = new BatchedSendQueue(Math.Max(maxCapacity, m_MaxPayloadSize));
                m_SendQueue.Add(sendTarget, queue);
            }

            if (!queue.PushMessage(payload))
            {
                if (pipeline == m_ReliableSequencedPipeline)
                {
                    // If the message is sent reliably, then we're over capacity and we can't
                    // provide any reliability guarantees anymore. Disconnect the client since at
                    // this point they're bound to become desynchronized.

                    Debug.LogError($"Couldn't add payload of size {payload.Count} to reliable send queue. " +
                        $"Closing connection {TransportIdToClientId(clientId)} as reliability guarantees can't be maintained.");

                    if (clientId == m_ServerClientId)
                    {
                        DisconnectLocalClient();
                    }
                    else
                    {
                        DisconnectRemoteClient(clientId);

                        HandleRemoteConnectionState(RemoteConnectionState.Stopped, clientId);
                    }
                }
                else
                {
                    // If the message is sent unreliably, we can always just flush everything out
                    // to make space in the send queue. This is an expensive operation, but a user
                    // would need to send A LOT of unreliable traffic in one update to get here.

                    m_Driver.ScheduleFlushSend(default).Complete();
                    SendBatchedMessages(sendTarget, queue);

                    // Don't check for failure. If it still doesn't work, there's nothing we can do
                    // at this point and the message is lost (it was sent unreliable anyway).
                    queue.PushMessage(payload);
                }
            }
        }

        /// <summary>
        /// Shuts down the transport
        /// </summary>
        private void Disconnect()
        {
            if (m_Driver.IsCreated)
            {
                // Flush all send queues to the network.
                foreach (var kvp in m_SendQueue)
                {
                    SendBatchedMessages(kvp.Key, kvp.Value);
                }

                // The above flush only puts the message in UTP internal buffers, need an update to
                // actually get the messages on the wire. (Normally a flush send would be sufficient,
                // but there might be disconnect messages and those require an update call.)
                m_Driver.ScheduleUpdate().Complete();
            }

            DisposeInternals();

            m_ReliableReceiveQueues.Clear();

            // We must reset this to zero because UTP actually re-uses clientIds if there is a clean disconnect
            m_ServerClientId = 0;
        }

        // -------------- Utility Types -------------------------------------------------------------------------------


        /// <summary>
        /// Cached information about reliability mode with a certain client
        /// </summary>
        private struct SendTarget : IEquatable<SendTarget>
        {
            public readonly ulong ClientId;
            public readonly NetworkPipeline NetworkPipeline;

            public SendTarget(ulong clientId, NetworkPipeline networkPipeline)
            {
                ClientId = clientId;
                NetworkPipeline = networkPipeline;
            }

            public bool Equals(SendTarget other)
            {
                return ClientId == other.ClientId && NetworkPipeline.Equals(other.NetworkPipeline);
            }

            public override bool Equals(object obj)
            {
                return obj is SendTarget other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (ClientId.GetHashCode() * 397) ^ NetworkPipeline.GetHashCode();
                }
            }
        }
    }
}