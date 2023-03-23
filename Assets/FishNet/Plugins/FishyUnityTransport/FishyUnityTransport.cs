using System;
using System.Collections.Generic;
using FishNet.Managing;
using FishNet.Managing.Logging;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using UnityEngine;
using UnityEngine.Serialization;

namespace FishNet.Transporting.FishyUnityTransport
{
    [AddComponentMenu("FishNet/Transport/FishyUnityTransport")]
    public class FishyUnityTransport : Transport
    {
        [SerializeField] private int _heartbeatTimeoutMS = NetworkParameterConstants.HeartbeatTimeoutMS;

        private int _localClientTransportId;
        private readonly Dictionary<int, ulong> _transportIdToClientIdMap = new();
        private readonly Dictionary<ulong, int> _clientIdToTransportIdMap = new();

        internal int ClientIdToTransportId(ulong clientId)
        {
            return clientId == _clientSocket.ServerClientId
                ? _localClientTransportId
                : _clientIdToTransportIdMap[clientId];
        }

        internal ulong TransportIdToClientId(int transportId)
        {
            return transportId == _localClientTransportId
                ? _clientSocket.ServerClientId
                : _transportIdToClientIdMap[transportId];
        }

        /// <summary>Timeout in milliseconds after which a heartbeat is sent if there is no activity.</summary>
        public int HeartbeatTimeoutMS => _heartbeatTimeoutMS;

        public int MaxPayloadSize = 6 * 1024;
        [SerializeField] private int _disconnectTimeoutMS = NetworkParameterConstants.DisconnectTimeoutMS;
        public int DisconnectTimeoutMS => _disconnectTimeoutMS;
        
        [Tooltip("The maximum amount of connection attempts we will try before disconnecting.")]
        [SerializeField]
        private int _maxConnectAttempts = NetworkParameterConstants.MaxConnectAttempts;

        [FormerlySerializedAs("m_ConnectTimeoutMS")]
        [Tooltip("Timeout in milliseconds indicating how long we will wait until we send a new connection attempt.")]
        [SerializeField]
        private int _connectTimeoutMS = NetworkParameterConstants.ConnectTimeoutMS;

        [Tooltip("The maximum amount of packets that can be in the internal send/receive queues. Basically this is how many packets can be sent/received in a single update/frame.")]
        public int MaxPacketQueueSize = 128;

        /// <summary>
        /// Timeout in milliseconds indicating how long we will wait until we send a new connection attempt.
        /// </summary>
        public int ConnectTimeoutMS
        {
            get => _connectTimeoutMS;
            set => _connectTimeoutMS = value;
        }

        /// <summary>The maximum amount of connection attempts we will try before disconnecting.</summary>
        public int MaxConnectAttempts
        {
            get => _maxConnectAttempts;
            set => _maxConnectAttempts = value;
        }

        public RelayServerData RelayServerData;

        private static readonly ConnectionAddressData DefaultConnectionAddressData = new() { Address = "127.0.0.1", Port = 7777, ServerListenAddress = string.Empty };
        public ConnectionAddressData ConnectionData = DefaultConnectionAddressData;

        [SerializeField] public ProtocolType ProtocolType;
        [SerializeField] private ServerSocket _serverSocket = new();
        [SerializeField] private ClientSocket _clientSocket = new();

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
        public override string GetConnectionAddress(int connectionId)
        {
            return _serverSocket.GetConnectionAddress(TransportIdToClientId(connectionId));
        }
        
        public override event Action<ClientConnectionStateArgs> OnClientConnectionState;

        public override event Action<ServerConnectionStateArgs> OnServerConnectionState;

        public override event Action<RemoteConnectionStateArgs> OnRemoteConnectionState;

        public override LocalConnectionState GetConnectionState(bool server)
        {
            return server ? _serverSocket.State : _clientSocket.State;
        }

        public override RemoteConnectionState GetConnectionState(int connectionId)
        {
            return _serverSocket.GetConnectionState(TransportIdToClientId(connectionId)) == NetworkConnection.State.Connected
                ? RemoteConnectionState.Started
                : RemoteConnectionState.Stopped;
        }

        public override void HandleClientConnectionState(ClientConnectionStateArgs connectionStateArgs)
        {
            switch (connectionStateArgs.ConnectionState)
            {
                case LocalConnectionState.Stopped:
                    _localClientTransportId = _clientSocket.ServerClientId.GetHashCode();
                    break;
                case LocalConnectionState.Starting:
                    break;
                case LocalConnectionState.Started:
                    _localClientTransportId = _clientSocket.ServerClientId.GetHashCode();
                    break;
                case LocalConnectionState.Stopping:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

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
            _serverSocket.Send(channelId, segment, TransportIdToClientId(connectionId));
        }
        #endregion

        #region Configuration.
        
        public override int GetMaximumClients() => _serverSocket.MaximumClients;

        public override void SetMaximumClients(int value)
        {
            if (_serverSocket.State != LocalConnectionState.Stopped)
            {
                if (NetworkManager.CanLog(LoggingType.Warning))
                    Debug.LogWarning($"Cannot set maximum clients when server is running.");
            }
            else
            {
                _serverSocket.MaximumClients = value;
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

        #endregion

        #region Start and stop.

        public override bool StartConnection(bool server)
        {
            return server ? StartServer() : StartClient();
        }

        public override bool StopConnection(bool server)
        {
            return server ? StopServer() : StopClient();
        }

        public override bool StopConnection(int connectionId, bool immediately)
        {
            return StopClient(connectionId);
        }

        public override void Shutdown()
        {
            StopConnection(false);
            StopConnection(true);
            _serverSocket.Shutdown();
            _clientSocket.Shutdown();
            _transportIdToClientIdMap.Clear();
            _clientIdToTransportIdMap.Clear();
        }

        #endregion

        #region Channels.

        public override int GetMTU(byte channelId)
        {
            //Check for client activity
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

        public void SetConnectionData(string ipv4Address, ushort port, string listenAddress = null)
        {
            ConnectionData.Address = ipv4Address;
            ConnectionData.Port = port;
            ConnectionData.ServerListenAddress = listenAddress ?? string.Empty;

            ProtocolType = ProtocolType.UnityTransport;
        }

        /// <summary>Set the relay server data (using the lower-level Unity Transport data structure).</summary>
        /// <param name="serverData">Data for the Relay server to use.</param>
        public void SetRelayServerData(RelayServerData serverData)
        {
            RelayServerData = serverData;
            ProtocolType = ProtocolType.RelayUnityTransport;
        }

        #region Privates.

        private bool StartServer()
        {
            return _serverSocket.StartConnection();
        }

        private bool StopServer()
        {
            return _serverSocket != null && _serverSocket.StopServer();
        }

        private bool StartClient()
        {
            return _clientSocket.StartConnection();
        }

        private bool StopClient()
        {
            return _clientSocket.StopClient();
        }

        private bool StopClient(int connectionId)
        {
            return _serverSocket.DisconnectRemoteClient(TransportIdToClientId(connectionId));
        }

        #endregion

        /// <summary>
        /// Can be used to simulate poor network conditions such as:
        /// - packet delay/latency
        /// - packet jitter (variances in latency, see: https://en.wikipedia.org/wiki/Jitter)
        /// - packet drop rate (packet loss)
        /// </summary>
#if UTP_TRANSPORT_2_0_ABOVE 
        [Obsolete("DebugSimulator is no longer supported and has no effect. Use Network Simulator from the Multiplayer Tools package.", false)]
#endif
        public SimulatorParameters DebugSimulator = new SimulatorParameters
        {
            PacketDelayMS = 0,
            PacketJitterMS = 0,
            PacketDropRate = 0
        };

        internal uint? DebugSimulatorRandomSeed { get; set; } = null;

        public void HandleRemoteConnectionState(RemoteConnectionState state, ulong clientId, int transportIndex)
        {
            int transportId = clientId.GetHashCode();
            switch (state)
            {
                case RemoteConnectionState.Started:
                    _transportIdToClientIdMap[transportId] = clientId;
                    _clientIdToTransportIdMap[clientId] = transportId;
                    break;
                case RemoteConnectionState.Stopped:
                    _transportIdToClientIdMap.Remove(transportId);
                    _clientIdToTransportIdMap.Remove(clientId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }

            HandleRemoteConnectionState(new RemoteConnectionStateArgs(state, transportId, transportIndex));
        }

        public void HandleReceivedData(ArraySegment<byte> message, Channel channel, ulong clientId, int transportIndex, bool server)
        {
            if (server)
            {
                HandleServerReceivedDataArgs(new ServerReceivedDataArgs(message, channel, ClientIdToTransportId(clientId), transportIndex));
            }
            else
            {
                HandleClientReceivedDataArgs(new ClientReceivedDataArgs(message, channel, transportIndex));
            }
        }
    }
}
