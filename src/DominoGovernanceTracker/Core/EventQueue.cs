using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using DominoGovernanceTracker.Models;
using Serilog;

namespace DominoGovernanceTracker.Core
{
    /// <summary>
    /// Thread-safe in-memory queue for audit events
    /// Provides buffering before events are sent to the API
    /// </summary>
    public class EventQueue : IDisposable
    {
        private readonly ConcurrentQueue<AuditEvent> _queue;
        private readonly int _maxSize;
        private long _totalEventsEnqueued;
        private long _totalEventsDequeued;
        private long _totalEventsOverflowed;

        // Callback for handling queue overflow (instead of dropping events)
        private Action<AuditEvent> _overflowHandler;

        public EventQueue(int maxSize = 1000)
        {
            _queue = new ConcurrentQueue<AuditEvent>();
            _maxSize = maxSize;
        }

        /// <summary>
        /// Sets a handler to be called when queue overflows
        /// Instead of dropping events, they are passed to this handler
        /// </summary>
        public void SetOverflowHandler(Action<AuditEvent> handler)
        {
            _overflowHandler = handler;
        }

        /// <summary>
        /// Adds an event to the queue
        /// </summary>
        public bool Enqueue(AuditEvent evt)
        {
            if (evt == null)
                return false;

            // Enqueue first, then handle overflow if needed (loop ensures bounded size)
            _queue.Enqueue(evt);
            Interlocked.Increment(ref _totalEventsEnqueued);

            // Handle overflow - loop to ensure we stay within bounds
            // (multiple threads could enqueue simultaneously)
            while (_queue.Count > _maxSize)
            {
                // Try to dequeue oldest event
                if (_queue.TryDequeue(out var droppedEvent))
                {
                    // If overflow handler is set, use it instead of dropping
                    if (_overflowHandler != null)
                    {
                        Interlocked.Increment(ref _totalEventsOverflowed);
                        Log.Warning("Event queue exceeded max size ({MaxSize}), sending oldest event to overflow handler", _maxSize);

                        try
                        {
                            _overflowHandler(droppedEvent);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Overflow handler failed - event will be lost");
                        }
                    }
                    else
                    {
                        // No overflow handler - event is dropped
                        Log.Warning("Event queue exceeded max size ({MaxSize}), dropping oldest event (no overflow handler)", _maxSize);
                    }

                    // Note: Don't decrement _totalEventsEnqueued - it's a lifetime counter
                    // The dropped event was enqueued, so the counter stays
                    Interlocked.Increment(ref _totalEventsDequeued);
                }
                else
                {
                    // Another thread already dequeued, exit loop
                    break;
                }
            }

            Log.Debug("Event enqueued: {EventType} (Queue size: {Size})", evt.EventType, _queue.Count);
            return true;
        }

        /// <summary>
        /// Attempts to dequeue a single event
        /// </summary>
        public bool TryDequeue(out AuditEvent evt)
        {
            var result = _queue.TryDequeue(out evt);
            if (result)
            {
                Interlocked.Increment(ref _totalEventsDequeued);
            }
            return result;
        }

        /// <summary>
        /// Dequeues up to maxCount events as a batch
        /// </summary>
        public List<AuditEvent> DequeueBatch(int maxCount)
        {
            var batch = new List<AuditEvent>(maxCount);

            for (int i = 0; i < maxCount; i++)
            {
                if (_queue.TryDequeue(out var evt))
                {
                    batch.Add(evt);
                    Interlocked.Increment(ref _totalEventsDequeued);
                }
                else
                {
                    break;  // Queue is empty
                }
            }

            if (batch.Count > 0)
            {
                Log.Debug("Dequeued batch of {Count} events", batch.Count);
            }

            return batch;
        }

        /// <summary>
        /// Peeks at the next event without removing it
        /// </summary>
        public bool TryPeek(out AuditEvent evt)
        {
            return _queue.TryPeek(out evt);
        }

        /// <summary>
        /// Gets the current queue count
        /// </summary>
        public int Count => _queue.Count;

        /// <summary>
        /// Checks if the queue is empty
        /// </summary>
        public bool IsEmpty => _queue.IsEmpty;

        /// <summary>
        /// Gets total events enqueued since creation
        /// </summary>
        public long TotalEnqueued => Interlocked.Read(ref _totalEventsEnqueued);

        /// <summary>
        /// Gets total events dequeued since creation
        /// </summary>
        public long TotalDequeued => Interlocked.Read(ref _totalEventsDequeued);

        /// <summary>
        /// Gets total events that overflowed to handler
        /// </summary>
        public long TotalOverflowed => Interlocked.Read(ref _totalEventsOverflowed);

        /// <summary>
        /// Clears all events from the queue
        /// </summary>
        public void Clear()
        {
            while (_queue.TryDequeue(out _)) { }
            Log.Information("Event queue cleared");
        }

        /// <summary>
        /// Gets queue statistics
        /// </summary>
        public QueueStats GetStats()
        {
            return new QueueStats
            {
                CurrentSize = _queue.Count,
                MaxSize = _maxSize,
                TotalEnqueued = TotalEnqueued,
                TotalDequeued = TotalDequeued,
                UtilizationPercent = (_queue.Count / (double)_maxSize) * 100
            };
        }

        public void Dispose()
        {
            Clear();
        }
    }

    /// <summary>
    /// Queue statistics
    /// </summary>
    public class QueueStats
    {
        public int CurrentSize { get; set; }
        public int MaxSize { get; set; }
        public long TotalEnqueued { get; set; }
        public long TotalDequeued { get; set; }
        public double UtilizationPercent { get; set; }
    }
}
