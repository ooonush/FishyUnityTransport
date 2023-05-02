using System;
using Unity.Networking.Transport;

namespace FishNet.Transporting.FishyUnityTransport
{
    /// <summary>
    /// Cached information about reliability mode with a certain client
    /// </summary>
    public readonly struct SendTarget : IEquatable<SendTarget>
    {
        public readonly NetworkConnection Connection;
        public readonly NetworkPipeline NetworkPipeline;

        public SendTarget(NetworkConnection connection, NetworkPipeline networkPipeline)
        {
            Connection = connection;
            NetworkPipeline = networkPipeline;
        }

        public bool Equals(SendTarget other)
        {
            return Connection == other.Connection && NetworkPipeline.Equals(other.NetworkPipeline);
        }

        public override bool Equals(object obj)
        {
            return obj is SendTarget other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Connection.GetHashCode() * 397) ^ NetworkPipeline.GetHashCode();
            }
        }
    }
}