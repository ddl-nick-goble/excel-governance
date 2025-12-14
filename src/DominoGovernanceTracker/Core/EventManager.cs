using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using DominoGovernanceTracker.Models;
using Serilog;
using MSExcel = Microsoft.Office.Interop.Excel;

namespace DominoGovernanceTracker.Core
{
    /// <summary>
    /// Manages Excel COM event subscriptions and captures audit events
    /// </summary>
    public class EventManager : IDisposable
    {
        private readonly MSExcel.Application _app;
        private readonly EventQueue _queue;
        private readonly DgtConfig _config;
        private readonly string _sessionId;
        private int _isTracking; // 0 = false, 1 = true (thread-safe with Interlocked - read from Ribbon UI thread)

        // LRU cache to track old values before changes (bounded to prevent memory leak)
        private readonly LruCache<string, object> _cellValueCache = new LruCache<string, object>(10000);

        // Event counter per workbook (thread-safe - CRITICAL for compliance tracking accuracy)
        private readonly ConcurrentDictionary<string, long> _workbookEventCounts = new ConcurrentDictionary<string, long>();
        private long _currentWorkbookEventCount; // Thread-safe with Interlocked (read from Ribbon UI thread)
        private string _currentWorkbookName;

        // Bulk operation threshold - operations affecting more cells are aggregated
        private const int BULK_OPERATION_THRESHOLD = 100;

        public EventManager(MSExcel.Application app, EventQueue queue, DgtConfig config)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _sessionId = Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Starts tracking Excel events
        /// </summary>
        public void StartTracking()
        {
            if (Interlocked.CompareExchange(ref _isTracking, 1, 0) != 0)
                return; // Already tracking

            if (!_config.TrackingEnabled)
            {
                Log.Information("Tracking is disabled in configuration");
                return;
            }

            try
            {
                // Workbook lifecycle events
                _app.WorkbookOpen += OnWorkbookOpen;
                _app.WorkbookBeforeClose += OnWorkbookBeforeClose;
                _app.WorkbookAfterSave += OnWorkbookAfterSave;
                _app.WorkbookActivate += OnWorkbookActivate;
                _app.WorkbookDeactivate += OnWorkbookDeactivate;
                ((MSExcel.AppEvents_Event)_app).NewWorkbook += OnNewWorkbook;

                // Sheet structural events (compliance tracking)
                _app.WorkbookNewSheet += OnWorkbookNewSheet;

                // Sheet events (application-level)
                _app.SheetChange += OnSheetChange;
                _app.SheetBeforeDoubleClick += OnSheetBeforeDoubleClick;
                _app.SheetActivate += OnSheetActivate;

                if (_config.TrackSelectionChanges)
                {
                    _app.SheetSelectionChange += OnSheetSelectionChange;
                }

                // _isTracking already set to 1 by CompareExchange above

                // Log session start event
                EnqueueEvent(new AuditEvent
                {
                    EventType = AuditEventType.SessionStart,
                    SessionId = _sessionId,
                    Details = $"DGT tracking started"
                });

                Log.Information("Event tracking started (Session: {SessionId})", _sessionId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start event tracking");
            }
        }

        /// <summary>
        /// Stops tracking Excel events
        /// </summary>
        public void StopTracking()
        {
            if (Interlocked.CompareExchange(ref _isTracking, 0, 1) != 1)
                return; // Already stopped

            try
            {
                _app.WorkbookOpen -= OnWorkbookOpen;
                _app.WorkbookBeforeClose -= OnWorkbookBeforeClose;
                _app.WorkbookAfterSave -= OnWorkbookAfterSave;
                _app.WorkbookActivate -= OnWorkbookActivate;
                _app.WorkbookDeactivate -= OnWorkbookDeactivate;
                ((MSExcel.AppEvents_Event)_app).NewWorkbook -= OnNewWorkbook;
                _app.WorkbookNewSheet -= OnWorkbookNewSheet;
                _app.SheetChange -= OnSheetChange;
                _app.SheetBeforeDoubleClick -= OnSheetBeforeDoubleClick;
                _app.SheetActivate -= OnSheetActivate;

                if (_config.TrackSelectionChanges)
                {
                    _app.SheetSelectionChange -= OnSheetSelectionChange;
                }

                // _isTracking already set to 0 by CompareExchange above

                // Log session end event
                EnqueueEvent(new AuditEvent
                {
                    EventType = AuditEventType.SessionEnd,
                    SessionId = _sessionId,
                    Details = $"DGT tracking stopped"
                });

                Log.Information("Event tracking stopped");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping event tracking");
            }
        }

        // === EVENT HANDLERS ===

        private void OnWorkbookOpen(MSExcel.Workbook wb)
        {
            var workbookName = GetSafeWorkbookName(wb);
            ResetWorkbookEventCount(workbookName);

            EnqueueEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookOpen,
                WorkbookName = workbookName,
                WorkbookPath = GetSafeWorkbookPath(wb)
            });

            // Pre-populate cache with current cell values for accurate old value tracking
            // QueueAsMacro handles async execution on Excel's main thread safely
            try
            {
                PrePopulateCacheForWorkbook(wb, workbookName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to pre-populate cache for workbook");
            }
        }

        private void OnWorkbookBeforeClose(MSExcel.Workbook wb, ref bool cancel)
        {
            var workbookName = GetSafeWorkbookName(wb);

            EnqueueEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookClose,
                WorkbookName = workbookName,
                WorkbookPath = GetSafeWorkbookPath(wb),
                Details = $"TotalEvents={GetWorkbookEventCount(workbookName)}"
            });

            // Clean up tracking data for this workbook
            _workbookEventCounts.TryRemove(workbookName, out _);

            // Clean up cache entries for this workbook to free memory
            _cellValueCache.RemoveWhere(key => key.StartsWith(workbookName + "!"));
        }

        private void OnWorkbookAfterSave(MSExcel.Workbook wb, bool success)
        {
            EnqueueEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookSave,
                WorkbookName = GetSafeWorkbookName(wb),
                WorkbookPath = GetSafeWorkbookPath(wb),
                Details = $"Success={success}"
            });
        }

        private void OnWorkbookActivate(MSExcel.Workbook wb)
        {
            var workbookName = GetSafeWorkbookName(wb);
            _currentWorkbookName = workbookName;

            // Restore event count for this workbook
            if (_workbookEventCounts.TryGetValue(workbookName, out var count))
            {
                _currentWorkbookEventCount = count;
            }
            else
            {
                _currentWorkbookEventCount = 0;
            }

            EnqueueEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookActivate,
                WorkbookName = workbookName,
                WorkbookPath = GetSafeWorkbookPath(wb)
            });
        }

        private void OnWorkbookDeactivate(MSExcel.Workbook wb)
        {
            var workbookName = GetSafeWorkbookName(wb);

            EnqueueEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookDeactivate,
                WorkbookName = workbookName,
                WorkbookPath = GetSafeWorkbookPath(wb)
            });

            // Save current event count for this workbook
            _workbookEventCounts[workbookName] = _currentWorkbookEventCount;
        }

        private void OnNewWorkbook(MSExcel.Workbook wb)
        {
            var workbookName = GetSafeWorkbookName(wb);
            ResetWorkbookEventCount(workbookName);

            EnqueueEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookNew,
                WorkbookName = workbookName
            });
        }

        private void OnSheetChange(object sheet, MSExcel.Range target)
        {
            MSExcel.Workbook wb = null;
            try
            {
                var ws = sheet as MSExcel.Worksheet;
                if (ws == null) return;

                wb = ws.Parent as MSExcel.Workbook;
                var workbookName = GetSafeWorkbookName(wb);
                var cellCount = target.Cells.Count;

                // BULK OPERATION DETECTION - Avoid queue overflow and performance issues
                if (cellCount > BULK_OPERATION_THRESHOLD)
                {
                    // Create single aggregated event for bulk operations (paste, fill, etc.)
                    EnqueueEvent(new AuditEvent
                    {
                        EventType = AuditEventType.CellChange,
                        WorkbookName = workbookName,
                        WorkbookPath = GetSafeWorkbookPath(wb),
                        SheetName = ws.Name,
                        CellAddress = GetSafeAddress(target),
                        CellCount = cellCount,
                        Details = $"BulkOperation:{cellCount} cells changed"
                    });

                    IncrementWorkbookEventCount(workbookName);

                    Log.Information("Bulk operation detected: {CellCount} cells changed in {Workbook}!{Sheet}",
                        cellCount, workbookName, ws.Name);
                    return;
                }

                // INDIVIDUAL CELL TRACKING - For normal operations
                foreach (MSExcel.Range cell in target.Cells)
                {
                    try
                    {
                        var cellAddress = GetSafeAddress(cell);
                        var cacheKey = $"{workbookName}!{ws.Name}!{cellAddress}";

                        // Try to get old value from cache
                        string oldValue = null;
                        if (_cellValueCache.TryGetValue(cacheKey, out var cachedValue))
                        {
                            oldValue = cachedValue?.ToString() ?? "";
                        }

                        // Get new value
                        var newValue = cell.Value?.ToString() ?? "";

                        // Get formula if configured
                        string formula = null;
                        if (_config.IncludeFormulas)
                        {
                            formula = GetCellFormula(cell);
                        }

                        EnqueueEvent(new AuditEvent
                        {
                            EventType = AuditEventType.CellChange,
                            WorkbookName = workbookName,
                            WorkbookPath = GetSafeWorkbookPath(wb),
                            SheetName = ws.Name,
                            CellAddress = cellAddress,
                            CellCount = 1,
                            OldValue = oldValue,
                            NewValue = newValue,
                            Formula = formula
                        });

                        // Update cache with new value
                        _cellValueCache.Set(cacheKey, cell.Value);

                        // Increment event counter
                        IncrementWorkbookEventCount(workbookName);
                    }
                    finally
                    {
                        // Release COM object to prevent memory leak
                        if (cell != null)
                            Marshal.ReleaseComObject(cell);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error capturing cell change event");
            }
            finally
            {
                // Release workbook COM object
                if (wb != null)
                    Marshal.ReleaseComObject(wb);
            }
        }

        // COMPLIANCE EVENTS: Sheet structural changes

        private void OnWorkbookNewSheet(MSExcel.Workbook wb, object sheet)
        {
            try
            {
                var ws = sheet as MSExcel.Worksheet;
                if (ws == null) return;

                EnqueueEvent(new AuditEvent
                {
                    EventType = AuditEventType.SheetAdd,
                    WorkbookName = GetSafeWorkbookName(wb),
                    WorkbookPath = GetSafeWorkbookPath(wb),
                    SheetName = ws.Name,
                    Details = "Sheet added to workbook"
                });

                Log.Debug("Sheet added: {Sheet} in {Workbook}", ws.Name, GetSafeWorkbookName(wb));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error capturing sheet add event");
            }
        }

        private void OnSheetActivate(object sheet)
        {
            try
            {
                var ws = sheet as MSExcel.Worksheet;
                if (ws == null) return;

                var wb = ws.Parent as MSExcel.Workbook;

                EnqueueEvent(new AuditEvent
                {
                    EventType = AuditEventType.SheetActivate,
                    WorkbookName = GetSafeWorkbookName(wb),
                    WorkbookPath = GetSafeWorkbookPath(wb),
                    SheetName = ws.Name,
                    Details = "Sheet activated"
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error capturing sheet activate event");
            }
        }

        private void OnSheetBeforeDoubleClick(object sheet, MSExcel.Range target, ref bool cancel)
        {
            // Track double-click events for compliance if needed
            // Currently not tracking - can be enabled later
        }

        private void OnSheetSelectionChange(object sheet, MSExcel.Range target)
        {
            try
            {
                var ws = sheet as MSExcel.Worksheet;
                if (ws == null) return;

                var wb = ws.Parent as MSExcel.Workbook;

                EnqueueEvent(new AuditEvent
                {
                    EventType = AuditEventType.SelectionChange,
                    WorkbookName = GetSafeWorkbookName(wb),
                    WorkbookPath = GetSafeWorkbookPath(wb),
                    SheetName = ws.Name,
                    CellAddress = GetSafeAddress(target),
                    CellCount = target.Cells.Count
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error capturing selection change event");
            }
        }

        // === HELPER METHODS ===

        private void EnqueueEvent(AuditEvent evt)
        {
            // Enrich event with common context
            evt.SessionId = _sessionId;
            evt.UserName = evt.UserName ?? Environment.UserName;
            evt.MachineName = evt.MachineName ?? Environment.MachineName;
            evt.UserDomain = evt.UserDomain ?? Environment.UserDomainName;
            evt.Timestamp = DateTime.UtcNow;

            _queue.Enqueue(evt);
        }

        private string GetSafeWorkbookName(MSExcel.Workbook wb)
        {
            try { return wb?.Name ?? "Unknown"; }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to get workbook name safely");
                return "Unknown";
            }
        }

        private string GetSafeWorkbookPath(MSExcel.Workbook wb)
        {
            try { return wb?.FullName ?? ""; }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to get workbook path safely");
                return "";
            }
        }

        private string GetSafeAddress(MSExcel.Range range)
        {
            try { return range?.Address ?? ""; }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to get range address safely");
                return "";
            }
        }

        private string GetCellValue(MSExcel.Range range)
        {
            try
            {
                if (range == null) return "";
                if (range.Cells.Count == 1)
                    return range.Value?.ToString() ?? "";
                return $"[{range.Cells.Count} cells]";
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error reading cell value");
                return "[Error reading value]";
            }
        }

        private string GetCellFormula(MSExcel.Range range)
        {
            try
            {
                if (range == null) return "";
                if (range.Cells.Count == 1)
                    return range.Formula?.ToString() ?? "";
                return "";
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error reading cell formula");
                return "";
            }
        }

        private bool AreValuesEqual(object value1, object value2)
        {
            // Handle null cases
            if (value1 == null && value2 == null) return true;
            if (value1 == null || value2 == null) return false;

            // Handle numeric comparisons (Excel often returns doubles)
            if (value1 is double d1 && value2 is double d2)
            {
                return Math.Abs(d1 - d2) < 0.000001; // Tolerance for floating point comparison
            }

            // Handle string comparisons
            return value1.ToString() == value2.ToString();
        }

        private void ResetWorkbookEventCount(string workbookName)
        {
            _currentWorkbookName = workbookName;
            Interlocked.Exchange(ref _currentWorkbookEventCount, 0);
            _workbookEventCounts.AddOrUpdate(workbookName, 0, (key, oldValue) => 0);
        }

        private void IncrementWorkbookEventCount(string workbookName)
        {
            // CRITICAL for compliance tracking: Thread-safe atomic increment
            if (workbookName == _currentWorkbookName)
            {
                Interlocked.Increment(ref _currentWorkbookEventCount);
            }

            // Increment count in concurrent dictionary (thread-safe)
            _workbookEventCounts.AddOrUpdate(
                workbookName,
                1, // Initial value if key doesn't exist
                (key, oldValue) => oldValue + 1); // Increment if exists
        }

        private int GetWorkbookEventCount(string workbookName)
        {
            if (_workbookEventCounts.TryGetValue(workbookName, out var count))
                return (int)count;
            return 0;
        }

        /// <summary>
        /// Gets the current event count for the active workbook (for Ribbon display)
        /// Thread-safe: called from Ribbon UI thread while events fire on Excel main thread
        /// </summary>
        public int GetCurrentWorkbookEventCount()
        {
            return (int)Interlocked.Read(ref _currentWorkbookEventCount);
        }

        /// <summary>
        /// Gets tracking status (thread-safe - called from Ribbon UI thread)
        /// </summary>
        public bool IsTracking => Interlocked.CompareExchange(ref _isTracking, 0, 0) == 1;

        /// <summary>
        /// Pre-populates the cache with current cell values for a workbook
        /// IMPORTANT: This must be called via QueueAsMacro to ensure COM thread safety
        /// </summary>
        private void PrePopulateCacheForWorkbook(MSExcel.Workbook wb, string workbookName)
        {
            try
            {
                // IMPORTANT: COM calls must happen on the main thread
                // Queue this work to run on Excel's main thread
                ExcelDna.Integration.ExcelAsyncUtil.QueueAsMacro(() =>
                {
                    try
                    {
                        foreach (MSExcel.Worksheet ws in wb.Worksheets)
                        {
                            MSExcel.Range usedRange = null;
                            try
                            {
                                // Only cache cells in the used range to avoid scanning empty sheets
                                usedRange = ws.UsedRange;
                                if (usedRange == null) continue;

                                // Limit to reasonable size to avoid long blocking
                                if (usedRange.Cells.Count > 10000)
                                {
                                    Log.Debug("Skipping cache pre-population for large sheet {Sheet} ({CellCount} cells)",
                                        ws.Name, usedRange.Cells.Count);
                                    continue;
                                }

                                int cachedCount = 0;
                                foreach (MSExcel.Range cell in usedRange.Cells)
                                {
                                    try
                                    {
                                        var cellAddress = GetSafeAddress(cell);
                                        var cacheKey = $"{workbookName}!{ws.Name}!{cellAddress}";

                                        // Only cache cells that have values
                                        if (cell.Value != null)
                                        {
                                            _cellValueCache.Set(cacheKey, cell.Value);
                                            cachedCount++;
                                        }
                                    }
                                    finally
                                    {
                                        // Release COM object to prevent memory leak
                                        if (cell != null)
                                            Marshal.ReleaseComObject(cell);
                                    }
                                }

                                Log.Debug("Pre-populated cache for sheet {Sheet}: {Count} cells",
                                    ws.Name, cachedCount);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Error pre-populating cache for sheet {Sheet}", ws.Name);
                            }
                            finally
                            {
                                // Release COM objects
                                if (usedRange != null)
                                    Marshal.ReleaseComObject(usedRange);
                                if (ws != null)
                                    Marshal.ReleaseComObject(ws);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Error pre-populating cache for workbook");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error queuing cache pre-population");
            }
        }

        public void Dispose()
        {
            StopTracking();
            _cellValueCache.Clear();
            _workbookEventCounts.Clear();
        }
    }
}
