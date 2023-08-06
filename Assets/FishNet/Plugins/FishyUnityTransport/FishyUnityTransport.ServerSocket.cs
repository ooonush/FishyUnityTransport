using System;
using System.Collections.Generic;
using UnityEngine;
using TransportNetworkEvent = Unity.Networking.Transport.NetworkEvent;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
#if UTP_TRANSPORT_2_0_ABOVE
using Unity.Networking.Transport.TLS;
#endif

#if !UTP_TRANSPORT_2_0_ABOVE
using NetworkEndpoint = Unity.Networking.Transport.NetworkEndPoint;
#endif

namespace FishNet.Transporting.UTP
{
    public partial class FishyUnityTransport
    {
        private LocalConnectionState m_ServerState;
        private int m_NextClientId = 1;
        private readonly Dictionary<int, ulong> m_TransportIdToClientIdMap = new Dictionary<int, ulong>();
        private readonly Dictionary<ulong, int> m_ClientIdToTransportIdMap = new Dictionary<ulong, int>();

        private int TransportIdToClientId(ulong connection) => m_ClientIdToTransportIdMap[connection];

        private ulong ClientIdToTransportId(int transportId) => m_TransportIdToClientIdMap[transportId];

        private void HandleRemoteConnectionState(RemoteConnectionState state, ulong clientId)
        {
            int transportId;
            switch (state)
            {
                case RemoteConnectionState.Started:
                    transportId = m_NextClientId++;
                    m_TransportIdToClientIdMap[transportId] = clientId;
                    m_ClientIdToTransportIdMap[clientId] = transportId;
                    break;
                case RemoteConnectionState.Stopped:
                    transportId = m_ClientIdToTransportIdMap[clientId];
                    m_TransportIdToClientIdMap.Remove(transportId);
                    m_ClientIdToTransportIdMap.Remove(clientId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }

            HandleRemoteConnectionState(new RemoteConnectionStateArgs(state, transportId, Index));
        }

        private void SetServerConnectionState(LocalConnectionState state)
        {
            m_ServerState = state;
            HandleServerConnectionState(new ServerConnectionStateArgs(state, Index));
        }

        /// <summary>
        /// Starts to listening for incoming clients
        /// Note:
        /// When this method returns false it could mean:
        /// - You are trying to start a client that is already started
        /// - It failed during the initial port binding when attempting to begin to connect
        /// </summary>
        /// <returns>true if the server was started and false if it failed to start the server</returns>
        private bool StartServer()
        {
            if (m_Driver.IsCreated)
            {
                return false;
            }

            SetServerConnectionState(LocalConnectionState.Starting);

            InitializeNetworkSettings();

            bool succeeded = Protocol switch
            {
                ProtocolType.UnityTransport => ServerBindAndListen(ConnectionData.ListenEndPoint),
                ProtocolType.RelayUnityTransport => StartRelayServer(),
                _ => false
            };

            if (succeeded)
            {
                SetServerConnectionState(LocalConnectionState.Started);
            }
            else
            {
                SetServerConnectionState(LocalConnectionState.Stopping);
                if (m_Driver.IsCreated)
                {
                    m_Driver.Dispose();
                }
                SetServerConnectionState(LocalConnectionState.Stopped);
            }

            return succeeded;
        }

        private bool ServerBindAndListen(NetworkEndpoint endPoint)
        {
            // Verify the endpoint is valid before proceeding
            if (endPoint.Family == NetworkFamily.Invalid)
            {
                Debug.LogError($"Network listen address ({ConnectionData.Address}) is {nameof(NetworkFamily.Invalid)}!");
                return false;
            }

            InitDriver();

            int result = m_Driver.Bind(endPoint);
            if (result != 0)
            {
                Debug.LogError("Server failed to bind. This is usually caused by another process being bound to the same port.");
                return false;
            }

            result = m_Driver.Listen();
            if (result != 0)
            {
                Debug.LogError("Server failed to listen.");
                return false;
            }

            return true;
        }

        private bool StartRelayServer()
        {
            //This comparison is currently slow since RelayServerData does not implement a custom comparison operator that doesn't use
            //reflection, but this does not live in the context of a performance-critical loop, it runs once at initial connection time.
            if (m_RelayServerData.Equals(default(RelayServerData)))
            {
                Debug.LogError("You must call SetRelayServerData() at least once before calling StartServer.");
                return false;
            }
            else
            {
                m_NetworkSettings.WithRelayParameters(ref m_RelayServerData, m_HeartbeatTimeoutMS);
                return ServerBindAndListen(NetworkEndpoint.AnyIpv4);
            }
        }

        private bool StopServer()
        {
            if (m_ServerState == LocalConnectionState.Stopping || m_ServerState == LocalConnectionState.Stopped)
            {
                return false;
            }

            if (m_ClientState == LocalConnectionState.Starting || m_ClientState == LocalConnectionState.Started)
            {
                StopClientHost();
            }

            SetServerConnectionState(LocalConnectionState.Stopping);
            Disconnect();
            m_NextClientId = 1;
            m_TransportIdToClientIdMap.Clear();
            m_ClientIdToTransportIdMap.Clear();
            SetServerConnectionState(LocalConnectionState.Stopped);

            return true;
        }

        /// <summary>
        /// Disconnects a remote client from the server
        /// </summary>
        /// <param name="clientId">The client to disconnect</param>
        private bool DisconnectRemoteClient(ulong clientId)
        {
            Debug.Assert(m_ServerState == LocalConnectionState.Started, "DisconnectRemoteClient should be called on a listening server");

            if (m_ServerState == LocalConnectionState.Started)
            {
                FlushSendQueuesForClientId(clientId);

                m_ReliableReceiveQueues.Remove(clientId);
                ClearSendQueuesForClientId(clientId);

                var connection = ParseClientId(clientId);
                if (m_Driver.GetConnectionState(connection) != NetworkConnection.State.Disconnected)
                {
                    m_Driver.Disconnect(connection);
                    return true;
                }
            }

            return false;
        }
    }
}