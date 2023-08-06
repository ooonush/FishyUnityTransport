using System;
using FishNet.Managing.Logging;
using Unity.Networking.Transport;
using UnityEngine;

namespace FishNet.Transporting.UTP
{
    public partial class FishyUnityTransport
    {
        [Range(1, 4095)]
        [SerializeField] private int m_MaximumClients = 4095;

        public override event Action<ClientConnectionStateArgs> OnClientConnectionState;
        public override event Action<ServerConnectionStateArgs> OnServerConnectionState;
        public override event Action<RemoteConnectionStateArgs> OnRemoteConnectionState;

        private void OnDestroy()
        {
            Shutdown();
        }

        public override string GetConnectionAddress(int connectionId)
        {
            ulong transportId = ClientIdToTransportId(connectionId);
            NetworkConnection connection = ParseClientId(transportId);
            return connection.GetState(m_Driver) == NetworkConnection.State.Disconnected
                ? string.Empty
#if UTP_TRANSPORT_2_0_ABOVE
                : m_Driver.GetRemoteEndpoint(connection).Address;
#else
                : m_Driver.RemoteEndPoint(connection).Address;
#endif
        }

        public override LocalConnectionState GetConnectionState(bool server)
        {
            return server ? m_ServerState : m_ClientState;
        }

        public override RemoteConnectionState GetConnectionState(int connectionId)
        {
            ulong transportId = ClientIdToTransportId(connectionId);
            return ParseClientId(transportId).GetState(m_Driver) == NetworkConnection.State.Connected
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

        public override void IterateIncoming(bool server)
        {
            if (m_ClientState == LocalConnectionState.Started && m_ServerState == LocalConnectionState.Started)
            {
                IterateClientHost(server);
            }

            IterateIncoming();
        }

        public override void IterateOutgoing(bool server)
        {
            if (server || m_ServerState != LocalConnectionState.Started)
            {
                IterateOutgoing();
            }
        }

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

        public override void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            if (m_ServerState == LocalConnectionState.Started)
            {
                ClientHostSendToServer(channelId, segment);
            }
            else
            {
                Send(m_ServerClientId, segment, (Channel)channelId);
            }
        }

        public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        {
            ulong transportId = ClientIdToTransportId(connectionId);
            if (m_ClientState == LocalConnectionState.Started && transportId == m_ServerClientId)
            {
                SendToClientHost(channelId, segment);
            }
            else
            {
                Send(transportId, segment, (Channel)channelId);
            }
        }

        public override int GetMaximumClients() => m_MaximumClients;

        public override void SetMaximumClients(int value)
        {
            if (m_ServerState == LocalConnectionState.Starting || m_ServerState == LocalConnectionState.Started)
            {
                if (NetworkManager.CanLog(LoggingType.Warning))
                    Debug.LogWarning($"Cannot set maximum clients when server is running.");
            }
            else
            {
                m_MaximumClients = value;
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
            ulong transportId = ClientIdToTransportId(connectionId);
            return transportId == m_ServerClientId ? StopClientHost() : DisconnectRemoteClient(transportId);
        }

        public override void Shutdown()
        {
            StopConnection(false);
            StopConnection(true);
        }

        public override int GetMTU(byte channelId)
        {
            // Check for client activity
            if (m_ClientState == LocalConnectionState.Started || m_ServerState == LocalConnectionState.Started)
            {
                NetworkPipeline pipeline = SelectSendPipeline((Channel)channelId);
                return NetworkParameterConstants.MTU - m_Driver.MaxHeaderSize(pipeline);
            }

            return NetworkParameterConstants.MTU;
        }

        private Channel SelectSendChannel(NetworkPipeline pipeline)
        {
            return pipeline == m_ReliableSequencedPipeline ? Channel.Reliable : Channel.Unreliable;
        }
    }
}