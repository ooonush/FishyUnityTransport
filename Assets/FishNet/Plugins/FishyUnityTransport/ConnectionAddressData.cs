using System;
using Unity.Networking.Transport;
using UnityEngine;
#if UTP_TRANSPORT_2_0_ABOVE
using Unity.Networking.Transport.TLS;
#endif

#if !UTP_TRANSPORT_2_0_ABOVE
using NetworkEndpoint = Unity.Networking.Transport.NetworkEndPoint;
#endif

/// <summary>
/// Structure to store the address to connect to
/// </summary>
[Serializable]
public struct ConnectionAddressData
{
    /// <summary>
    /// IP address of the server (address to which clients will connect to).
    /// </summary>
    [Tooltip("IP address of the server (address to which clients will connect to).")] [SerializeField]
    public string Address;

    /// <summary>
    /// UDP port of the server.
    /// </summary>
    [Tooltip("UDP port of the server.")] [SerializeField]
    public ushort Port;

    /// <summary>
    /// IP address the server will listen on. If not provided, will use 'Address'.
    /// </summary>
    [Tooltip("IP address the server will listen on. If not provided, will use 'Address'.")] [SerializeField]
    public string ServerListenAddress;

    private static NetworkEndpoint ParseNetworkEndpoint(string ip, ushort port)
    {
        NetworkEndpoint endpoint = default;

        if (!NetworkEndpoint.TryParse(ip, port, out endpoint, NetworkFamily.Ipv4) &&
            !NetworkEndpoint.TryParse(ip, port, out endpoint, NetworkFamily.Ipv6))
        {
            Debug.LogError($"Invalid network endpoint: {ip}:{port}.");
        }

        return endpoint;
    }

    /// <summary>
    /// Endpoint (IP address and port) clients will connect to.
    /// </summary>
    public NetworkEndpoint ServerEndPoint => ParseNetworkEndpoint(Address, Port);

    /// <summary>
    /// Endpoint (IP address and port) server will listen/bind on.
    /// </summary>
    public NetworkEndpoint ListenEndPoint =>
        ParseNetworkEndpoint((ServerListenAddress?.Length == 0) ? Address : ServerListenAddress, Port);
}