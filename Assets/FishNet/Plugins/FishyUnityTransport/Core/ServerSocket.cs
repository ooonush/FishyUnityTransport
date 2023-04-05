using System;
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
            if (!Driver.Listening)
            {
                if (Transport.NetworkManager.CanLog(LoggingType.Error))
                {
                    Debug.LogError("Server failed to listen");
                }
                return false;
            }

            return true;
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

        #endregion

        /// <summary>
        /// Stops a remote client from the server, disconnecting the client.
        /// </summary>
        /// <param name="clientId"></param>
        public bool DisconnectRemoteClient(ulong clientId)
        {
            if (State != LocalConnectionState.Started) return false;

            FlushSendQueuesForClientId(clientId);

            ReliableReceiveQueues.Remove(clientId);
            ClearSendQueuesForClientId(clientId);

            NetworkConnection connection = ParseClientId(clientId);
            if (Driver.GetConnectionState(connection) != NetworkConnection.State.Disconnected)
            {
                Driver.Disconnect(connection);
            }

            Transport.HandleRemoteConnectionState(RemoteConnectionState.Stopped, clientId, Transport.Index);

            return true;
        }

        protected override void HandleDisconnectEvent(ulong clientId)
        {
            Transport.HandleRemoteConnectionState(RemoteConnectionState.Stopped, clientId, Transport.Index);
        }

        public string GetConnectionAddress(ulong clientId)
        {
            NetworkConnection connection = ParseClientId(clientId);
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
                DisconnectRemoteClient(ParseClientId(incomingConnection));
                return;
            }

            Transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, incomingConnection.GetHashCode(), Transport.Index));
        }
        
        /// <summary>
        /// Gets the current ConnectionState of a remote client on the server.
        /// </summary>
        /// <param name="clientId">ConnectionId to get ConnectionState for.</param>
        public NetworkConnection.State GetConnectionState(ulong clientId)
        {
            return ParseClientId(clientId).GetState(Driver);
        }

        protected override void SetLocalConnectionState(LocalConnectionState state)
        {
            if (state == State) return;

            State = state;

            Transport.HandleServerConnectionState(new ServerConnectionStateArgs(state, Transport.Index));
        }

        protected override void OnPushMessageFailure(int channelId, ArraySegment<byte> payload, ulong clientId)
        {
            DisconnectRemoteClient(clientId);
        }

        protected override void HandleReceivedData(ArraySegment<byte> message, Channel channel, ulong clientId)
        {
            Transport.HandleServerReceivedData(message, channel, clientId, Transport.Index);
        }

        protected override void SetupSecureParameters()
        {
            if (string.IsNullOrEmpty(_serverCertificate) || string.IsNullOrEmpty(_serverPrivateKey))
            {
                throw new Exception("In order to use encrypted communications, when hosting, you must set the server certificate and key.");
            }

            NetworkSettings.WithSecureServerParameters(_serverCertificate, _serverPrivateKey);
        }
    }
}