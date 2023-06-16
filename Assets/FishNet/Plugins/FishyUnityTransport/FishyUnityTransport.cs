using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FishNet.Managing;
using FishNet.Managing.Logging;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using UnityEngine;
using UnityEngine.Serialization;
#if UTP_TRANSPORT_2_0_ABOVE
using Unity.Networking.Transport.TLS;
#endif

#if !UTP_TRANSPORT_2_0_ABOVE
using NetworkEndpoint = Unity.Networking.Transport.NetworkEndPoint;
#endif

namespace FishNet.Transporting.FishyUnityTransport
{
    [AddComponentMenu("FishNet/Transport/FishyUnityTransport")]
    public class FishyUnityTransport : Transport
    {
        ~FishyUnityTransport()
        {
            Shutdown();
        }

        #region Constants

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
        internal const int MaxReliableThroughput = NetworkParameterConstants.MTU * 32 * 60 / 1000; // bytes per millisecond

        private static readonly ConnectionAddressData DefaultConnectionAddressData = new ConnectionAddressData()
        {
            Address = "127.0.0.1", Port = 7777, ServerListenAddress = string.Empty
        };

        #endregion

        public ProtocolType Protocol => _protocolType;
        [SerializeField] private ProtocolType _protocolType;

#if UTP_TRANSPORT_2_0_ABOVE
        [Tooltip("Per default the client/server will communicate over UDP. Set to true to communicate with WebSocket.")]
        [SerializeField]
        private bool _useWebSockets = false;

        /// <summary>
        /// "Per default the client/server will communicate over UDP. Set to true to communicate with WebSocket.
        /// </summary>
        public bool UseWebSockets
        {
            get => _useWebSockets;
            set => _useWebSockets = value;
        }


        [Tooltip("Per default the client/server communication will not be encrypted. Select true to enable DTLS for UDP and TLS for Websocket.")]
        [SerializeField]
        private bool _useEncryption;
        /// <summary>
        /// Per default the client/server communication will not be encrypted. Select true to enable DTLS for UDP and TLS for Websocket.
        /// </summary>
        public bool UseEncryption
        {
            get => _useEncryption;
            set => _useEncryption = value;
        }
#endif

        [Tooltip("The maximum amount of packets that can be in the internal send/receive queues. Basically this is how many packets can be sent/received in a single update/frame.")]
        [SerializeField]
        private int _maxPacketQueueSize = InitialMaxPacketQueueSize;

        /// <summary>The maximum amount of packets that can be in the internal send/receive queues.</summary>
        /// <remarks>Basically this is how many packets can be sent/received in a single update/frame.</remarks>
        public int MaxPacketQueueSize
        {
            get => _maxPacketQueueSize;
            set => _maxPacketQueueSize = value;
        }

        [Tooltip("The maximum size of an unreliable payload that can be handled by the transport.")]
        [SerializeField]
        private int _maxPayloadSize = InitialMaxPayloadSize;

        /// <summary>The maximum size of an unreliable payload that can be handled by the transport.</summary>
        public int MaxPayloadSize
        {
            get => _maxPayloadSize;
            set => _maxPayloadSize = value;
        }

        private int _maxSendQueueSize;

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
            get => _maxSendQueueSize;
            set => _maxSendQueueSize = value;
        }

        [Tooltip("Timeout in milliseconds after which a heartbeat is sent if there is no activity.")]
        [SerializeField]
        private int _heartbeatTimeoutMS = NetworkParameterConstants.HeartbeatTimeoutMS;

        /// <summary>Timeout in milliseconds after which a heartbeat is sent if there is no activity.</summary>
        public int HeartbeatTimeoutMS
        {
            get => _heartbeatTimeoutMS;
            set => _heartbeatTimeoutMS = value;
        }

        [Tooltip("Timeout in milliseconds indicating how long we will wait until we send a new connection attempt.")]
        [SerializeField]
        private int _connectTimeoutMS = NetworkParameterConstants.ConnectTimeoutMS;

        /// <summary>
        /// Timeout in milliseconds indicating how long we will wait until we send a new connection attempt.
        /// </summary>
        public int ConnectTimeoutMS
        {
            get => _connectTimeoutMS;
            set => _connectTimeoutMS = value;
        }

        [Tooltip("The maximum amount of connection attempts we will try before disconnecting.")]
        [SerializeField]
        private int _maxConnectAttempts = NetworkParameterConstants.MaxConnectAttempts;

        /// <summary>The maximum amount of connection attempts we will try before disconnecting.</summary>
        public int MaxConnectAttempts
        {
            get => _maxConnectAttempts;
            set => _maxConnectAttempts = value;
        }

        [Tooltip("Inactivity timeout after which a connection will be disconnected. The connection needs to receive data from the connected endpoint within this timeout. Note that with heartbeats enabled, simply not sending any data will not be enough to trigger this timeout (since heartbeats count as connection events).")]
        [SerializeField]
        private int _disconnectTimeoutMS = NetworkParameterConstants.DisconnectTimeoutMS;

        /// <summary>Inactivity timeout after which a connection will be disconnected.</summary>
        /// <remarks>
        /// The connection needs to receive data from the connected endpoint within this timeout.
        /// Note that with heartbeats enabled, simply not sending any data will not be enough to
        /// trigger this timeout (since heartbeats count as connection events).
        /// </remarks>
        public int DisconnectTimeoutMS
        {
            get => _disconnectTimeoutMS;
            set => _disconnectTimeoutMS = value;
        }

        public ConnectionAddressData ConnectionData = DefaultConnectionAddressData;

        /// <summary>
        /// Can be used to simulate poor network conditions such as:
        /// - packet delay/latency
        /// - packet jitter (variances in latency, see: https://en.wikipedia.org/wiki/Jitter)
        /// - packet drop rate (packet loss)
        /// </summary>
#if UTP_TRANSPORT_2_0_ABOVE 
        [Obsolete("DebugSimulator is no longer supported and has no effect. Use Network Simulator from the Multiplayer Tools package.", false)]
#endif
        public SimulatorParameters DebugSimulator = new SimulatorParameters()
        {
            PacketDelayMS = 0,
            PacketJitterMS = 0,
            PacketDropRate = 0
        };

        /// <summary>
        /// Maximum number of connections allowed.
        /// </summary>
        [Range(1, 4095)]
        [SerializeField] private int _maximumClients = 4095;

        internal uint? DebugSimulatorRandomSeed { get; set; }
        internal RelayServerData RelayServerDataInternal;

        public RelayServerData RelayServerData => RelayServerDataInternal;

        private readonly ServerSocket _serverSocket = new ServerSocket();
        private readonly ClientSocket _clientSocket = new ClientSocket();

        #region Initialization and unity.

        /// <summary>
        /// Initializes the transport. Use this instead of Awake.
        /// </summary>
        public override void Initialize(NetworkManager networkManager, int transportIndex)
        {
            base.Initialize(networkManager, transportIndex);
            
            _serverSocket.Initialize(this);
            _clientSocket.Initialize(this);
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        #endregion

        #region ConnectionStates.

        public override event Action<ClientConnectionStateArgs> OnClientConnectionState;
        public override event Action<ServerConnectionStateArgs> OnServerConnectionState;
        public override event Action<RemoteConnectionStateArgs> OnRemoteConnectionState;

        public override string GetConnectionAddress(int connectionId)
        {
            return _serverSocket.GetConnectionAddress(connectionId);
        }

        public override LocalConnectionState GetConnectionState(bool server)
        {
            return server ? _serverSocket.State : _clientSocket.State;
        }

        public override RemoteConnectionState GetConnectionState(int connectionId)
        {
            return _serverSocket.GetConnectionState(connectionId) == NetworkConnection.State.Connected
                ? RemoteConnectionState.Started
                : RemoteConnectionState.Stopped;
        }

        public override void HandleClientConnectionState(ClientConnectionStateArgs connectionStateArgs)
        {
            OnClientConnectionState?.Invoke(connectionStateArgs);
        }

        public override void HandleServerConnectionState(ServerConnectionStateArgs connectionStateArgs)
        {
            OnServerConnectionState?.Invoke(connectionStateArgs);
        }

        public override void HandleRemoteConnectionState(RemoteConnectionStateArgs connectionStateArgs)
        {
            OnRemoteConnectionState?.Invoke(connectionStateArgs);
        }

        #endregion

        #region Iterating.

        public override void IterateIncoming(bool server)
        {
            if (server)
            {
                _serverSocket.IterateIncoming();
            }
            else
            {
                _clientSocket.IterateIncoming();
            }
        }
        
        public override void IterateOutgoing(bool server)
        {
            if (server)
            {
                _serverSocket.IterateOutgoing();
            }
            else
            {
                _clientSocket.IterateOutgoing();
            }
        }

        #endregion

        #region ReceivedData.

        public override event Action<ClientReceivedDataArgs> OnClientReceivedData;

        internal void HandleClientReceivedData(ArraySegment<byte> message, Channel channel, int transportIndex)
        {
            HandleClientReceivedDataArgs(new ClientReceivedDataArgs(message, channel, transportIndex));
        }

        public override void HandleClientReceivedDataArgs(ClientReceivedDataArgs receivedDataArgs)
        {
            OnClientReceivedData?.Invoke(receivedDataArgs);
        }

        public override event Action<ServerReceivedDataArgs> OnServerReceivedData;

        public override void HandleServerReceivedDataArgs(ServerReceivedDataArgs receivedDataArgs)
        {
            OnServerReceivedData?.Invoke(receivedDataArgs);
        }

        #endregion

        #region Sending.

        public override void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            if (_clientSocket.State != LocalConnectionState.Started) return;
            _clientSocket.SendToServer(channelId, segment);
        }

        public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        {
            if (_serverSocket.State != LocalConnectionState.Started) return;
            _serverSocket.Send(channelId, segment, connectionId);
        }

        #endregion

        #region Configuration.

        public override int GetMaximumClients() => _maximumClients;

        public override void SetMaximumClients(int value)
        {
            if (_serverSocket.State != LocalConnectionState.Stopped)
            {
                if (NetworkManager.CanLog(LoggingType.Warning))
                    Debug.LogWarning($"Cannot set maximum clients when server is running.");
            }
            else
            {
                _maximumClients = value;
            }
        }

        public override void SetClientAddress(string address)
        {
            ConnectionData.Address = address;
        }

        public override string GetClientAddress()
        {
            return ConnectionData.Address;
        }

        public override void SetPort(ushort port)
        {
            ConnectionData.Port = port;
        }

        public override ushort GetPort()
        {
            return ConnectionData.Port;
        }

        public override void SetServerBindAddress(string address, IPAddressType addressType)
        {
            ConnectionData.ServerListenAddress = address;
        }

        public override string GetServerBindAddress(IPAddressType addressType)
        {
            return ConnectionData.ServerListenAddress;
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
            byte[] hostConnectionData = hostConnectionDataBytes ?? connectionDataBytes;
            RelayServerDataInternal = new RelayServerData(ipv4Address, port, allocationIdBytes, connectionDataBytes, hostConnectionData, keyBytes, isSecure);
            _protocolType = ProtocolType.RelayUnityTransport;
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

        /// <summary>Set the relay server data (using the lower-level Unity Transport data structure).</summary>
        /// <param name="serverData">Data for the Relay server to use.</param>
        public void SetRelayServerData(RelayServerData serverData)
        {
            RelayServerDataInternal = serverData;
            _protocolType = ProtocolType.RelayUnityTransport;
        }

        /// <summary>
        /// Sets IP and Port information. This will be ignored if using the Unity Relay and you should call <see cref="SetRelayServerData(string,ushort,byte[],byte[],byte[],byte[],bool)"/>
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

        /// <summary>
        /// Sets IP and Port information. This will be ignored if using the Unity Relay and you should call <see cref="SetRelayServerData(string,ushort,byte[],byte[],byte[],byte[],bool)"/>
        /// </summary>
        /// <param name="ipv4Address">The remote IP address (despite the name, can be an IPv6 address)</param>
        /// <param name="port">The remote port</param>
        /// <param name="listenAddress">The local listen address</param>
        public void SetConnectionData(string ipv4Address, ushort port, string listenAddress = null)
        {
            ConnectionData.Address = ipv4Address;
            ConnectionData.Port = port;
            ConnectionData.ServerListenAddress = listenAddress ?? string.Empty;

            _protocolType = ProtocolType.UnityTransport;
        }

        #endregion

        #region Start and stop.

        public override bool StartConnection(bool server)
        {
            return server ? _serverSocket.StartConnection() : _clientSocket.StartConnection();
        }

        public override bool StopConnection(bool server)
        {
            return server ? _serverSocket.StopServer() : _clientSocket.StopClient();
        }

        public override bool StopConnection(int connectionId, bool immediately)
        {
            return _serverSocket.DisconnectRemoteClient(connectionId);
        }

        public override void Shutdown()
        {
            StopConnection(false);
            StopConnection(true);
        }

        #endregion

        #region Channels.

        public override int GetMTU(byte channelId)
        {
            // Check for client activity
            if (_clientSocket is { State: LocalConnectionState.Started })
            {
                return NetworkParameterConstants.MTU - _clientSocket.GetMaxHeaderSize((Channel)channelId);
            }

            if (_serverSocket is { State: LocalConnectionState.Started })
            {
                return NetworkParameterConstants.MTU - _serverSocket.GetMaxHeaderSize((Channel)channelId);
            }

            return NetworkParameterConstants.MTU;
        }

        #endregion
    }
}