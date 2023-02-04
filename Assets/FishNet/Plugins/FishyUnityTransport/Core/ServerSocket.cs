using System;
using FishNet.Managing.Logging;
using FishNet.Transporting;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using UnityEngine;

namespace FishyUnityTransport
{
    internal class ServerSocket : CommonSocket
    {
        #region Private
        /// <summary>
        /// Client connections to this server.
        /// </summary>
        private NativeList<NetworkConnection> _connections;
        
        /// <summary>
        /// Maximum number of connections allowed.
        /// </summary>
        private int _maximumClients = short.MaxValue;
        #endregion
        
        /// <summary>
        /// Starts the server.
        /// </summary>
        public bool StartConnection(int maximumClients)
        {
            SetMaximumClients(maximumClients);
            
            if (Driver.IsCreated || GetLocalConnectionState() != LocalConnectionState.Stopped)
            {
                if (Transport.NetworkManager.CanLog(LoggingType.Error))
                    Debug.LogError("Attempting to start a server that is already active.");
                return false;
            }
            
            SetLocalConnectionState(LocalConnectionState.Starting, true);
            
            bool succeeded = Transport.UseRelay 
                ? StartRelayServer(ref Transport.RelayServerData, Transport.HeartbeatTimeoutMS) 
                : ServerBindAndListen(Transport.ConnectionData.ListenEndPoint);

            if (!succeeded && Driver.IsCreated)
            {
                Driver.Dispose();
                SetLocalConnectionState(LocalConnectionState.Stopped, true);
            }
            
            SetLocalConnectionState(LocalConnectionState.Started, true);

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

            _networkSettings.WithRelayParameters(ref relayServerData, heartbeatTimeoutMS);
            return ServerBindAndListen(NetworkEndPoint.AnyIpv4);
        }

        private bool ServerBindAndListen(NetworkEndPoint endPoint)
        {
            InitDriver();
            
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

            // Finally, create a NativeList to hold all the connections
            _connections = new NativeList<NetworkConnection>(16, Allocator.Persistent);
            
            return true;
        }
        
        /// <summary>
        /// Stops the server.
        /// </summary>
        public bool StopServer()
        {
            LocalConnectionState state = GetLocalConnectionState();
            if (state is LocalConnectionState.Stopped or LocalConnectionState.Stopping) return false;
            
            SetLocalConnectionState(LocalConnectionState.Stopping, true);
            
            foreach (var kvp in _sendQueue)
            {
                SendMessages(kvp.Key, kvp.Value);
            }

            if (_connections.IsCreated)
            {
                _connections.Dispose();
            }

            Driver.ScheduleUpdate().Complete();
            
            if (Driver.IsCreated)
            {
                Driver.Dispose();
            }
            DisposeQueues();
            
            SetLocalConnectionState(LocalConnectionState.Stopped, true);
            return true;
        }

        /// <summary>
        /// Stops a remote client from the server, disconnecting the client.
        /// </summary>
        /// <param name="connection"></param>
        private bool StopRemoteConnection(NetworkConnection connection)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return false;

            Transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, GetConnectionId(connection), Transport.Index));
            
            FlushSendQueuesForConnection(connection);
            
            _reliableReceiveQueue.Remove(GetConnectionId(connection));
            ClearSendQueuesForConnection(connection);

            if (Driver.GetConnectionState(connection) != NetworkConnection.State.Disconnected)
            {
                Driver.Disconnect(connection);
            }

            _connections.RemoveAt(_connections.IndexOf(connection));
            
            Driver.ScheduleUpdate().Complete();
            
            return true;
        }

        internal bool StopConnection(int connectionId)
        {
            foreach (NetworkConnection connection in _connections)
            {
                if (GetConnectionId(connection) == connectionId)
                {
                    return StopRemoteConnection(connection);
                }
            }

            return false;
        }

        private bool TryGetConnection(int connectionId, out NetworkConnection connection)
        {
            if (_connections.IsCreated)
            {
                foreach (NetworkConnection c in _connections)
                {
                    if (GetConnectionId(c) == connectionId)
                    {
                        connection = c;
                        return true;
                    }
                }
            }

            connection = default;
            return false;
        }

        public string GetConnectionAddress(int connectionId)
        {
            if (!TryGetConnection(connectionId, out NetworkConnection connection)) return string.Empty;

            NetworkEndPoint endpoint = Driver.RemoteEndPoint(connection);
            return endpoint.Address;
        }

        /// <summary>
        /// Gets the current ConnectionState of a remote client on the server.
        /// </summary>
        /// <param name="connectionId">ConnectionId to get ConnectionState for.</param>
        internal RemoteConnectionState GetConnectionState(int connectionId)
        {
            return !TryGetConnection(connectionId, out _) ? RemoteConnectionState.Stopped : RemoteConnectionState.Started;
        }

        /// <summary>
        /// Returns the maximum number of clients allowed to connect to the server.
        /// If the transport does not support this method the value -1 is returned.
        /// </summary>
        public int GetMaximumClients()
        {
            return _maximumClients;
        }

        /// <summary>
        /// Sets the maximum number of clients allowed to connect to the server.
        /// </summary>
        public void SetMaximumClients(int value)
        {
            _maximumClients = value;
        }

        /// <summary>
        /// Send data to a connection over a particular channel.
        /// </summary>
        public void SendToClient(int channelId, ArraySegment<byte> message, int connectionId)
        {
            if (!TryGetConnection(connectionId, out NetworkConnection connection)) return;
            Send(channelId, message, connection);
        }
        
        private void HandleIncomingConnections()
        {
            NetworkConnection incomingConnection;
            while ((incomingConnection = Driver.Accept()) != default)
            {
                _connections.Add(incomingConnection);

                if (_connections.Length > _maximumClients)
                {
                    StopRemoteConnection(incomingConnection);
                    return;
                }
                
                Transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, incomingConnection.GetHashCode(), Transport.Index));
            }
        }
        
        private void HandleIncomingEvents()
        {
            NetworkEvent.Type netEvent;
            while ((netEvent = Driver.PopEvent(out NetworkConnection connection, out DataStreamReader stream, out NetworkPipeline pipeline)) !=
                   NetworkEvent.Type.Empty)
            {
                switch (netEvent)
                {
                    case NetworkEvent.Type.Data:
                        ReceiveMessages(GetConnectionId(connection), pipeline, stream);
                        break;
                    case NetworkEvent.Type.Disconnect:
                        StopRemoteConnection(connection);
                        break;
                }
            }
        }

        /// <summary>
        /// Iterates through all incoming packets and handles them.
        /// </summary>
        internal void IterateIncoming()
        {
            if (!Driver.IsCreated ||
                GetLocalConnectionState() == LocalConnectionState.Stopped ||
                GetLocalConnectionState() == LocalConnectionState.Stopping)
                return;

            Driver.ScheduleUpdate().Complete();

            if (Transport.UseRelay && Driver.GetRelayConnectionStatus() == RelayConnectionStatus.AllocationInvalid)
            {
                Debug.LogError("Transport failure! Relay allocation needs to be recreated, and NetworkManager restarted. " +
                               "Use NetworkManager.OnTransportFailure to be notified of such events programmatically.");
                
                // TODO
                // InvokeOnTransportEvent(NetcodeNetworkEvent.TransportFailure, 0, default, Time.realtimeSinceStartup);
                return;
            }
            
            HandleIncomingConnections();
            HandleIncomingEvents();
        }
    }
}