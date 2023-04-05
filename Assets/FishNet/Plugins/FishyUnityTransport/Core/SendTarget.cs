using System;
using Unity.Networking.Transport;

namespace FishNet.Transporting.FishyUnityTransport
{
    /// <summary>
    /// Cached information about reliability mode with a certain client
    /// </summary>
    public readonly struct SendTarget : IEquatable<SendTarget>
    {
        public readonly ulong ClientId;
        public readonly NetworkPipeline NetworkPipeline;

        public SendTarget(ulong clientId, NetworkPipeline networkPipeline)
        {
            ClientId = clientId;
            NetworkPipeline = networkPipeline;
        }

        public bool Equals(SendTarget other)
        {
            return ClientId == other.ClientId && NetworkPipeline.Equals(other.NetworkPipeline);
        }

        public override bool Equals(object obj)
        {
            return obj is SendTarget other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (ClientId.GetHashCode() * 397) ^ NetworkPipeline.GetHashCode();
            }
        }
    }
}