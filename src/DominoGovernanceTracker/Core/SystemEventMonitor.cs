using System;
using Microsoft.Win32;
using Serilog;

namespace DominoGovernanceTracker.Core
{
    /// <summary>
    /// Monitors system power events (sleep/wake) for recovery purposes
    /// Helps add-in recover from system sleep/hibernate cycles
    /// Thread-safe: uses Interlocked operations for all shared state
    /// </summary>
    public class SystemEventMonitor : IDisposable
    {
        private int _isMonitoring; // 0 = false, 1 = true (thread-safe with Interlocked)
        private long _lastSuspendTimeTicks; // DateTime.Ticks for thread-safe reads/writes
        private long _lastResumeTimeTicks; // DateTime.Ticks for thread-safe reads/writes
        private long _suspendCount;
        private long _resumeCount;

        public event EventHandler SystemSuspending;
        public event EventHandler SystemResumed;

        public SystemEventMonitor()
        {
            System.Threading.Interlocked.Exchange(ref _lastSuspendTimeTicks, DateTime.MinValue.Ticks);
            System.Threading.Interlocked.Exchange(ref _lastResumeTimeTicks, DateTime.MinValue.Ticks);

            Log.Information("System Event Monitor initialized");
        }

        /// <summary>
        /// Starts monitoring system events
        /// </summary>
        public void Start()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _isMonitoring, 1, 0) != 0)
                return; // Already monitoring

            try
            {
                // Subscribe to system power mode changes
                SystemEvents.PowerModeChanged += OnPowerModeChanged;

                Log.Information("System Event Monitor started");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start System Event Monitor");
                System.Threading.Interlocked.Exchange(ref _isMonitoring, 0); // Reset on failure
            }
        }

        /// <summary>
        /// Stops monitoring system events
        /// </summary>
        public void Stop()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _isMonitoring, 0, 1) != 1)
                return; // Already stopped

            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;

                Log.Information("System Event Monitor stopped (suspends: {Suspends}, resumes: {Resumes})",
                    System.Threading.Interlocked.Read(ref _suspendCount),
                    System.Threading.Interlocked.Read(ref _resumeCount));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping System Event Monitor");
            }
        }

        /// <summary>
        /// Handles power mode changes
        /// </summary>
        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                switch (e.Mode)
                {
                    case PowerModes.Suspend:
                        HandleSuspend();
                        break;

                    case PowerModes.Resume:
                        HandleResume();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling power mode change");
            }
        }

        /// <summary>
        /// Handles system suspend (sleep/hibernate)
        /// </summary>
        private void HandleSuspend()
        {
            System.Threading.Interlocked.Exchange(ref _lastSuspendTimeTicks, DateTime.UtcNow.Ticks);
            var count = System.Threading.Interlocked.Increment(ref _suspendCount);

            Log.Warning("System is suspending (going to sleep) - count: {Count}", count);

            // Notify listeners
            OnSystemSuspending();
        }

        /// <summary>
        /// Handles system resume (wake from sleep)
        /// </summary>
        private void HandleResume()
        {
            System.Threading.Interlocked.Exchange(ref _lastResumeTimeTicks, DateTime.UtcNow.Ticks);
            var count = System.Threading.Interlocked.Increment(ref _resumeCount);

            // Calculate suspend duration (thread-safe read)
            var resumeTicks = System.Threading.Interlocked.Read(ref _lastResumeTimeTicks);
            var suspendTicks = System.Threading.Interlocked.Read(ref _lastSuspendTimeTicks);
            var suspendDuration = new DateTime(resumeTicks, DateTimeKind.Utc) - new DateTime(suspendTicks, DateTimeKind.Utc);

            Log.Warning("System resumed from suspend (woke up) - count: {Count}, suspended for: {Duration}",
                count, suspendDuration);

            // Notify listeners
            OnSystemResumed();
        }

        /// <summary>
        /// Raises SystemSuspending event
        /// </summary>
        protected virtual void OnSystemSuspending()
        {
            try
            {
                SystemSuspending?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in SystemSuspending event handler");
            }
        }

        /// <summary>
        /// Raises SystemResumed event
        /// </summary>
        protected virtual void OnSystemResumed()
        {
            try
            {
                SystemResumed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in SystemResumed event handler");
            }
        }

        /// <summary>
        /// Gets whether monitoring is active
        /// </summary>
        public bool IsMonitoring => System.Threading.Interlocked.CompareExchange(ref _isMonitoring, 0, 0) == 1;

        /// <summary>
        /// Gets last suspend time
        /// </summary>
        public DateTime LastSuspendTime
        {
            get
            {
                var ticks = System.Threading.Interlocked.Read(ref _lastSuspendTimeTicks);
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Gets last resume time
        /// </summary>
        public DateTime LastResumeTime
        {
            get
            {
                var ticks = System.Threading.Interlocked.Read(ref _lastResumeTimeTicks);
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Gets total suspend count
        /// </summary>
        public long SuspendCount => System.Threading.Interlocked.Read(ref _suspendCount);

        /// <summary>
        /// Gets total resume count
        /// </summary>
        public long ResumeCount => System.Threading.Interlocked.Read(ref _resumeCount);

        public void Dispose()
        {
            Stop();
        }
    }
}
