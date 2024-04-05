namespace FishNet.Transporting.UTP
{
    internal static class LocalConnectionStateExtensions
    {
        public static bool IsStartingOrStarted(this LocalConnectionState state)
        {
            return state == LocalConnectionState.Starting || state == LocalConnectionState.Started;
        }
        
        public static bool IsStoppingOrStopped(this LocalConnectionState state)
        {
            return state == LocalConnectionState.Stopping || state == LocalConnectionState.Stopped;
        }
    }
}