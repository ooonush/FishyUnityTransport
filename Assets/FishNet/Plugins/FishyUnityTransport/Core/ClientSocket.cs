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
    internal class ClientSocket : CommonSocket
    {
        public ulong ServerClientId { get; private set; }
        private string _serverCommonName;
        private string _clientCaCertificate;

        /// <summary>Set the client parameters for encryption.</summary>
        /// <remarks>
        /// If the CA certificate is not provided, validation will be done against the OS/browser
        /// certificate store. This is what you'd want if using certificates from a known provider.
        /// For self-signed certificates, the CA certificate needs to be provided.
        /// </remarks>
        /// <param name="serverCommonName">Common name of the server (typically hostname).</param>
        /// <param name="caCertificate">CA certificate used to validate the server's authenticity.</param>
        public void SetClientSecrets(string serverCommonName, string caCertificate = null)
        {
            _serverCommonName = serverCommonName;
            _clientCaCertificate = caCertificate;
        }

        public bool StartConnection()
        {
            if (Driver.IsCreated || State != LocalConnectionState.Stopped)
            {
                if (Transport.NetworkManager.CanLog(LoggingType.Error))
                    Debug.LogError("Attempting to start a client that is already active.");
                return false;
            }

            InitializeNetworkSettings();

            SetLocalConnectionState(LocalConnectionState.Starting);

            bool succeeded = ClientBindAndConnect();
            if (succeeded) return true;
            SetLocalConnectionState(LocalConnectionState.Stopped);
            if (Driver.IsCreated)
            {
                Driver.Dispose();
            }

            // SetLocalConnectionState(LocalConnectionState.Started, false);

            return false;
        }

        private bool ClientBindAndConnect()
        {
            NetworkEndpoint serverEndpoint;

            if (Transport.Protocol == ProtocolType.RelayUnityTransport)
            {
                //This comparison is currently slow since RelayServerData does not implement a custom comparison operator that doesn't use
                //reflection, but this does not live in the context of a performance-critical loop, it runs once at initial connection time.
                if (Transport.RelayServerData.Equals(default(RelayServerData)))
                {
                    Debug.LogError("You must call SetRelayServerData() at least once before calling StartRelayServer.");
                    return false;
                }

                NetworkSettings.WithRelayParameters(ref Transport.RelayServerDataInternal, Transport.HeartbeatTimeoutMS);
                serverEndpoint = Transport.RelayServerData.Endpoint;
            }
            else
            {
                serverEndpoint = Transport.ConnectionData.ServerEndPoint;
            }

            InitDriver(false);

            NetworkEndpoint bindEndpoint = serverEndpoint.Family == NetworkFamily.Ipv6 ? NetworkEndpoint.AnyIpv6 : NetworkEndpoint.AnyIpv4;
            Driver.Bind(bindEndpoint);
            if (!Driver.Bound)
            {
                Debug.LogError("Client failed to bind");
                return false;
            }

            NetworkConnection serverConnection = Driver.Connect(serverEndpoint);
            ServerClientId = ParseClientId(serverConnection);

            return true;
        }

        public void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            Send(channelId, segment, ServerClientId);
        }

        public bool StopClient()
        {
            if (!DisconnectLocalClient()) return false;
            Shutdown();
            return true;
        }

        protected override void HandleDisconnectEvent(ulong clientId)
        {
            // Handle cases where we're a client receiving a Disconnect event. The
            // meaning of the event depends on our current state. If we were connected
            // then it means we got disconnected. If we were disconnected means that our
            // connection attempt has failed.
            if (State == LocalConnectionState.Started)
            {
                SetLocalConnectionState(LocalConnectionState.Stopped);
                // m_ServerClientId = default;
            }
            else if (State == LocalConnectionState.Stopped)
            {
                Debug.LogError("Failed to connect to server.");
                // m_ServerClientId = default;
            }
        }

        public bool DisconnectLocalClient()
        {
            if (State is LocalConnectionState.Stopped or LocalConnectionState.Stopping) return false;

            SetLocalConnectionState(LocalConnectionState.Stopping);

            FlushSendQueuesForClientId(ServerClientId);

            if (ParseClientId(ServerClientId).Disconnect(Driver) != 0) return false;
            ReliableReceiveQueues.Remove(ServerClientId);
            ClearSendQueuesForClientId(ServerClientId);

            SetLocalConnectionState(LocalConnectionState.Stopped);

            return true;
        }

        protected override void SetLocalConnectionState(LocalConnectionState state)
        {
            State = state;
            Transport.HandleClientConnectionState(new ClientConnectionStateArgs(state, Transport.Index));
        }

        protected override void OnPushMessageFailure(int channelId, ArraySegment<byte> payload, ulong clientId)
        {
            DisconnectLocalClient();
        }

        protected override void HandleReceivedData(ArraySegment<byte> message, Channel channel, ulong clientId)
        {
            Transport.HandleClientReceivedData(message, channel, Transport.Index);
        }

        protected override void SetupSecureParameters()
        {
            if (string.IsNullOrEmpty(_serverCommonName))
            {
                throw new Exception("In order to use encrypted communications, clients must set the server common name.");
            }
            if (string.IsNullOrEmpty(_clientCaCertificate))
            {
                NetworkSettings.WithSecureClientParameters(_serverCommonName);
            }
            else
            {
                NetworkSettings.WithSecureClientParameters(_clientCaCertificate, _serverCommonName);
            }
        }
    }
}