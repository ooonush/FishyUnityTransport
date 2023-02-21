using System;
using Unity.Networking.Transport;
using UnityEngine;

namespace FishNet.Transporting.FishyUnityTransport
{
    /// <summary>
    /// Structure to store the address to connect to
    /// </summary>
    [Serializable]
    public struct ConnectionAddressData
    {
        /// <summary>
        /// IP address of the server (address to which clients will connect to).
        /// </summary>
        [Tooltip("IP address of the server (address to which clients will connect to).")]
        [SerializeField]
        public string Address;

        /// <summary>
        /// UDP port of the server.
        /// </summary>
        [Tooltip("UDP port of the server.")]
        [SerializeField]
        public ushort Port;

        /// <summary>
        /// IP address the server will listen on. If not provided, will use 'Address'.
        /// </summary>
        [Tooltip("IP address the server will listen on. If not provided, will use 'Address'.")]
        [SerializeField]
        public string ServerListenAddress;

        private static NetworkEndPoint ParseNetworkEndpoint(string ip, ushort port)
        {
            if (!NetworkEndPoint.TryParse(ip, port, out NetworkEndPoint endpoint, NetworkFamily.Ipv4) &&
                !NetworkEndPoint.TryParse(ip, port, out endpoint, NetworkFamily.Ipv6))
            {
                Debug.LogError($"Invalid network endpoint: {ip}:{port}.");
            }

            return endpoint;
        }

        /// <summary>
        /// Endpoint (IP address and port) clients will connect to.
        /// </summary>
        public NetworkEndPoint ServerEndPoint => ParseNetworkEndpoint(Address, Port);

        /// <summary>
        /// Endpoint (IP address and port) server will listen/bind on.
        /// </summary>
        public NetworkEndPoint ListenEndPoint => ParseNetworkEndpoint((ServerListenAddress?.Length == 0) ? Address : ServerListenAddress, Port);
    }
}