using System;
using System.Collections.Generic;
using FishNet.Utility.Performance;
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
        private LocalConnectionState m_ClientState;

        private void SetClientConnectionState(LocalConnectionState state)
        {
            m_ClientState = state;
            HandleClientConnectionState(new ClientConnectionStateArgs(state, Index));
        }

        /// <summary>
        /// Connects client to the server
        /// Note:
        /// When this method returns false it could mean:
        /// - You are trying to start a client that is already started
        /// - It failed during the initial port binding when attempting to begin to connect
        /// </summary>
        /// <returns>true if the client was started and false if it failed to start the client</returns>
        private bool StartClient()
        {
            if (m_ServerState == LocalConnectionState.Starting || m_ServerState == LocalConnectionState.Started)
            {
                return StartClientHost();
            }

            if (m_Driver.IsCreated)
            {
                return false;
            }

            SetClientConnectionState(LocalConnectionState.Starting);

            InitializeNetworkSettings();

            bool succeeded = ClientBindAndConnect();
            if (!succeeded)
            {
                SetClientConnectionState(LocalConnectionState.Stopping);
                if (m_Driver.IsCreated)
                {
                    m_Driver.Dispose();
                }
                SetClientConnectionState(LocalConnectionState.Stopped);
            }

            return succeeded;
        }

        private bool ClientBindAndConnect()
        {
            var serverEndpoint = default(NetworkEndpoint);

            if (m_ProtocolType == ProtocolType.RelayUnityTransport)
            {
                //This comparison is currently slow since RelayServerData does not implement a custom comparison operator that doesn't use
                //reflection, but this does not live in the context of a performance-critical loop, it runs once at initial connection time.
                if (m_RelayServerData.Equals(default(RelayServerData)))
                {
                    Debug.LogError("You must call SetRelayServerData() at least once before calling StartClient.");
                    return false;
                }

                m_NetworkSettings.WithRelayParameters(ref m_RelayServerData, m_HeartbeatTimeoutMS);
                serverEndpoint = m_RelayServerData.Endpoint;
            }
            else
            {
                serverEndpoint = ConnectionData.ServerEndPoint;
            }

            // Verify the endpoint is valid before proceeding
            if (serverEndpoint.Family == NetworkFamily.Invalid)
            {
                Debug.LogError($"Target server network address ({ConnectionData.Address}) is {nameof(NetworkFamily.Invalid)}!");
                return false;
            }

            InitDriver();

            var bindEndpoint = serverEndpoint.Family == NetworkFamily.Ipv6 ? NetworkEndpoint.AnyIpv6 : NetworkEndpoint.AnyIpv4;
            int result = m_Driver.Bind(bindEndpoint);
            if (result != 0)
            {
                Debug.LogError("Client failed to bind");
                return false;
            }

            var serverConnection = m_Driver.Connect(serverEndpoint);
            m_ServerClientId = ParseClientId(serverConnection);

            return true;
        }

        private bool StopClient()
        {
            if (m_ClientState == LocalConnectionState.Stopping || m_ClientState == LocalConnectionState.Stopped)
            {
                return false;
            }

            if (m_ServerState == LocalConnectionState.Starting || m_ServerState == LocalConnectionState.Started)
            {
                return StopClientHost();
            }

            DisconnectLocalClient();
            Disconnect();

            return true;
        }

        /// <summary>
        /// Disconnects the local client from the remote
        /// </summary>
        private void DisconnectLocalClient()
        {
            if (m_ClientState == LocalConnectionState.Started || m_ClientState == LocalConnectionState.Starting)
            {
                SetClientConnectionState(LocalConnectionState.Stopping);
                FlushSendQueuesForClientId(m_ServerClientId);

                if (m_Driver.Disconnect(ParseClientId(m_ServerClientId)) == 0)
                {
                    m_ReliableReceiveQueues.Remove(m_ServerClientId);
                    ClearSendQueuesForClientId(m_ServerClientId);

                    // If we successfully disconnect we dispatch a local disconnect message
                    // this how uNET and other transports worked and so this is just keeping with the old behavior
                    // should be also noted on the client this will call shutdown on the NetworkManager and the Transport
                    SetClientConnectionState(LocalConnectionState.Stopped);
                }
            }
        }

        private readonly struct ClientHostSendData
        {
            public byte[] Data { get; }
            public int Length { get; }
            public Channel Channel { get; }

            public ClientHostSendData(Channel channel, ArraySegment<byte> data)
            {
                if (data.Array == null) throw new InvalidOperationException();

                Data = new byte[data.Count];
                Length = data.Count;
                Buffer.BlockCopy(data.Array, data.Offset, Data, 0, Length);
                Channel = channel;
            }
        }

        private const ulong k_ClientHostId = 0;
        private Queue<ClientHostSendData> m_ClientHostSendQueue;
        private Queue<ClientHostSendData> m_ClientHostReceiveQueue;

        private bool StartClientHost()
        {
            if (m_ClientState != LocalConnectionState.Stopped)
            {
                return false;
            }

            if (m_ServerState == LocalConnectionState.Started || m_ServerState == LocalConnectionState.Starting)
            {
                SetClientConnectionState(LocalConnectionState.Starting);
                m_ServerClientId = k_ClientHostId;
                SetClientConnectionState(LocalConnectionState.Started);
                HandleRemoteConnectionState(RemoteConnectionState.Started, m_ServerClientId);
                return true;
            }
            return false;
        }

        private bool StopClientHost()
        {
            if (m_ServerState == LocalConnectionState.Stopping || m_ServerState == LocalConnectionState.Stopped)
            {
                return false;
            }

            if (m_ClientState == LocalConnectionState.Stopping || m_ClientState == LocalConnectionState.Stopped)
            {
                return false;
            }

            SetClientConnectionState(LocalConnectionState.Stopping);
            HandleRemoteConnectionState(RemoteConnectionState.Stopped, m_ServerClientId);
            DisposeClientHost();
            SetClientConnectionState(LocalConnectionState.Stopped);

            return true;
        }

        private void DisposeClientHost()
        {
            m_ServerClientId = default;
            m_ClientHostSendQueue?.Clear();
            m_ClientHostReceiveQueue?.Clear();

            m_ClientHostSendQueue = null;
            m_ClientHostReceiveQueue = null;
        }

        private void IterateClientHost(bool asServer)
        {
            if (asServer)
            {
                while (m_ClientHostSendQueue != null && m_ClientHostSendQueue.Count > 0)
                {
                    ClientHostSendData packet = m_ClientHostSendQueue.Dequeue();
                    var segment = new ArraySegment<byte>(packet.Data, 0, packet.Length);
                    int connectionId = TransportIdToClientId(k_ClientHostId);
                    HandleServerReceivedDataArgs(new ServerReceivedDataArgs(segment, packet.Channel, connectionId, Index));
                }
            }
            else
            {
                while (m_ClientHostReceiveQueue != null && m_ClientHostReceiveQueue.Count > 0)
                {
                    ClientHostSendData packet = m_ClientHostReceiveQueue.Dequeue();
                    var segment = new ArraySegment<byte>(packet.Data, 0, packet.Length);
                    HandleClientReceivedDataArgs(new ClientReceivedDataArgs(segment, packet.Channel, Index));
                    ByteArrayPool.Store(packet.Data);
                }
            }
        }

        private void SendToClientHost(int channelId, ArraySegment<byte> payload)
        {
            m_ClientHostReceiveQueue ??= new Queue<ClientHostSendData>();

            m_ClientHostReceiveQueue.Enqueue(new ClientHostSendData((Channel)channelId, payload));
        }

        private void ClientHostSendToServer(int channelId, ArraySegment<byte> payload)
        {
            m_ClientHostSendQueue ??= new Queue<ClientHostSendData>();

            m_ClientHostSendQueue.Enqueue(new ClientHostSendData((Channel)channelId, payload));
        }
    }
}