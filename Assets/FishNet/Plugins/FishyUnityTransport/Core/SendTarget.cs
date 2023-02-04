using System;
using Unity.Networking.Transport;

namespace FishyUnityTransport
{
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
            return Connection.Equals(other.Connection) && NetworkPipeline.Equals(other.NetworkPipeline);
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