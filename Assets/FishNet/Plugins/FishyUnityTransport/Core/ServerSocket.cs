using System;
using System.Collections.Generic;
using FishNet.Managing.Logging;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.TLS;
using UnityEngine;
#if UTP_TRANSPORT_2_0_ABOVE
using Unity.Networking.Transport.TLS;
#endif

#if !UTP_TRANSPORT_2_0_ABOVE
using NetworkEndpoint = Unity.Networking.Transport.NetworkEndPoint;
#endif

namespace FishNet.Transporting.FishyUnityTransport
{
    [Serializable]
    internal class ServerSocket : CommonSocket
    {
        private string _serverPrivateKey;
        private string _serverCertificate;

        private readonly Dictionary<int, NetworkConnection> _transportIdToClientIdMap = new Dictionary<int, NetworkConnection>();
        private readonly Dictionary<NetworkConnection, int> _clientIdToTransportIdMap = new Dictionary<NetworkConnection, int>();

        internal int ConnectionToTransportId(NetworkConnection connection) => _clientIdToTransportIdMap[connection];

        private NetworkConnection TransportIdToConnection(int transportId) => _transportIdToClientIdMap[transportId];

        /// <summary>Set the server parameters for encryption.</summary>
        /// <param name="serverCertificate">Public certificate for the server (PEM format).</param>
        /// <param name="serverPrivateKey">Private key for the server (PEM format).</param>
        public void SetServerSecrets(string serverCertificate, string serverPrivateKey)
        {
            _serverPrivateKey = serverPrivateKey;
            _serverCertificate = serverCertificate;
        }

        #region Start And Stop Server

        /// <summary>
        /// Starts the server.
        /// </summary>
        public bool StartConnection()
        {
            if (Driver.IsCreated || State != LocalConnectionState.Stopped)
            {
                if (Transport.NetworkManager.CanLog(LoggingType.Error))
                    Debug.LogError("Attempting to start a server that is already active.");
                return false;
            }

            InitializeNetworkSettings();

            SetLocalConnectionState(LocalConnectionState.Starting);

            bool succeeded = Transport.Protocol switch
            {
                ProtocolType.UnityTransport => ServerBindAndListen(Transport.ConnectionData.ListenEndPoint),
                ProtocolType.RelayUnityTransport => StartRelayServer(ref Transport.RelayServerDataInternal, Transport.HeartbeatTimeoutMS),
                _ => false
            };

            if (!succeeded && Driver.IsCreated)
            {
                Driver.Dispose();
                SetLocalConnectionState(LocalConnectionState.Stopped);
            }

            SetLocalConnectionState(LocalConnectionState.Started);

            return succeeded;
        }

        private bool StartRelayServer(ref RelayServerData relayServerData, int heartbeatTimeoutMS)
        {
            //This comparison is currently slow since RelayServerData does not implement a custom comparison operator that doesn't use
            //reflection, but this does not live in the context of a performance-critical loop, it runs once at initial connection time.
            if (relayServerData.Equals(default(RelayServerData)))
            {
                Debug.LogError("You must call SetRelayServerData() at least once before calling StartRelayServer.");
                return false;
            }

            NetworkSettings.WithRelayParameters(ref relayServerData, heartbeatTimeoutMS);
            return ServerBindAndListen(NetworkEndpoint.AnyIpv4);
        }

        private bool ServerBindAndListen(NetworkEndpoint endPoint)
        {
            InitDriver(true);

            // Bind the driver to the endpoint
            Driver.Bind(endPoint);
            if (!Driver.Bound)
            {
                if (Transport.NetworkManager.CanLog(LoggingType.Error))
                    Debug.LogError($"Unable to bind to the specified port {endPoint.Port}.");

                return false;
            }

            // and start listening for new connections.
            Driver.Listen();
            if (Driver.Listening) return true;
            if (Transport.NetworkManager.CanLog(LoggingType.Error))
            {
                Debug.LogError("Server failed to listen");
            }
            return false;

        }

        /// <summary>
        /// Stops the server.
        /// </summary>
        public bool StopServer()
        {
            SetLocalConnectionState(LocalConnectionState.Stopping);
            
            Shutdown();

            SetLocalConnectionState(LocalConnectionState.Stopped);
            return true;
        }

        protected override void Shutdown()
        {
            foreach (NetworkConnection connection in _clientIdToTransportIdMap.Keys)
            {
                connection.Disconnect(Driver);
            }

            base.Shutdown();

            _transportIdToClientIdMap.Clear();
            _clientIdToTransportIdMap.Clear();
        }

        #endregion

        protected override void OnIterateIncoming()
        {
            while (AcceptConnection() && Driver.IsCreated) { }
        }

        private bool AcceptConnection()
        {
            NetworkConnection connection = Driver.Accept();

            if (connection == default)
            {
                return false;
            }

            HandleRemoteConnectionState(RemoteConnectionState.Started, connection);

            return true;
        }

        /// <summary>
        /// Stops a remote client from the server, disconnecting the client.
        /// </summary>
        /// <param name="transportId"></param>
        public bool DisconnectRemoteClient(int transportId)
        {
            NetworkConnection connection = TransportIdToConnection(transportId);
            return DisconnectRemoteClient(connection);
        }

        private bool DisconnectRemoteClient(NetworkConnection connection)
        {
            Debug.Assert(State == LocalConnectionState.Started,
                "DisconnectRemoteClient should be called on a listening server");
            if (State != LocalConnectionState.Started) return false;

            FlushSendQueuesForClientId(connection);
            ReliableReceiveQueues.Remove(connection);
            ClearSendQueuesForClientId(connection);

            if (Driver.GetConnectionState(connection) != NetworkConnection.State.Disconnected)
            {
                Driver.Disconnect(connection);
            }

            HandleRemoteConnectionState(RemoteConnectionState.Stopped, connection);
            return true;
        }

        private void HandleRemoteConnectionState(RemoteConnectionState state, NetworkConnection connection)
        {
            int transportId = ParseTransportId(connection);
            switch (state)
            {
                case RemoteConnectionState.Started:
                    _transportIdToClientIdMap[transportId] = connection;
                    _clientIdToTransportIdMap[connection] = transportId;
                    break;
                case RemoteConnectionState.Stopped:
                    _transportIdToClientIdMap.Remove(transportId);
                    _clientIdToTransportIdMap.Remove(connection);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
            
            Transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(state, transportId, Transport.Index));
        }

        protected override void HandleDisconnectEvent(NetworkConnection connection)
        {
            HandleRemoteConnectionState(RemoteConnectionState.Stopped, connection);
        }

        public string GetConnectionAddress(int transportId)
        {
            NetworkConnection connection = TransportIdToConnection(transportId);
            return connection.GetState(Driver) == NetworkConnection.State.Disconnected
                ? string.Empty
#if UTP_TRANSPORT_2_0_ABOVE
                : Driver.GetRemoteEndpoint(connection).Address;
#else
                : Driver.RemoteEndPoint(connection).Address;
#endif
        }

        protected virtual void HandleIncomingConnection(NetworkConnection incomingConnection)
        {
            if (NetworkManager.ServerManager.Clients.Count >= Transport.GetMaximumClients())
            {
                DisconnectRemoteClient(incomingConnection);
                return;
            }

            HandleRemoteConnectionState(RemoteConnectionState.Started, incomingConnection);
        }

        protected override void SetLocalConnectionState(LocalConnectionState state)
        {
            if (state == State) return;

            State = state;
            if (Transport)
            {
                Transport.HandleServerConnectionState(new ServerConnectionStateArgs(state, Transport.Index));
            }
        }

        protected override void OnPushMessageFailure(int channelId, ArraySegment<byte> payload, NetworkConnection connection)
        {
            DisconnectRemoteClient(connection);
            HandleRemoteConnectionState(RemoteConnectionState.Stopped, connection);
        }

        protected override void HandleReceivedData(ArraySegment<byte> message, Channel channel, NetworkConnection connection)
        {
            Transport.HandleServerReceivedDataArgs(new ServerReceivedDataArgs(message, channel, ConnectionToTransportId(connection), Transport.Index));
        }

        protected override void SetupSecureParameters()
        {
            if (string.IsNullOrEmpty(_serverCertificate) || string.IsNullOrEmpty(_serverPrivateKey))
            {
                throw new Exception("In order to use encrypted communications, when hosting, you must set the server certificate and key.");
            }

            NetworkSettings.WithSecureServerParameters(_serverCertificate, _serverPrivateKey);
        }

        public NetworkConnection.State GetConnectionState(int transportId)
        {
            return TransportIdToConnection(transportId).GetState(Driver);
        }

        public void Send(byte channelId, ArraySegment<byte> segment, int transportId)
        {
            Send(channelId, segment, TransportIdToConnection(transportId));
        }
    }
}