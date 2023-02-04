using System;
using System.Collections.Generic;
using FishNet.Managing.Logging;
using FishNet.Transporting;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Error;
using UnityEngine;

namespace FishyUnityTransport
{
    internal abstract class CommonSocket
    {
        /// <summary>
        /// Transport controlling this socket.
        /// </summary>
        protected FishyUnityTransport Transport;

        protected NetworkSettings _networkSettings;
        
        #region Connection States
        /// <summary>
        /// Current ConnectionState.
        /// </summary>
        private LocalConnectionState _connectionState = LocalConnectionState.Stopped;
        
        /// <summary>
        /// Returns the current ConnectionState.
        /// </summary>
        /// <returns></returns>
        internal LocalConnectionState GetLocalConnectionState()
        {
            return _connectionState;
        }
        #endregion
        
        #region Transport
        /// <summary>
        /// Unity transport driver to send and receive data.
        /// </summary>
        protected NetworkDriver Driver;

        /// <summary>
        /// A pipeline on the driver that is sequenced, and ensures messages are delivered.
        /// </summary>
        protected NetworkPipeline ReliablePipeline;

        /// <summary>
        /// A pipeline on the driver that is sequenced, but does not ensure messages are delivered.
        /// </summary>
        protected NetworkPipeline UnreliablePipeline;
        #endregion

        #region Queues
        /// <summary>
        /// SendQueue dictionary is used to batch events instead of sending them immediately.
        /// </summary>
        protected readonly Dictionary<SendTarget, BatchedSendQueue> _sendQueue = new();

        /// <summary>
        /// SendQueue dictionary is used to batch events instead of sending them immediately.
        /// </summary>
        protected readonly Dictionary<int, BatchedReceiveQueue> _reliableReceiveQueue = new();
        #endregion

        protected void FlushSendQueuesForConnection(NetworkConnection connection)
        {
            foreach (var kvp in _sendQueue)
            {
                if (kvp.Key.Connection == connection)
                {
                    SendMessages(kvp.Key, kvp.Value);
                }
            }
        }

        protected void ClearSendQueuesForConnection(NetworkConnection connection)
        {
            // NativeList and manual foreach avoids any allocations.
            using var keys = new NativeList<SendTarget>(16, Allocator.Temp);
            foreach (SendTarget key in _sendQueue.Keys)
            {
                if (key.Connection == connection)
                {
                    keys.Add(key);
                }
            }

            foreach (SendTarget target in keys)
            {
                _sendQueue[target].Dispose();
                _sendQueue.Remove(target);
            }
        }

        internal void Dispose()
        {
            if (Driver.IsCreated)
            {
                Driver.Dispose();
            }

            _networkSettings.Dispose();

            DisposeQueues();
        }
        
        protected void DisposeQueues()
        {
            foreach (BatchedSendQueue queue in _sendQueue.Values)
            {
                queue.Dispose();
            }

            _reliableReceiveQueue.Clear();
            _sendQueue.Clear();
        }
        
        /// <summary>
        /// Initializes this for use.
        /// </summary>
        /// <param name="transport"></param>
        internal void Initialize(FishyUnityTransport transport)
        {
            Transport = transport;
            _networkSettings = new NetworkSettings(Allocator.Persistent);
        }
        
        /// <summary>
        /// Sets a new connection state.
        /// </summary>
        /// <param name="connectionState"></param>
        /// <param name="server"></param>
        protected void SetLocalConnectionState(LocalConnectionState connectionState, bool server)
        {
            if (connectionState == _connectionState)
                return;

            _connectionState = connectionState;

            if (server)
                Transport.HandleServerConnectionState(new ServerConnectionStateArgs(connectionState,
                    Transport.Index));
            else
                Transport.HandleClientConnectionState(new ClientConnectionStateArgs(connectionState,
                    Transport.Index));
        }
        
        private const int MAX_RELIABLE_THROUGHPUT = NetworkParameterConstants.MTU * 32 * 60 / 1000; // bytes per millisecond

        /// <summary>
        /// Sends a message via the transport
        /// </summary>
        protected void Send(int channelId, ArraySegment<byte> message, NetworkConnection connection)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started)
                return;

            NetworkPipeline pipeline = GetNetworkPipeline((Channel)channelId);

            if (pipeline != ReliablePipeline && message.Count > Transport.MaxPayloadSize)
            {
                Debug.LogError(
                    $"Unreliable payload of size {message.Count} larger than configured 'Max Payload Size' ({Transport.MaxPayloadSize}).");
                return;
            }
            
            var sendTarget = new SendTarget(connection, pipeline);

            if (!_sendQueue.TryGetValue(sendTarget, out BatchedSendQueue queue))
            {
                // The maximum reliable throughput, assuming the full reliable window can be sent on every
                // tick. This will be a large over-estimation in any realistic scenario.

                const int maxCapacity = NetworkParameterConstants.DisconnectTimeoutMS * MAX_RELIABLE_THROUGHPUT;

                queue = new BatchedSendQueue(Math.Max(maxCapacity, Transport.MaxPayloadSize));

                _sendQueue.Add(sendTarget, queue);
            }

            if (queue.PushMessage(message)) return;
            
            if (pipeline == ReliablePipeline)
            {
                // If the message is sent reliably, then we're over capacity and we can't
                // provide any reliability guarantees anymore. Disconnect the client since at
                // this point they're bound to become desynchronized.

                Debug.LogError($"Couldn't add payload of size {message.Count} to reliable send queue. " +
                               $"Closing connection {GetConnectionId(connection)} as reliability guarantees can't be maintained.");

                OnTransportFailure(connection, message);
            }
            else
            {
                // If the message is sent unreliably, we can always just flush everything out
                // to make space in the send queue. This is an expensive operation, but a user
                // would need to send A LOT of unreliable traffic in one update to get here.

                Driver.ScheduleFlushSend(default).Complete();
                SendMessages(sendTarget, queue);

                // Don't check for failure. If it still doesn't work, there's nothing we can do
                // at this point and the message is lost (it was sent unreliable anyway).
                queue.PushMessage(message);
            }
        }

        protected virtual void OnTransportFailure(NetworkConnection connection, ArraySegment<byte> message)
        {
            
        }

        /// <summary>
        /// Processes data to be sent by the socket.
        /// </summary>
        internal void IterateOutgoing()
        {
            LocalConnectionState state = GetLocalConnectionState();
            if (state is LocalConnectionState.Stopped or LocalConnectionState.Stopping)
                return;
            
            foreach (var kvp in _sendQueue)
            {
                SendMessages(kvp.Key, kvp.Value);
            }
        }

        protected static int GetConnectionId(NetworkConnection connection)
        {
            return connection.GetHashCode();
        }
        
        /// <summary>
        /// Send all queued messages
        /// </summary>
        protected void SendMessages(SendTarget target, BatchedSendQueue queue)
        {
            NetworkPipeline pipeline = target.NetworkPipeline;
            NetworkConnection connection = target.Connection;
            
            while (!queue.IsEmpty)
            {
                int status = Driver.BeginSend(pipeline, connection, out DataStreamWriter writer);
                if (status != (int)StatusCode.Success)
                {
                    if (Transport.NetworkManager.CanLog(LoggingType.Error))
                        Debug.LogError("Error sending the message: " + (StatusCode)status + " " + GetConnectionId(connection));
                    return;
                }
                
                int written = pipeline == ReliablePipeline ?  queue.FillWriterWithBytes(ref writer) : queue.FillWriterWithMessages(ref writer);
                
                status = Driver.EndSend(writer);
                if (status == written)
                {
                    // Batched message was sent successfully. Remove it from the queue.
                    queue.Consume(written);
                }
                else
                {
                    if (status == (int)StatusCode.NetworkSendQueueFull) return;

                    if (Transport.NetworkManager.CanLog(LoggingType.Error))
                        Debug.LogError("Error sending the message: " + status + ", " + connection.InternalId);
                    queue.Consume(written);

                    return;
                }
            }
        }

        /// <summary>
        /// Returns a message from the transport
        /// </summary>
        protected void ReceiveMessages(int connectionId, NetworkPipeline pipeline, DataStreamReader reader, bool server = true)
        {
            BatchedReceiveQueue queue;
            if (pipeline == ReliablePipeline)
            {
                if (_reliableReceiveQueue.TryGetValue(connectionId, out queue))
                {
                    queue.PushReader(reader);
                }
                else
                {
                    queue = new BatchedReceiveQueue(reader);
                    _reliableReceiveQueue[connectionId] = queue;
                }
            }
            else
            {
                queue = new BatchedReceiveQueue(reader);
            }

            while (!queue.IsEmpty)
            {
                var message = queue.PopMessage();
                if (message == default)
                {
                    break;
                }
                
                Channel channel = pipeline == ReliablePipeline ? Channel.Reliable : Channel.Unreliable;

                if (server)
                {
                    Transport.HandleServerReceivedDataArgs(new ServerReceivedDataArgs(message, channel, connectionId, Transport.Index));
                }
                else
                {
                    Transport.HandleClientReceivedDataArgs(new ClientReceivedDataArgs(message, channel, Transport.Index));
                }
            }
        }


        /// <summary>
        /// Returns this drivers max header size based on the requested channel.
        /// </summary>
        /// <param name="channelId">The channel to check.</param>
        /// <returns>This client's max header size.</returns>
        public int GetMaxHeaderSize(int channelId = (int) Channel.Reliable)
        {
            if (GetLocalConnectionState() == LocalConnectionState.Started)
            {
                return Driver.MaxHeaderSize(GetNetworkPipeline((Channel)channelId));
            }

            return 0;
        }
        
        protected void InitDriver()
        {
            Driver = NetworkDriver.Create(_networkSettings);

            ReliablePipeline = Driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            UnreliablePipeline = Driver.CreatePipeline(typeof(UnreliableSequencedPipelineStage));
        }
        
        protected NetworkPipeline GetNetworkPipeline(Channel channel)
        {
            return channel switch
            {
                Channel.Reliable => ReliablePipeline,
                Channel.Unreliable => UnreliablePipeline,
                _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
            };
        }
    }
}