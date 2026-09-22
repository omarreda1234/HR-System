using System.Collections.Concurrent;

namespace HRSystem.Services
{
    /// <summary>
    /// Ensures that only one connection (manual or background auto-sync) is made to a device at any time.
    /// </summary>
    public static class DeviceSyncLock
    {
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _deviceLocks = new();

        public static SemaphoreSlim GetLock(int deviceId)
        {
            return _deviceLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        }
    }
}
