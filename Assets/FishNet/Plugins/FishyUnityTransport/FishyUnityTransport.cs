using System;
using FishNet.Managing;
using FishNet.Managing.Logging;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using UnityEngine;

namespace FishNet.Transporting.FishyUnityTransport
{
    [AddComponentMenu("FishNet/Transport/FishyUnityTransport")]
    public class FishyUnityTransport : Transport
    {
        [SerializeField] private int _heartbeatTimeoutMS = NetworkParameterConstants.HeartbeatTimeoutMS;

        /// <summary>Timeout in milliseconds after which a heartbeat is sent if there is no activity.</summary>
        public int HeartbeatTimeoutMS => _heartbeatTimeoutMS;

        public int MaxPayloadSize = 256000;

        public RelayServerData RelayServerData;

        private static readonly ConnectionAddressData DefaultConnectionAddressData = new() { Address = "127.0.0.1", Port = 7777, ServerListenAddress = string.Empty };
        public ConnectionAddressData ConnectionData = DefaultConnectionAddressData;

        [Range(1, 4095)]
        [SerializeField] private int _maximumClients = 4095;

        [SerializeField] public bool UseRelay;

        private readonly ServerSocket _serverSocket = new();

        private readonly ClientSocket _clientSocket = new();

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
            return _serverSocket.GetConnectionAddress(connectionId);
        }
        
        public override event Action<ClientConnectionStateArgs> OnClientConnectionState;

        public override event Action<ServerConnectionStateArgs> OnServerConnectionState;

        public override event Action<RemoteConnectionStateArgs> OnRemoteConnectionState;

        public override LocalConnectionState GetConnectionState(bool server)
        {
            return server ? _serverSocket.GetLocalConnectionState() : _clientSocket.GetLocalConnectionState();
        }

        public override RemoteConnectionState GetConnectionState(int connectionId)
        {
            return _serverSocket.GetConnectionState(connectionId);
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
            _clientSocket.SendToServer(channelId, segment);
        }
        
        public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        {
            _serverSocket.SendToClient(channelId, segment, connectionId);
        }
        #endregion

        #region Configuration.
        
        public override int GetMaximumClients() => _serverSocket.GetMaximumClients();

        public override void SetMaximumClients(int value)
        {
            if (_serverSocket.GetLocalConnectionState() != LocalConnectionState.Stopped)
            {
                if (NetworkManager.CanLog(LoggingType.Warning))
                    Debug.LogWarning($"Cannot set maximum clients when server is running.");
            }
            else
            {
                _serverSocket.SetMaximumClients(value);
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
            _serverSocket.Dispose();
            _clientSocket.Dispose();
        }

        #endregion

        #region Channels.

        public override int GetMTU(byte channelId)
        {
            //Check for client activity
            if (_clientSocket != null && _clientSocket.GetLocalConnectionState() == LocalConnectionState.Started)
            {
                return NetworkParameterConstants.MTU - _clientSocket.GetMaxHeaderSize(channelId);
            }
            
            if (_serverSocket != null && _serverSocket.GetLocalConnectionState() == LocalConnectionState.Started)
            {
                return NetworkParameterConstants.MTU - _serverSocket.GetMaxHeaderSize(channelId);
            }
            
            return NetworkParameterConstants.MTU;
        }

        #endregion

        public void SetConnectionData(string ipv4Address, ushort port, string listenAddress = null)
        {
            ConnectionData.Address = ipv4Address;
            ConnectionData.Port = port;
            ConnectionData.ServerListenAddress = listenAddress ?? string.Empty;

            UseRelay = false;
        }

        /// <summary>Set the relay server data (using the lower-level Unity Transport data structure).</summary>
        /// <param name="serverData">Data for the Relay server to use.</param>
        public void SetRelayServerData(RelayServerData serverData)
        {
            RelayServerData = serverData;
            UseRelay = true;
        }

        #region Privates.

        private bool StartServer()
        {
            return _serverSocket.StartConnection(_maximumClients);
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
            return _serverSocket.StopConnection(connectionId);
        }

        #endregion
    }
}
