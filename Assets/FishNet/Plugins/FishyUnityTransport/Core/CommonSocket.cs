using System;
using System.Collections.Generic;
using FishNet.Managing;
using FishNet.Transporting.FishyUnityTransport.BatchedQueue;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Error;
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.Utilities;
using UnityEngine;
#if UTP_TRANSPORT_2_0_ABOVE
using Unity.Networking.Transport.TLS;
#endif

#if !UTP_TRANSPORT_2_0_ABOVE
using NetworkEndpoint = Unity.Networking.Transport.NetworkEndPoint;
#endif

namespace FishNet.Transporting.FishyUnityTransport
{
    internal abstract class CommonSocket : IDisposable
    {
        protected FishyUnityTransport Transport;
        protected NetworkManager NetworkManager;

        /// <summary>
        /// Unity transport driver to send and receive data.
        /// </summary>
        protected NetworkDriver Driver;
        protected NetworkSettings NetworkSettings;
        private NetworkPipeline _unreliableFragmentedPipeline;
        private NetworkPipeline _unreliableSequencedFragmentedPipeline;
        private NetworkPipeline _reliableSequencedPipeline;

        #region Queues

        /// <summary>
        /// SendQueue dictionary is used to batch events instead of sending them immediately.
        /// </summary>
        private readonly Dictionary<SendTarget, BatchedSendQueue> _sendQueue = new Dictionary<SendTarget, BatchedSendQueue>();

        /// <summary>
        /// SendQueue dictionary is used to batch events instead of sending them immediately.
        /// </summary>
        protected readonly Dictionary<NetworkConnection, BatchedReceiveQueue> ReliableReceiveQueues = new Dictionary<NetworkConnection, BatchedReceiveQueue>();

        #endregion

        /// <summary>
        /// Current ConnectionState.
        /// </summary>
        public LocalConnectionState State { get; protected set; } = LocalConnectionState.Stopped;

        public void Initialize(FishyUnityTransport transport)
        {
            Transport = transport;
        }

        protected void InitializeNetworkSettings()
        {
            NetworkSettings = new NetworkSettings(Allocator.Persistent);

#if !UNITY_WEBGL
            // If the user sends a message of exactly m_MaxPayloadSize in length, we need to
            // account for the overhead of its length when we store it in the send queue.
            int fragmentationCapacity = Transport.MaxPayloadSize + BatchedSendQueue.PerMessageOverhead;

            NetworkSettings.WithFragmentationStageParameters(payloadCapacity: fragmentationCapacity);
#if !UTP_TRANSPORT_2_0_ABOVE
            NetworkSettings.WithBaselibNetworkInterfaceParameters(
                receiveQueueCapacity: Transport.MaxPacketQueueSize,
                sendQueueCapacity: Transport.MaxPacketQueueSize);
#endif
#endif
        }

        protected void InitDriver(bool asServer)
        {
            CreateDriver(Transport, out Driver,
                out _unreliableFragmentedPipeline,
                out _unreliableSequencedFragmentedPipeline,
                out _reliableSequencedPipeline, asServer);
        }

        protected static int ParseTransportId(NetworkConnection connection) => connection.GetHashCode();

        protected abstract void SetLocalConnectionState(LocalConnectionState state);

        protected abstract void OnPushMessageFailure(int channelId, ArraySegment<byte> payload, NetworkConnection connection);

        protected abstract void HandleReceivedData(ArraySegment<byte> message, Channel channel, NetworkConnection connection);

        protected virtual void OnIterateIncoming() { }

        protected virtual void HandleDisconnectEvent(NetworkConnection connection) { }

        protected virtual void Shutdown()
        {
            if (!Driver.IsCreated)
            {
                return;
            }

            // Flush all send queues to the network. NGO can be configured to flush its message
            // queue on shutdown. But this only calls the Send() method, which doesn't actually
            // get anything to the network.
            foreach (var kvp in _sendQueue)
            {
                SendBatchedMessages(kvp.Key, kvp.Value);
            }

            // The above flush only puts the message in UTP internal buffers, need an update to
            // actually get the messages on the wire. (Normally a flush send would be sufficient,
            // but there might be disconnect messages and those require an update call.)
            Driver.ScheduleUpdate().Complete();

            Dispose();

            ReliableReceiveQueues.Clear();
        }

        #region Iterate Incoming

        /// <summary>
        /// Iterates through all incoming packets and handles them.
        /// </summary>
        internal void IterateIncoming()
        {
            if (!Driver.IsCreated || State == LocalConnectionState.Stopped || State == LocalConnectionState.Stopping)
                return;

            Driver.ScheduleUpdate().Complete();

            if (Transport.Protocol == ProtocolType.RelayUnityTransport && Driver.GetRelayConnectionStatus() == RelayConnectionStatus.AllocationInvalid)
            {
                Debug.LogError("Transport failure! Relay allocation needs to be recreated, and NetworkManager restarted. " +
                               "Use NetworkManager.OnTransportFailure to be notified of such events programmatically.");

                // TODO
                // InvokeOnTransportEvent(TransportFailure);
                return;
            }

            while (ProcessEvent() && Driver.IsCreated) { }

            OnIterateIncoming();
        }

        #endregion

        private bool ProcessEvent()
        {
            NetworkEvent.Type eventType = Driver.PopEvent(out NetworkConnection connection, out DataStreamReader reader,
                out NetworkPipeline pipeline);
            switch (eventType)
            {
                case NetworkEvent.Type.Connect:
                {
                    SetLocalConnectionState(LocalConnectionState.Started);
                    return true;
                }
                case NetworkEvent.Type.Disconnect:
                {
                    ReliableReceiveQueues.Remove(connection);
                    ClearSendQueuesForClientId(connection);

                    HandleDisconnectEvent(connection);

                    return true;
                }
                case NetworkEvent.Type.Data:
                {
                    ReceiveMessages(connection, pipeline, reader);
                    return true;
                }
                case NetworkEvent.Type.Empty:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return false;
        }

        /// <summary>
        /// Processes data to be sent by the socket.
        /// </summary>
        public void IterateOutgoing()
        {
            if (State == LocalConnectionState.Stopped || State == LocalConnectionState.Stopping) return;

            foreach (var kvp in _sendQueue)
            {
                SendBatchedMessages(kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// Returns this drivers max header size based on the requested channel.
        /// </summary>
        /// <param name="channel">The channel to check.</param>
        /// <returns>This client's max header size.</returns>
        public int GetMaxHeaderSize(Channel channel)
        {
            return State == LocalConnectionState.Started ? Driver.MaxHeaderSize(SelectSendPipeline(channel)) : 0;
        }

        public void Dispose()
        {
            if (Driver.IsCreated)
            {
                Driver.Dispose();
            }

            NetworkSettings.Dispose();

            foreach (BatchedSendQueue queue in _sendQueue.Values)
            {
                queue.Dispose();
            }

            _sendQueue.Clear();
        }

        /// <summary>
        /// Sends a message via the transport
        /// </summary>
        protected void Send(int channelId, ArraySegment<byte> payload, NetworkConnection connection)
        {
            if (State != LocalConnectionState.Started) return;

            NetworkPipeline pipeline = SelectSendPipeline((Channel)channelId);

            if (pipeline != _reliableSequencedPipeline && payload.Count > Transport.MaxPayloadSize)
            {
                Debug.LogError($"Unreliable payload of size {payload.Count} larger than configured 'Max Payload Size' ({Transport.MaxPayloadSize}).");
                return;
            }

            var sendTarget = new SendTarget(connection, pipeline);
            if (!_sendQueue.TryGetValue(sendTarget, out BatchedSendQueue queue))
            {
                // timeout. The idea being that if the send queue contains enough reliable data that
                // sending it all out would take longer than the disconnection timeout, then there's
                // no point storing even more in the queue (it would be like having a ping higher
                // than the disconnection timeout, which is far into the realm of unplayability).
                //
                // The throughput used to determine what consists the maximum send queue size is
                // the maximum theoritical throughput of the reliable pipeline assuming we only send
                // on each update at 60 FPS, which turns out to be around 2.688 MB/s.
                //
                // Note that we only care about reliable throughput for send queues because that's
                // the only case where a full send queue causes a connection loss. Full unreliable
                // send queues are dealt with by flushing it out to the network or simply dropping
                // new messages if that fails.

                int maxCapacity = Transport.MaxSendQueueSize > 0 ? Transport.MaxSendQueueSize : Transport.DisconnectTimeoutMS * FishyUnityTransport.MaxReliableThroughput;

                queue = new BatchedSendQueue(Math.Max(maxCapacity, Transport.MaxPayloadSize));

                _sendQueue.Add(sendTarget, queue);
            }

            if (!queue.PushMessage(payload))
            {
                if (pipeline == _reliableSequencedPipeline)
                {
                    // If the message is sent reliably, then we're over capacity and we can't
                    // provide any reliability guarantees anymore. Disconnect the client since at
                    // this point they're bound to become desynchronized.

                    Debug.LogError($"Couldn't add payload of size {payload.Count} to reliable send queue. " +
                                   $"Closing connection {connection} as reliability guarantees can't be maintained.");

                    OnPushMessageFailure(channelId, payload, connection);
                }
                else
                {
                    // If the message is sent unreliably, we can always just flush everything out
                    // to make space in the send queue. This is an expensive operation, but a user
                    // would need to send A LOT of unreliable traffic in one update to get here.

                    Driver.ScheduleFlushSend(default).Complete();
                    SendBatchedMessages(sendTarget, queue);

                    // Don't check for failure. If it still doesn't work, there's nothing we can do
                    // at this point and the message is lost (it was sent unreliable anyway).
                    queue.PushMessage(payload);
                }
            }
        }

        /// <summary>
        /// Send all queued messages
        /// </summary>
        private void SendBatchedMessages(SendTarget sendTarget, BatchedSendQueue queue)
        {
            new SendBatchedMessagesJob
            {
                Driver = Driver.ToConcurrent(),
                Target = sendTarget,
                Queue = queue,
                ReliablePipeline = _reliableSequencedPipeline
            }.Run();
        }

        [BurstCompile]
        private struct SendBatchedMessagesJob : IJob
        {
            public NetworkDriver.Concurrent Driver;
            public SendTarget Target;
            public BatchedSendQueue Queue;
            public NetworkPipeline ReliablePipeline;

            public void Execute()
            {
                NetworkConnection connection = Target.Connection;
                NetworkPipeline pipeline = Target.NetworkPipeline;

                while (!Queue.IsEmpty)
                {
                    int result = Driver.BeginSend(pipeline, connection, out DataStreamWriter writer);

                    if ((StatusCode)result != StatusCode.Success)
                    {
                        Debug.LogError($"Error sending the message to client {ParseTransportId(connection)} with code: {result}");
                        return;
                    }

                    // We don't attempt to send entire payloads over the reliable pipeline. Instead we
                    // fragment it manually. This is safe and easy to do since the reliable pipeline
                    // basically implements a stream, so as long as we separate the different messages
                    // in the stream (the send queue does that automatically) we are sure they'll be
                    // reassembled properly at the other end. This allows us to lift the limit of ~44KB
                    // on reliable payloads (because of the reliable window size).
                    int written = pipeline == ReliablePipeline ? Queue.FillWriterWithBytes(ref writer) : Queue.FillWriterWithMessages(ref writer);

                    result = Driver.EndSend(writer);
                    if (result == written)
                    {
                        // Batched message was sent successfully. Remove it from the queue.
                        Queue.Consume(written);
                    }
                    else
                    {
                        // Some error occured. If it's just the UTP queue being full, then don't log
                        // anything since that's okay (the unsent message(s) are still in the queue
                        // and we'll retry sending them later). Otherwise log the error and remove the
                        // message from the queue (we don't want to resend it again since we'll likely
                        // just get the same error again).
                        if ((StatusCode)result != StatusCode.NetworkSendQueueFull)
                        {
                            Debug.LogError($"Error sending the message to client {ParseTransportId(connection)} with code: {(StatusCode)result}");
                            Queue.Consume(written);
                        }

                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Returns a message from the transport
        /// </summary>
        private void ReceiveMessages(NetworkConnection connection, NetworkPipeline pipeline, DataStreamReader dataReader)
        {
            BatchedReceiveQueue queue;
            if (pipeline == _reliableSequencedPipeline)
            {
                if (ReliableReceiveQueues.TryGetValue(connection, out queue))
                {
                    queue.PushReader(dataReader);
                }
                else
                {
                    queue = new BatchedReceiveQueue(dataReader);
                    ReliableReceiveQueues[connection] = queue;
                }
            }
            else
            {
                queue = new BatchedReceiveQueue(dataReader);
            }

            while (!queue.IsEmpty)
            {
                var message = queue.PopMessage();
                if (message == default)
                {
                    // Only happens if there's only a partial message in the queue (rare).
                    break;
                }

                Channel channel = pipeline == _reliableSequencedPipeline ? Channel.Reliable : Channel.Unreliable;

                HandleReceivedData(message, channel, connection);
            }
        }

        protected void ClearSendQueuesForClientId(NetworkConnection connection)
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

        protected void FlushSendQueuesForClientId(NetworkConnection connection)
        {
            foreach (var kvp in _sendQueue)
            {
                if (kvp.Key.Connection == connection)
                {
                    SendBatchedMessages(kvp.Key, kvp.Value);
                }
            }
        }

        private NetworkPipeline SelectSendPipeline(Channel channel)
        {
            return channel switch
            {
                Channel.Unreliable => _unreliableFragmentedPipeline,
                Channel.Reliable => _reliableSequencedPipeline,
                _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
            };
        }

        #region DebugSimulator

#if UTP_TRANSPORT_2_0_ABOVE 
        [Obsolete("DebugSimulator is no longer supported and has no effect. Use Network Simulator from the Multiplayer Tools package.", false)]
#endif
        private SimulatorParameters DebugSimulator => Transport.DebugSimulator;

#if UTP_TRANSPORT_2_0_ABOVE
        private void ConfigureSimulatorForUtp2()
        {
            // As DebugSimulator is deprecated, the 'packetDelayMs', 'packetJitterMs' and 'packetDropPercentage'
            // parameters are set to the default and are supposed to be changed using Network Simulator tool instead.
            NetworkSettings.WithSimulatorStageParameters(
                maxPacketCount: 300, // TODO Is there any way to compute a better value?
                maxPacketSize: NetworkParameterConstants.MTU,
                packetDelayMs: 0,
                packetJitterMs: 0,
                packetDropPercentage: 0,
                randomSeed: Transport.DebugSimulatorRandomSeed ??= (uint)System.Diagnostics.Stopwatch.GetTimestamp()
                , mode: ApplyMode.AllPackets
            );

            NetworkSettings.WithNetworkSimulatorParameters();
        }
#else
        private void ConfigureSimulatorForUtp1()
        {
            NetworkSettings.WithSimulatorStageParameters(
                maxPacketCount: 300, // TODO Is there any way to compute a better value?
                maxPacketSize: NetworkParameterConstants.MTU,
                packetDelayMs: DebugSimulator.PacketDelayMS,
                packetJitterMs: DebugSimulator.PacketJitterMS,
                packetDropPercentage: DebugSimulator.PacketDropRate,
                randomSeed: Transport.DebugSimulatorRandomSeed ??= (uint)System.Diagnostics.Stopwatch.GetTimestamp()
            );
        }
#endif

        #endregion

        #region CreateDriver
        protected abstract void SetupSecureParameters();

        /// <summary>
        /// Creates the internal NetworkDriver
        /// </summary>
        /// <param name="transport"></param>
        /// <param name="driver">The driver</param>
        /// <param name="unreliableFragmentedPipeline">The UnreliableFragmented NetworkPipeline</param>
        /// <param name="unreliableSequencedFragmentedPipeline">The UnreliableSequencedFragmented NetworkPipeline</param>
        /// <param name="reliableSequencedPipeline">The ReliableSequenced NetworkPipeline</param>
        /// <param name="asServer"></param>
        private void CreateDriver(FishyUnityTransport transport, out NetworkDriver driver,
            out NetworkPipeline unreliableFragmentedPipeline,
            out NetworkPipeline unreliableSequencedFragmentedPipeline,
            out NetworkPipeline reliableSequencedPipeline, bool asServer)
        {
#if UTP_TRANSPORT_2_0_ABOVE && UNITY_MP_TOOLS_NETSIM_IMPLEMENTATION_ENABLED
            ConfigureSimulatorForUtp2();
#elif !UTP_TRANSPORT_2_0_ABOVE && (UNITY_EDITOR || DEVELOPMENT_BUILD)
            ConfigureSimulatorForUtp1();
#endif
            NetworkSettings.WithNetworkConfigParameters(
                maxConnectAttempts: transport.MaxConnectAttempts,
                connectTimeoutMS: transport.ConnectTimeoutMS,
                disconnectTimeoutMS: transport.DisconnectTimeoutMS,
#if UTP_TRANSPORT_2_0_ABOVE
                sendQueueCapacity: Transport.MaxPacketQueueSize,
                receiveQueueCapacity: Transport.MaxPacketQueueSize,
#endif
                heartbeatTimeoutMS: transport.HeartbeatTimeoutMS);

#if UNITY_WEBGL && !UNITY_EDITOR
            if (asServer)
            {
                throw new Exception("WebGL as a server is not supported by Unity Transport, outside the Editor.");
            }
#endif

#if UTP_TRANSPORT_2_0_ABOVE
            if (transport.UseEncryption)
            {
                if (transport.Protocol == ProtocolType.RelayUnityTransport)
                {
                    if (transport.RelayServerData.IsSecure == 0)
                    {
                        // log an error because we have mismatched configuration
                        Debug.LogError("Mismatched security configuration, between Relay and local NetworkManager settings");
                    }

                    // No need to to anything else if using Relay because UTP will handle the
                    // configuration of the security parameters on its own.
                }
                else
                {
                    SetupSecureParameters();
                }
            }
#endif

#if UTP_TRANSPORT_2_0_ABOVE
            if (transport.UseWebSockets)
            {
                driver = NetworkDriver.Create(new WebSocketNetworkInterface(), NetworkSettings);
            }
            else
            {
#if UNITY_WEBGL
                Debug.LogWarning($"WebSockets were used even though they're not selected in NetworkManager. You should check {nameof(UseWebSockets)}', on the Unity Transport component, to silence this warning.");
                driver = NetworkDriver.Create(new WebSocketNetworkInterface(), m_NetworkSettings);
#else
                driver = NetworkDriver.Create(new UDPNetworkInterface(), NetworkSettings);
#endif
            }
#else
            driver = NetworkDriver.Create(NetworkSettings);
#endif

#if !UTP_TRANSPORT_2_0_ABOVE
            SetupPipelinesForUtp1(driver,
                out unreliableFragmentedPipeline,
                out unreliableSequencedFragmentedPipeline,
                out reliableSequencedPipeline);
#else
            SetupPipelinesForUtp2(driver,
                out unreliableFragmentedPipeline,
                out unreliableSequencedFragmentedPipeline,
                out reliableSequencedPipeline);
#endif
        }

#if !UTP_TRANSPORT_2_0_ABOVE
        private void SetupPipelinesForUtp1(NetworkDriver driver,
            out NetworkPipeline unreliableFragmentedPipeline,
            out NetworkPipeline unreliableSequencedFragmentedPipeline,
            out NetworkPipeline reliableSequencedPipeline)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (DebugSimulator.PacketDelayMS > 0 || DebugSimulator.PacketDropRate > 0)
            {
                unreliableFragmentedPipeline = driver.CreatePipeline(
                    typeof(FragmentationPipelineStage),
                    typeof(SimulatorPipelineStage),
                    typeof(SimulatorPipelineStageInSend)
                );
                unreliableSequencedFragmentedPipeline = driver.CreatePipeline(
                    typeof(FragmentationPipelineStage),
                    typeof(UnreliableSequencedPipelineStage),
                    typeof(SimulatorPipelineStage),
                    typeof(SimulatorPipelineStageInSend)
                );
                reliableSequencedPipeline = driver.CreatePipeline(
                    typeof(ReliableSequencedPipelineStage),
                    typeof(SimulatorPipelineStage),
                    typeof(SimulatorPipelineStageInSend)
                );
            }
            else
#endif
            {
                unreliableFragmentedPipeline = driver.CreatePipeline(
                    typeof(FragmentationPipelineStage)
                );
                unreliableSequencedFragmentedPipeline = driver.CreatePipeline(
                    typeof(FragmentationPipelineStage),
                    typeof(UnreliableSequencedPipelineStage)
                );
                reliableSequencedPipeline = driver.CreatePipeline(
                    typeof(ReliableSequencedPipelineStage)
                );
            }
        }
#else
        private void SetupPipelinesForUtp2(NetworkDriver driver,
            out NetworkPipeline unreliableFragmentedPipeline,
            out NetworkPipeline unreliableSequencedFragmentedPipeline,
            out NetworkPipeline reliableSequencedPipeline)
        {

            unreliableFragmentedPipeline = driver.CreatePipeline(
                typeof(FragmentationPipelineStage)
#if UNITY_MP_TOOLS_NETSIM_IMPLEMENTATION_ENABLED
                , typeof(SimulatorPipelineStage)
#endif
            );

            unreliableSequencedFragmentedPipeline = driver.CreatePipeline(
                typeof(FragmentationPipelineStage),
                typeof(UnreliableSequencedPipelineStage)
#if UNITY_MP_TOOLS_NETSIM_IMPLEMENTATION_ENABLED
                , typeof(SimulatorPipelineStage)
#endif
            );

            reliableSequencedPipeline = driver.CreatePipeline(
                typeof(ReliableSequencedPipelineStage)
#if UNITY_MP_TOOLS_NETSIM_IMPLEMENTATION_ENABLED
                , typeof(SimulatorPipelineStage)
#endif
            );
        }
#endif

        #endregion
    }
}