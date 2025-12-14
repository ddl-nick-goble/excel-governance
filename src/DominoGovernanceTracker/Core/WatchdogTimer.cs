using System;
using System.Threading;
using Serilog;

namespace DominoGovernanceTracker.Core
{
    /// <summary>
    /// Self-healing timer that detects and recovers from timer failures
    /// Useful for recovering from sleep/wake cycles and other disruptions
    /// </summary>
    public class WatchdogTimer : IDisposable
    {
        private readonly TimerCallback _callback;
        private readonly TimeSpan _interval;
        private readonly string _name;
        private Timer _mainTimer;
        private Timer _watchdogTimer;
        private long _lastTickUtc; // DateTime.Ticks for thread-safe reads/writes
        private long _tickCount;
        private long _recoveryCount;
        private int _isRunning; // 0 = false, 1 = true (thread-safe with Interlocked)
        private readonly object _lock = new object();

        public WatchdogTimer(string name, TimerCallback callback, TimeSpan interval)
        {
            _name = name ?? "Unknown";
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            _interval = interval;
            Interlocked.Exchange(ref _lastTickUtc, DateTime.UtcNow.Ticks);

            Log.Debug("WatchdogTimer '{Name}' created (interval: {Interval}ms)", _name, interval.TotalMilliseconds);
        }

        /// <summary>
        /// Starts the watchdog timer
        /// </summary>
        public void Start()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
                return; // Already running

            lock (_lock)
            {
                Interlocked.Exchange(ref _lastTickUtc, DateTime.UtcNow.Ticks);

                // Start main timer
                _mainTimer = new Timer(OnMainTimerTick, null, _interval, _interval);

                // Start watchdog (checks twice as often as main timer)
                var watchdogInterval = TimeSpan.FromMilliseconds(_interval.TotalMilliseconds / 2);
                _watchdogTimer = new Timer(OnWatchdogTick, null, watchdogInterval, watchdogInterval);

                Log.Information("WatchdogTimer '{Name}' started", _name);
            }
        }

        /// <summary>
        /// Stops the watchdog timer
        /// </summary>
        public void Stop()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 0, 1) != 1)
                return; // Already stopped

            lock (_lock)
            {
                _mainTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _mainTimer?.Dispose();
                _mainTimer = null;

                _watchdogTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _watchdogTimer?.Dispose();
                _watchdogTimer = null;

                Log.Information("WatchdogTimer '{Name}' stopped (ticks: {Ticks}, recoveries: {Recoveries})",
                    _name, _tickCount, _recoveryCount);
            }
        }

        /// <summary>
        /// Main timer tick - executes the user callback
        /// </summary>
        private void OnMainTimerTick(object state)
        {
            try
            {
                // Update last tick time (thread-safe)
                Interlocked.Exchange(ref _lastTickUtc, DateTime.UtcNow.Ticks);
                Interlocked.Increment(ref _tickCount);

                // Execute user callback
                _callback(state);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WatchdogTimer '{Name}' callback error", _name);
            }
        }

        /// <summary>
        /// Watchdog tick - checks if main timer is still working
        /// </summary>
        private void OnWatchdogTick(object state)
        {
            try
            {
                if (Interlocked.CompareExchange(ref _isRunning, 0, 0) == 0)
                    return;

                // Read last tick time (thread-safe)
                var lastTickTicks = Interlocked.Read(ref _lastTickUtc);
                var lastTick = new DateTime(lastTickTicks, DateTimeKind.Utc);
                var timeSinceLastTick = DateTime.UtcNow - lastTick;

                // If main timer hasn't ticked in 2x the interval, it's probably stuck
                var maxAllowedDelay = TimeSpan.FromMilliseconds(_interval.TotalMilliseconds * 2.5);

                if (timeSinceLastTick > maxAllowedDelay)
                {
                    Log.Warning(
                        "WatchdogTimer '{Name}' detected timer failure (last tick: {TimeSinceLastTick}ms ago, expected: {ExpectedInterval}ms) - recovering",
                        _name, timeSinceLastTick.TotalMilliseconds, _interval.TotalMilliseconds);

                    RecoverTimer();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WatchdogTimer '{Name}' watchdog error", _name);
            }
        }

        /// <summary>
        /// Recovers the main timer by recreating it
        /// </summary>
        private void RecoverTimer()
        {
            lock (_lock)
            {
                try
                {
                    // Dispose old timer
                    _mainTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    _mainTimer?.Dispose();

                    // Create new timer
                    _mainTimer = new Timer(OnMainTimerTick, null, TimeSpan.Zero, _interval);

                    Interlocked.Increment(ref _recoveryCount);
                    Interlocked.Exchange(ref _lastTickUtc, DateTime.UtcNow.Ticks);

                    Log.Information("WatchdogTimer '{Name}' recovered (recovery count: {Count})",
                        _name, _recoveryCount);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to recover WatchdogTimer '{Name}'", _name);
                }
            }
        }

        /// <summary>
        /// Manually triggers recovery (useful for sleep/wake events)
        /// </summary>
        public void TriggerRecovery()
        {
            Log.Information("WatchdogTimer '{Name}' recovery triggered manually", _name);
            RecoverTimer();
        }

        /// <summary>
        /// Gets total ticks
        /// </summary>
        public long TickCount => Interlocked.Read(ref _tickCount);

        /// <summary>
        /// Gets recovery count
        /// </summary>
        public long RecoveryCount => Interlocked.Read(ref _recoveryCount);

        /// <summary>
        /// Gets time since last tick
        /// </summary>
        public TimeSpan TimeSinceLastTick
        {
            get
            {
                var lastTickTicks = Interlocked.Read(ref _lastTickUtc);
                var lastTick = new DateTime(lastTickTicks, DateTimeKind.Utc);
                return DateTime.UtcNow - lastTick;
            }
        }

        /// <summary>
        /// Gets whether timer is running (thread-safe)
        /// </summary>
        public bool IsRunning => Interlocked.CompareExchange(ref _isRunning, 0, 0) == 1;

        public void Dispose()
        {
            Stop();
        }
    }
}
