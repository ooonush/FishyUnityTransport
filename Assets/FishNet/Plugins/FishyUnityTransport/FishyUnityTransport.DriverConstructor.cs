using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace FishNet.Transporting.UTP
{
    /// <summary>
    /// Provides an interface that overrides the ability to create your own drivers and pipelines
    /// </summary>
    public interface INetworkStreamDriverConstructor
    {
        /// <summary>
        /// Creates the internal NetworkDriver
        /// </summary>
        /// <param name="transport">The owner transport</param>
        /// <param name="driver">The driver</param>
        /// <param name="unreliableFragmentedPipeline">The UnreliableFragmented NetworkPipeline</param>
        /// <param name="unreliableSequencedFragmentedPipeline">The UnreliableSequencedFragmented NetworkPipeline</param>
        /// <param name="reliableSequencedPipeline">The ReliableSequenced NetworkPipeline</param>
        void CreateDriver(
            FishyUnityTransport transport,
            out NetworkDriver driver,
            out NetworkPipeline unreliableFragmentedPipeline,
            out NetworkPipeline unreliableSequencedFragmentedPipeline,
            out NetworkPipeline reliableSequencedPipeline);
    }

    public partial class FishyUnityTransport
    {
#if UTP_TRANSPORT_2_0_ABOVE
        private void ConfigureSimulatorForUtp2()
        {
            // As DebugSimulator is deprecated, the 'packetDelayMs', 'packetJitterMs' and 'packetDropPercentage'
            // parameters are set to the default and are supposed to be changed using Network Simulator tool instead.
            m_NetworkSettings.WithSimulatorStageParameters(
                maxPacketCount: 300, // TODO Is there any way to compute a better value?
                maxPacketSize: NetworkParameterConstants.MTU,
                packetDelayMs: 0,
                packetJitterMs: 0,
                packetDropPercentage: 0,
                randomSeed: (uint)System.Diagnostics.Stopwatch.GetTimestamp()
                , mode: ApplyMode.AllPackets
            );

            m_NetworkSettings.WithNetworkSimulatorParameters();
        }
#else
        private void ConfigureSimulatorForUtp1()
        {
            m_NetworkSettings.WithSimulatorStageParameters(
                maxPacketCount: 300, // TODO Is there any way to compute a better value?
                maxPacketSize: NetworkParameterConstants.MTU,
                packetDelayMs: DebugSimulator.PacketDelayMS,
                packetJitterMs: DebugSimulator.PacketJitterMS,
                packetDropPercentage: DebugSimulator.PacketDropRate,
                randomSeed: (uint)System.Diagnostics.Stopwatch.GetTimestamp()
            );
        }
#endif

#if UTP_TRANSPORT_2_0_ABOVE
        private string m_ServerPrivateKey;
        private string m_ServerCertificate;

        private string m_ServerCommonName;
        private string m_ClientCaCertificate;

        /// <summary>Set the server parameters for encryption.</summary>
        /// <remarks>
        /// The public certificate and private key are expected to be in the PEM format, including
        /// the begin/end markers like <c>-----BEGIN CERTIFICATE-----</c>.
        /// </remarks>
        /// <param name="serverCertificate">Public certificate for the server (PEM format).</param>
        /// <param name="serverPrivateKey">Private key for the server (PEM format).</param>
        public void SetServerSecrets(string serverCertificate, string serverPrivateKey)
        {
            m_ServerPrivateKey = serverPrivateKey;
            m_ServerCertificate = serverCertificate;
        }

        /// <summary>Set the client parameters for encryption.</summary>
        /// <remarks>
        /// <para>
        /// If the CA certificate is not provided, validation will be done against the OS/browser
        /// certificate store. This is what you'd want if using certificates from a known provider.
        /// For self-signed certificates, the CA certificate needs to be provided.
        /// </para>
        /// <para>
        /// The CA certificate (if provided) is expected to be in the PEM format, including the
        /// begin/end markers like <c>-----BEGIN CERTIFICATE-----</c>.
        /// </para>
        /// </remarks>
        /// <param name="serverCommonName">Common name of the server (typically hostname).</param>
        /// <param name="caCertificate">CA certificate used to validate the server's authenticity.</param>
        public void SetClientSecrets(string serverCommonName, string caCertificate = null)
        {
            m_ServerCommonName = serverCommonName;
            m_ClientCaCertificate = caCertificate;
        }
#endif

        /// <summary>
        /// Creates the internal NetworkDriver
        /// </summary>
        /// <param name="transport">The owner transport</param>
        /// <param name="driver">The driver</param>
        /// <param name="unreliableFragmentedPipeline">The UnreliableFragmented NetworkPipeline</param>
        /// <param name="unreliableSequencedFragmentedPipeline">The UnreliableSequencedFragmented NetworkPipeline</param>
        /// <param name="reliableSequencedPipeline">The ReliableSequenced NetworkPipeline</param>
        public void CreateDriver(FishyUnityTransport transport, out NetworkDriver driver,
            out NetworkPipeline unreliableFragmentedPipeline,
            out NetworkPipeline unreliableSequencedFragmentedPipeline,
            out NetworkPipeline reliableSequencedPipeline)
        {
#if !UTP_TRANSPORT_2_0_ABOVE && (UNITY_EDITOR || DEVELOPMENT_BUILD)
            ConfigureSimulatorForUtp1();
#endif

            m_NetworkSettings.WithNetworkConfigParameters(
                maxConnectAttempts: transport.m_MaxConnectAttempts,
                connectTimeoutMS: transport.m_ConnectTimeoutMS,
                disconnectTimeoutMS: transport.m_DisconnectTimeoutMS,
#if UTP_TRANSPORT_2_0_ABOVE
                sendQueueCapacity: m_MaxPacketQueueSize,
                receiveQueueCapacity: m_MaxPacketQueueSize,
#endif
                heartbeatTimeoutMS: transport.m_HeartbeatTimeoutMS);

#if UNITY_WEBGL && !UNITY_EDITOR
            if (NetworkManager.IsServer && m_ProtocolType != ProtocolType.RelayUnityTransport)
            {
                throw new Exception("WebGL as a server is not supported by Unity Transport, outside the Editor.");
            }
#endif

#if UTP_TRANSPORT_2_0_ABOVE
            if (m_UseEncryption)
            {
                if (m_ProtocolType == ProtocolType.RelayUnityTransport)
                {
                    if (m_RelayServerData.IsSecure == 0)
                    {
                        // log an error because we have mismatched configuration
                        NetworkManager.LogError("Mismatched security configuration, between Relay and local NetworkManager settings");
                    }

                    // No need to to anything else if using Relay because UTP will handle the
                    // configuration of the security parameters on its own.
                }
                else
                {
                    if (NetworkManager.IsServer)
                    {
                        if (string.IsNullOrEmpty(m_ServerCertificate) || string.IsNullOrEmpty(m_ServerPrivateKey))
                        {
                            throw new Exception("In order to use encrypted communications, when hosting, you must set the server certificate and key.");
                        }

                        m_NetworkSettings.WithSecureServerParameters(m_ServerCertificate, m_ServerPrivateKey);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(m_ServerCommonName))
                        {
                            throw new Exception("In order to use encrypted communications, clients must set the server common name.");
                        }
                        else if (string.IsNullOrEmpty(m_ClientCaCertificate))
                        {
                            m_NetworkSettings.WithSecureClientParameters(m_ServerCommonName);
                        }
                        else
                        {
                            m_NetworkSettings.WithSecureClientParameters(m_ClientCaCertificate, m_ServerCommonName);
                        }
                    }
                }
            }
#endif

#if UTP_TRANSPORT_2_0_ABOVE
            if (m_UseWebSockets)
            {
                driver = NetworkDriver.Create(new WebSocketNetworkInterface(), m_NetworkSettings);
            }
            else
            {
#if UNITY_WEBGL
                Debug.LogWarning($"WebSockets were used even though they're not selected in NetworkManager. You should check {nameof(UseWebSockets)}', on the Unity Transport component, to silence this warning.");
                driver = NetworkDriver.Create(new WebSocketNetworkInterface(), m_NetworkSettings);
#else
                driver = NetworkDriver.Create(new UDPNetworkInterface(), m_NetworkSettings);
#endif
            }
#else
            driver = NetworkDriver.Create(m_NetworkSettings);
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
            );

            unreliableSequencedFragmentedPipeline = driver.CreatePipeline(
                typeof(FragmentationPipelineStage),
                typeof(UnreliableSequencedPipelineStage)
            );

            reliableSequencedPipeline = driver.CreatePipeline(
                typeof(ReliableSequencedPipelineStage)
            );
        }
#endif
    }
}