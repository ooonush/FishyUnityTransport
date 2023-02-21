using System;
using FishNet.Managing.Logging;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using UnityEngine;

namespace FishNet.Transporting.FishyUnityTransport
{
    internal class ClientSocket : CommonSocket
    {
        private NetworkConnection _connection;
        
        private bool StartRelayClientConnect(ref RelayServerData relayServerData, int heartbeatTimeoutMS)
        {
            //This comparison is currently slow since RelayServerData does not implement a custom comparison operator that doesn't use
            //reflection, but this does not live in the context of a performance-critical loop, it runs once at initial connection time.
            if (relayServerData.Equals(default(RelayServerData)))
            {
                Debug.LogError("You must call SetRelayServerData() at least once before calling StartRelayServer.");
                return false;
            }

            _networkSettings.WithRelayParameters(ref relayServerData, heartbeatTimeoutMS);
            ClientBindAndConnect(relayServerData.Endpoint);
            return true;
        }

        internal bool StartConnection()
        {
            if (Driver.IsCreated || GetLocalConnectionState() != LocalConnectionState.Stopped)
            {
                if (Transport.NetworkManager.CanLog(LoggingType.Error))
                    Debug.LogError("Attempting to start a client that is already active.");
                return false;
            }
            
            SetLocalConnectionState(LocalConnectionState.Starting, false);

            bool succeeded = Transport.UseRelay 
                ? StartRelayClientConnect(ref Transport.RelayServerData, Transport.HeartbeatTimeoutMS) 
                : ClientBindAndConnect(Transport.ConnectionData.ServerEndPoint);

            if (succeeded) return true;
            SetLocalConnectionState(LocalConnectionState.Stopped, false);
            if (Driver.IsCreated)
            {
                Driver.Dispose();
            }

            // SetLocalConnectionState(LocalConnectionState.Started, false);

            return false;
        }

        private bool ClientBindAndConnect(NetworkEndPoint serverEndpoint)
        {
            InitDriver();

            NetworkEndPoint bindEndpoint = serverEndpoint.Family == NetworkFamily.Ipv6 ? NetworkEndPoint.AnyIpv6 : NetworkEndPoint.AnyIpv4;
            Driver.Bind(bindEndpoint);
            if (!Driver.Bound)
            {
                Debug.LogError("Client failed to bind");
                return false;
            }

            _connection = Driver.Connect(serverEndpoint);
            
            return true;
        }

        /// <summary>
        /// Stops the client connection.
        /// </summary>
        internal bool StopClient()
        {
            LocalConnectionState state = GetLocalConnectionState();
            if (state is LocalConnectionState.Stopped or LocalConnectionState.Stopping)
                return false;

            SetLocalConnectionState(LocalConnectionState.Stopping, false);
            
            foreach (var kvp in _sendQueue)
            {
                SendMessages(kvp.Key, kvp.Value);
            }

            if (_connection.IsCreated)
            {
                _connection.Disconnect(Driver);
                _connection = default;
            }

            Driver.ScheduleUpdate().Complete();

            if (Driver.IsCreated)
            {
                Driver.Dispose();
            }
            DisposeQueues();

            SetLocalConnectionState(LocalConnectionState.Stopped, false);
            return true;
        }

        /// <summary>
        /// Iterates through all incoming packets and handles them.
        /// </summary>
        internal void IterateIncoming()
        {
            if (GetLocalConnectionState() == LocalConnectionState.Stopped || GetLocalConnectionState() == LocalConnectionState.Stopping)
                return;
            
            Driver.ScheduleUpdate().Complete();

            NetworkEvent.Type incomingEvent;
            while ((incomingEvent = _connection.PopEvent(Driver, out DataStreamReader stream, out NetworkPipeline pipeline)) !=
                   NetworkEvent.Type.Empty)
            {
                switch (incomingEvent)
                {
                    case NetworkEvent.Type.Data:
                        ReceiveMessages(_connection.GetHashCode(), pipeline, stream, false);
                        break;
                    case NetworkEvent.Type.Connect:
                        SetLocalConnectionState(LocalConnectionState.Started, false);
                        break;
                    case NetworkEvent.Type.Disconnect:
                        StopClient();
                        break;
                }
            }
        }

        /// <summary>
        /// Sends a packet to the server.
        /// </summary>
        internal void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started)
            {
                return;
            }
            
            Send(channelId, segment, _connection);
        }

        protected override void OnTransportFailure(NetworkConnection connection, ArraySegment<byte> message)
        {
            if (connection == _connection)
            {
                StopClient();
            }
        }
    }
}