using System;
using UnityEngine;

namespace FishNet.Transporting.FishyUnityTransport
{
    /// <summary>
    /// Parameters for the Network Simulator
    /// </summary>
    [Serializable]
    public struct SimulatorParameters
    {
        /// <summary>
        /// Delay to add to every send and received packet (in milliseconds). Only applies in the editor and in development builds. The value is ignored in production builds.
        /// </summary>
        [Tooltip("Delay to add to every send and received packet (in milliseconds). Only applies in the editor and in development builds. The value is ignored in production builds.")]
        [SerializeField]
        public int PacketDelayMS;

        /// <summary>
        /// Jitter (random variation) to add/substract to the packet delay (in milliseconds). Only applies in the editor and in development builds. The value is ignored in production builds.
        /// </summary>
        [Tooltip("Jitter (random variation) to add/substract to the packet delay (in milliseconds). Only applies in the editor and in development builds. The value is ignored in production builds.")]
        [SerializeField]
        public int PacketJitterMS;

        /// <summary>
        /// Percentage of sent and received packets to drop. Only applies in the editor and in the editor and in developments builds.
        /// </summary>
        [Tooltip("Percentage of sent and received packets to drop. Only applies in the editor and in the editor and in developments builds.")]
        [SerializeField]
        public int PacketDropRate;
    }
}