using System;
using FishNet.Managing.Logging;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using UnityEngine;

namespace FishNet.Transporting.FishyUnityTransport
{
    [Serializable]
    internal class ClientSocket : CommonSocket
    {
        public ulong ServerClientId;

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
            NetworkEndPoint serverEndpoint;

            if (Transport.ProtocolType == ProtocolType.RelayUnityTransport)
            {
                //This comparison is currently slow since RelayServerData does not implement a custom comparison operator that doesn't use
                //reflection, but this does not live in the context of a performance-critical loop, it runs once at initial connection time.
                if (Transport.RelayServerData.Equals(default(RelayServerData)))
                {
                    Debug.LogError("You must call SetRelayServerData() at least once before calling StartRelayServer.");
                    return false;
                }

                NetworkSettings.WithRelayParameters(ref Transport.RelayServerData, Transport.HeartbeatTimeoutMS);
                serverEndpoint = Transport.RelayServerData.Endpoint;
            }
            else
            {
                serverEndpoint = Transport.ConnectionData.ServerEndPoint;
            }

            InitDriver();

            NetworkEndPoint bindEndpoint = serverEndpoint.Family == NetworkFamily.Ipv6 ? NetworkEndPoint.AnyIpv6 : NetworkEndPoint.AnyIpv4;
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
            Transport.HandleReceivedData(message, channel, clientId, Transport.Index, false);
        }
    }
}