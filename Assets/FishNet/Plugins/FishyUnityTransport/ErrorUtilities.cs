using Unity.Collections;

namespace FishNet.Transporting.UTP
{
    /// <summary>
    /// Helper utility class to convert <see cref="Transport"/> error codes to human readable error messages.
    /// </summary>
    public static class ErrorUtilities
    {
        private static readonly FixedString128Bytes k_NetworkSuccess = "Success";
        private static readonly FixedString128Bytes k_NetworkIdMismatch = "Invalid connection ID {0}.";
        private static readonly FixedString128Bytes k_NetworkVersionMismatch = "Connection ID is invalid. Likely caused by sending on stale connection {0}.";
        private static readonly FixedString128Bytes k_NetworkStateMismatch = "Connection state is invalid. Likely caused by sending on connection {0} which is stale or still connecting.";
        private static readonly FixedString128Bytes k_NetworkPacketOverflow = "Packet is too large to be allocated by the transport.";
        private static readonly FixedString128Bytes k_NetworkSendQueueFull = "Unable to queue packet in the transport. Likely caused by send queue size ('Max Send Queue Size') being too small.";

        /// <summary>
        /// Convert a UTP error code to human-readable error message.
        /// </summary>
        /// <param name="error">UTP error code.</param>
        /// <param name="connectionId">ID of the connection on which the error occurred.</param>
        /// <returns>Human-readable error message.</returns>
        public static string ErrorToString(Unity.Networking.Transport.Error.StatusCode error, ulong connectionId)
        {
            return ErrorToString((int)error, connectionId);
        }

        internal static string ErrorToString(int error, ulong connectionId)
        {
            return ErrorToFixedString(error, connectionId).ToString();
        }

        internal static FixedString128Bytes ErrorToFixedString(int error, ulong connectionId)
        {
            switch ((Unity.Networking.Transport.Error.StatusCode)error)
            {
                case Unity.Networking.Transport.Error.StatusCode.Success:
                    return k_NetworkSuccess;
                case Unity.Networking.Transport.Error.StatusCode.NetworkIdMismatch:
                    return FixedString.Format(k_NetworkIdMismatch, connectionId);
                case Unity.Networking.Transport.Error.StatusCode.NetworkVersionMismatch:
                    return FixedString.Format(k_NetworkVersionMismatch, connectionId);
                case Unity.Networking.Transport.Error.StatusCode.NetworkStateMismatch:
                    return FixedString.Format(k_NetworkStateMismatch, connectionId);
                case Unity.Networking.Transport.Error.StatusCode.NetworkPacketOverflow:
                    return k_NetworkPacketOverflow;
                case Unity.Networking.Transport.Error.StatusCode.NetworkSendQueueFull:
                    return k_NetworkSendQueueFull;
                default:
                    return FixedString.Format("Unknown error code {0}.", error);
            }
        }
    }
}