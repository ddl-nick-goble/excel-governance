using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using DominoGovernanceTracker.Models;
using DominoGovernanceTracker.Services;
using ExcelDna.Integration;
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
        private readonly ModelRegistrationService _modelService;
        private readonly string _sessionId;
        private int _isTracking; // 0 = false, 1 = true (thread-safe with Interlocked - read from Ribbon UI thread)
        private int _isReconnecting; // 0 = false, 1 = true (prevents concurrent reconnect attempts)

        // LRU cache to track old values before changes (bounded to prevent memory leak)
        private readonly LruCache<string, object> _cellValueCache = new LruCache<string, object>(10000);

        // LRU cache to track formatting fingerprints for change detection
        private readonly LruCache<string, string> _cellFormatCache = new LruCache<string, string>(10000);

        // AfterCalculate debounce timer (collapses rapid recalc events into a single diff pass)
        private Timer _afterCalculateTimer;
        private const int AFTER_CALCULATE_DEBOUNCE_MS = 250;

        // Correlation ID linking a direct cell change to its dependent recalculated changes
        private string _lastChangeCorrelationId;
        private string _debouncedCorrelationId;

        // Calculation mode polling — detect Automatic/Manual changes
        private MSExcel.XlCalculation _lastCalculationMode;
        private Timer _calcModeTimer;
        private const int CALC_MODE_POLL_MS = 1000;

        // Defined names snapshot per workbook for add/change/delete detection
        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _definedNamesSnapshot
            = new ConcurrentDictionary<string, Dictionary<string, string>>();

        // Undo detection — track undo caption to detect when it changes
        private string _lastUndoCaption;
        private string _lastRedoCaption;

        // Track workbook full paths for Save As detection
        private readonly ConcurrentDictionary<string, string> _workbookPaths
            = new ConcurrentDictionary<string, string>();

        // Event counter per workbook (thread-safe - CRITICAL for compliance tracking accuracy)
        private readonly ConcurrentDictionary<string, long> _workbookEventCounts = new ConcurrentDictionary<string, long>();
        private long _currentWorkbookEventCount; // Thread-safe with Interlocked (read from Ribbon UI thread)
        private string _currentWorkbookName;

        // Bulk operation threshold - operations affecting more cells are aggregated
        private const int BULK_OPERATION_THRESHOLD = 100;

        public EventManager(MSExcel.Application app, EventQueue queue, DgtConfig config, ModelRegistrationService modelService)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
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

                // Subscribe to AfterCalculate for dependent/calculated cell change detection
                ((MSExcel.AppEvents_Event)_app).AfterCalculate += OnAfterCalculate;

                // Snapshot initial calculation mode
                try { _lastCalculationMode = _app.Calculation; } catch { }

                // Start polling for calc mode changes and undo/redo detection
                _calcModeTimer = new Timer(PollCalcModeAndUndo, null, CALC_MODE_POLL_MS, CALC_MODE_POLL_MS);

                // Snapshot sheets for already-open workbooks so rename/delete tracking works
                try
                {
                    foreach (MSExcel.Workbook wb in _app.Workbooks)
                    {
                        try
                        {
                            var workbookName = GetSafeWorkbookName(wb);
                            _modelService.CacheWorkbookRegistration(wb, workbookName);
                            _workbookPaths[workbookName] = GetSafeWorkbookPath(wb);
                            SnapshotSheets(wb, workbookName);
                        }
                        finally
                        {
                            if (wb != null) Marshal.ReleaseComObject(wb);
                        }
                    }
                }
                catch { }

                // _isTracking already set to 1 by CompareExchange above
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
                ((MSExcel.AppEvents_Event)_app).AfterCalculate -= OnAfterCalculate;

                if (_config.TrackSelectionChanges)
                {
                    _app.SheetSelectionChange -= OnSheetSelectionChange;
                }

                // Stop timers
                _afterCalculateTimer?.Dispose();
                _afterCalculateTimer = null;
                _calcModeTimer?.Dispose();
                _calcModeTimer = null;

                // _isTracking already set to 0 by CompareExchange above
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

            // Cache registration status from workbook custom document properties
            _modelService.CacheWorkbookRegistration(wb, workbookName);

            // Store path for Save As detection
            _workbookPaths[workbookName] = GetSafeWorkbookPath(wb);

            EnqueueEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookOpen,
                WorkbookName = workbookName,
                WorkbookPath = GetSafeWorkbookPath(wb)
            });

            // Snapshot sheets for delete/rename detection
            SnapshotSheets(wb, workbookName);

            // Snapshot defined names for add/change/delete detection
            SnapshotDefinedNames(wb, workbookName);

            // Subscribe to data refresh events on connections
            SubscribeDataRefreshEvents(wb, workbookName);

            // Pre-populate cache with current cell values for accurate old value tracking
            // QueueAsMacro handles async execution on Excel's main thread safely
            try
            {
                PrePopulateCacheForWorkbook(wb, workbookName);
            }
            catch (Exception ex)
            {
                if (!HandleIfDisconnected(ex))
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
            _workbookPaths.TryRemove(workbookName, out _);
            _modelService.RemoveFromCache(workbookName);
            _definedNamesSnapshot.TryRemove(workbookName, out _);
            _sheetSnapshots.TryRemove(workbookName, out _);

            // Clean up cache entries for this workbook to free memory
            _cellValueCache.RemoveWhere(key => key.StartsWith(workbookName + "!"));
            _cellFormatCache.RemoveWhere(key => key.StartsWith(workbookName + "!"));
        }

        private void OnWorkbookAfterSave(MSExcel.Workbook wb, bool success)
        {
            var workbookName = GetSafeWorkbookName(wb);
            var workbookPath = GetSafeWorkbookPath(wb);

            // Detect Save As: find any stale entry whose key doesn't match the current workbook name
            // This covers: first save of "Book1" -> "file.xlsx", Save As rename, etc.
            string oldName = null;
            string oldPath = null;
            foreach (var kvp in _workbookPaths)
            {
                if (kvp.Key != workbookName)
                {
                    // Check if this old entry refers to a workbook that no longer exists
                    // (i.e., it was this workbook under its previous name)
                    try
                    {
                        var _ = _app.Workbooks[kvp.Key]; // throws if not found
                    }
                    catch
                    {
                        // Old name no longer exists — this was a rename/SaveAs
                        oldName = kvp.Key;
                        oldPath = kvp.Value;
                        _workbookPaths.TryRemove(kvp.Key, out _);
                        break;
                    }
                }
            }

            if (oldName != null && success)
            {
                EnqueueEvent(new AuditEvent
                {
                    EventType = AuditEventType.WorkbookSaveAs,
                    WorkbookName = workbookName,
                    WorkbookPath = workbookPath,
                    OldValue = oldPath,
                    NewValue = workbookPath,
                    Details = $"SaveAs from '{oldName}' to '{workbookName}'"
                });

                // Clean up stale cache entries under the old name
                _modelService.RemoveFromCache(oldName);
                _workbookEventCounts.TryRemove(oldName, out _);

                IncrementWorkbookEventCount(workbookName);
            }

            // Always update path tracking
            _workbookPaths[workbookName] = workbookPath;

            // Always re-cache registration from the actual document properties.
            // This ensures the cache is correct after Save, Save As, or any name change.
            if (success)
            {
                _modelService.CacheWorkbookRegistration(wb, workbookName);
            }

            EnqueueEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookSave,
                WorkbookName = workbookName,
                WorkbookPath = workbookPath,
                Details = $"Success={success}"
            });

            // Detect defined name changes on save
            try
            {
                DetectDefinedNameChanges(wb, workbookName, workbookPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error detecting defined name changes on save");
            }
        }

        private void OnWorkbookActivate(MSExcel.Workbook wb)
        {
            var workbookName = GetSafeWorkbookName(wb);
            _currentWorkbookName = workbookName;

            // Cache registration status on activate (handles workbook switching)
            _modelService.CacheWorkbookRegistration(wb, workbookName);

            // Restore event count for this workbook
            if (_workbookEventCounts.TryGetValue(workbookName, out var count))
            {
                _currentWorkbookEventCount = count;
            }
            else
            {
                _currentWorkbookEventCount = 0;
            }

            // Update stored path (covers normal activate + post-SaveAs activate)
            _workbookPaths[workbookName] = GetSafeWorkbookPath(wb);

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

                // Generate correlation ID so dependent CalculatedCellChange events link back
                _lastChangeCorrelationId = Guid.NewGuid().ToString("N");

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

                    IncrementWorkbookEventCount(workbookName, cellCount);

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

                        // Get formula to determine ValueInput vs FormulaChange
                        string formula = GetCellFormula(cell);
                        bool hasFormula = !string.IsNullOrEmpty(formula)
                            && formula.StartsWith("=");

                        var eventType = hasFormula
                            ? AuditEventType.FormulaChange
                            : AuditEventType.ValueInput;

                        EnqueueEvent(new AuditEvent
                        {
                            EventType = eventType,
                            WorkbookName = workbookName,
                            WorkbookPath = GetSafeWorkbookPath(wb),
                            SheetName = ws.Name,
                            CellAddress = cellAddress,
                            CellCount = 1,
                            OldValue = oldValue,
                            NewValue = newValue,
                            Formula = hasFormula ? formula : null,
                            DisplayValue = GetCellDisplayValue(cell),
                            CorrelationId = _lastChangeCorrelationId
                        });

                        // For formula cells, also emit a CalculatedCellChange synchronously
                        // so we don't rely on the debounced AfterCalculate diff
                        if (hasFormula && oldValue != newValue)
                        {
                            EnqueueEvent(new AuditEvent
                            {
                                EventType = AuditEventType.CalculatedCellChange,
                                WorkbookName = workbookName,
                                WorkbookPath = GetSafeWorkbookPath(wb),
                                SheetName = ws.Name,
                                CellAddress = cellAddress,
                                CellCount = 1,
                                OldValue = oldValue,
                                NewValue = newValue,
                                Formula = formula,
                                DisplayValue = GetCellDisplayValue(cell),
                                CorrelationId = _lastChangeCorrelationId
                            });
                        }

                        // Always update cache with new value
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
                if (!HandleIfDisconnected(ex))
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
                if (!HandleIfDisconnected(ex))
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
                var workbookName = GetSafeWorkbookName(wb);
                var workbookPath = GetSafeWorkbookPath(wb);

                EnqueueEvent(new AuditEvent
                {
                    EventType = AuditEventType.SheetActivate,
                    WorkbookName = workbookName,
                    WorkbookPath = workbookPath,
                    SheetName = ws.Name,
                    Details = "Sheet activated"
                });

                // Detect sheet delete/rename by comparing to snapshot
                DetectSheetChanges(wb, workbookName, workbookPath);
            }
            catch (Exception ex)
            {
                if (!HandleIfDisconnected(ex))
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
                if (!HandleIfDisconnected(ex))
                    Log.Warning(ex, "Error capturing selection change event");
            }
        }

        // === SHEET DELETE DETECTION ===
        // Excel COM has no direct SheetDelete event at app level.
        // We detect deletes by comparing sheet lists on SheetActivate (which fires after delete).

        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _sheetSnapshots
            = new ConcurrentDictionary<string, Dictionary<string, string>>();

        private void SnapshotSheets(MSExcel.Workbook wb, string workbookName)
        {
            try
            {
                var sheets = new Dictionary<string, string>();
                foreach (MSExcel.Worksheet ws in wb.Worksheets)
                {
                    sheets[ws.CodeName] = ws.Name;
                }
                _sheetSnapshots[workbookName] = sheets;
            }
            catch (Exception ex)
            {
                Log.Debug("Error snapshotting sheets: {Error}", ex.Message);
            }
        }

        private void DetectSheetChanges(MSExcel.Workbook wb, string workbookName, string workbookPath)
        {
            try
            {
                if (!_sheetSnapshots.TryGetValue(workbookName, out var oldSheets)) return;

                var currentSheets = new Dictionary<string, string>();
                foreach (MSExcel.Worksheet ws in wb.Worksheets)
                {
                    currentSheets[ws.CodeName] = ws.Name;
                }

                // Detect deletes (CodeName in old but not in current)
                foreach (var kvp in oldSheets)
                {
                    if (!currentSheets.ContainsKey(kvp.Key))
                    {
                        EnqueueEvent(new AuditEvent
                        {
                            EventType = AuditEventType.SheetDelete,
                            WorkbookName = workbookName,
                            WorkbookPath = workbookPath,
                            SheetName = kvp.Value,
                            Details = $"Sheet '{kvp.Value}' deleted"
                        });
                        IncrementWorkbookEventCount(workbookName);
                    }
                }

                _sheetSnapshots[workbookName] = currentSheets;
            }
            catch (Exception ex)
            {
                Log.Debug("Error detecting sheet changes: {Error}", ex.Message);
            }
        }

        private void DetectSheetChangesForActiveWorkbook()
        {
            MSExcel.Workbook wb = null;
            try
            {
                wb = _app.ActiveWorkbook;
                if (wb == null) return;

                var workbookName = GetSafeWorkbookName(wb);
                var modelId = _modelService.GetCachedModelId(workbookName);
                if (string.IsNullOrEmpty(modelId)) return;

                if (!_sheetSnapshots.ContainsKey(workbookName))
                {
                    SnapshotSheets(wb, workbookName);
                    return;
                }

                DetectSheetChanges(wb, workbookName, GetSafeWorkbookPath(wb));
            }
            catch (Exception ex)
            {
                if (!HandleIfDisconnected(ex))
                    Log.Debug("Error detecting sheet changes (poll): {Error}", ex.Message);
            }
            finally
            {
                if (wb != null) Marshal.ReleaseComObject(wb);
            }
        }

        // === CALCULATION MODE + UNDO/REDO POLLING ===

        private void PollCalcModeAndUndo(object state)
        {
            try
            {
                ExcelAsyncUtil.QueueAsMacro(() =>
                {
                    try
                    {
                        // --- Calc mode detection ---
                        var currentMode = _app.Calculation;
                        if (currentMode != _lastCalculationMode)
                        {
                            var oldMode = _lastCalculationMode;
                            _lastCalculationMode = currentMode;

                            // Get active workbook context
                            var wb = _app.ActiveWorkbook;
                            if (wb != null)
                            {
                                EnqueueEvent(new AuditEvent
                                {
                                    EventType = AuditEventType.CalculationModeChange,
                                    WorkbookName = GetSafeWorkbookName(wb),
                                    WorkbookPath = GetSafeWorkbookPath(wb),
                                    OldValue = oldMode.ToString(),
                                    NewValue = currentMode.ToString(),
                                    Details = $"{oldMode} -> {currentMode}"
                                });
                            }
                        }

                        // --- Undo/Redo detection ---
                        DetectUndoRedo();

                        // --- Format change detection for active selection ---
                        DetectSelectionFormatChange();

                        // --- Sheet rename/delete detection for active workbook ---
                        DetectSheetChangesForActiveWorkbook();
                    }
                    catch (Exception ex)
                    {
                        if (!HandleIfDisconnected(ex))
                            Log.Debug("Error in calc mode / undo poll: {Error}", ex.Message);
                    }
                });
            }
            catch { }
        }

        private void DetectUndoRedo()
        {
            try
            {
                // Excel exposes undo/redo state via CommandBars
                string undoCaption = null;
                string redoCaption = null;

                try
                {
                    var undoControl = _app.CommandBars["Standard"].FindControl(Id: 128); // Undo button
                    undoCaption = undoControl?.Caption;
                }
                catch { }

                try
                {
                    var redoControl = _app.CommandBars["Standard"].FindControl(Id: 129); // Redo button
                    redoCaption = redoControl?.Caption;
                }
                catch { }

                // If undo caption changed and redo caption appeared/changed, user did an undo
                // Heuristic: when undo shrinks (something was undone), redo grows
                if (_lastUndoCaption != null && undoCaption != _lastUndoCaption)
                {
                    // Check if redo grew (undo was performed) vs redo shrunk (redo was performed)
                    if (redoCaption != null && redoCaption != _lastRedoCaption && _lastRedoCaption != redoCaption)
                    {
                        // Redo caption changed — if it became non-empty or different, undo happened
                        if (!string.IsNullOrEmpty(redoCaption) && (string.IsNullOrEmpty(_lastRedoCaption) || redoCaption != _lastRedoCaption))
                        {
                            var wb = _app.ActiveWorkbook;
                            if (wb != null)
                            {
                                EnqueueEvent(new AuditEvent
                                {
                                    EventType = AuditEventType.UndoPerformed,
                                    WorkbookName = GetSafeWorkbookName(wb),
                                    WorkbookPath = GetSafeWorkbookPath(wb),
                                    Details = undoCaption ?? "Undo"
                                });
                            }
                        }
                    }
                }

                if (_lastRedoCaption != null && redoCaption != _lastRedoCaption)
                {
                    // If redo caption shrunk/disappeared and undo grew, redo was performed
                    if (string.IsNullOrEmpty(redoCaption) && !string.IsNullOrEmpty(_lastRedoCaption))
                    {
                        var wb = _app.ActiveWorkbook;
                        if (wb != null)
                        {
                            EnqueueEvent(new AuditEvent
                            {
                                EventType = AuditEventType.RedoPerformed,
                                WorkbookName = GetSafeWorkbookName(wb),
                                WorkbookPath = GetSafeWorkbookPath(wb),
                                Details = _lastRedoCaption ?? "Redo"
                            });
                        }
                    }
                }

                _lastUndoCaption = undoCaption;
                _lastRedoCaption = redoCaption;
            }
            catch (Exception ex)
            {
                Log.Debug("Error detecting undo/redo: {Error}", ex.Message);
            }
        }

        // === DATA REFRESH EVENTS ===

        private void SubscribeDataRefreshEvents(MSExcel.Workbook wb, string workbookName)
        {
            try
            {
                foreach (MSExcel.QueryTable qt in GetAllQueryTables(wb))
                {
                    try
                    {
                        var wbName = workbookName; // capture for closure
                        qt.BeforeRefresh += (ref bool cancel) =>
                        {
                            EnqueueEvent(new AuditEvent
                            {
                                EventType = AuditEventType.DataRefreshStart,
                                WorkbookName = wbName,
                                WorkbookPath = GetSafeWorkbookPath(wb),
                                Details = $"Connection={qt.Connection ?? "Unknown"}"
                            });
                        };
                        qt.AfterRefresh += (bool success) =>
                        {
                            EnqueueEvent(new AuditEvent
                            {
                                EventType = AuditEventType.DataRefreshEnd,
                                WorkbookName = wbName,
                                WorkbookPath = GetSafeWorkbookPath(wb),
                                Details = $"Success={success},Connection={qt.Connection ?? "Unknown"}"
                            });
                        };
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("Error subscribing to QueryTable refresh: {Error}", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Error subscribing data refresh events: {Error}", ex.Message);
            }
        }

        private static List<MSExcel.QueryTable> GetAllQueryTables(MSExcel.Workbook wb)
        {
            var result = new List<MSExcel.QueryTable>();
            try
            {
                foreach (MSExcel.Worksheet ws in wb.Worksheets)
                {
                    try
                    {
                        foreach (MSExcel.QueryTable qt in ws.QueryTables)
                        {
                            result.Add(qt);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private void DetectSelectionFormatChange()
        {
            MSExcel.Range selection = null;
            MSExcel.Worksheet ws = null;
            MSExcel.Workbook wb = null;
            try
            {
                selection = _app.Selection as MSExcel.Range;
                if (selection == null) return;
                if (selection.Cells.Count != 1) return;

                ws = selection.Worksheet;
                if (ws == null) return;
                wb = ws.Parent as MSExcel.Workbook;
                if (wb == null) return;

                var workbookName = GetSafeWorkbookName(wb);
                var modelId = _modelService.GetCachedModelId(workbookName);
                if (string.IsNullOrEmpty(modelId)) return;

                var cellAddress = GetSafeAddress(selection);
                if (string.IsNullOrEmpty(cellAddress)) return;

                var cacheKey = $"{workbookName}!{ws.Name}!{cellAddress}";
                var currentFormat = GetFormatFingerprint(selection);
                if (_cellFormatCache.TryGetValue(cacheKey, out var oldFormat))
                {
                    if (oldFormat != currentFormat)
                    {
                        EnqueueEvent(new AuditEvent
                        {
                            EventType = AuditEventType.FormatChange,
                            WorkbookName = workbookName,
                            WorkbookPath = GetSafeWorkbookPath(wb),
                            SheetName = ws.Name,
                            CellAddress = cellAddress,
                            CellCount = 1,
                            OldValue = oldFormat,
                            NewValue = currentFormat,
                            DisplayValue = GetCellDisplayValue(selection),
                            CorrelationId = _lastChangeCorrelationId
                        });

                        _cellFormatCache.Set(cacheKey, currentFormat);
                        IncrementWorkbookEventCount(workbookName);
                    }
                }
                else
                {
                    _cellFormatCache.Set(cacheKey, currentFormat);
                }
            }
            catch (Exception ex)
            {
                if (!HandleIfDisconnected(ex))
                    Log.Debug("Error detecting selection format change: {Error}", ex.Message);
            }
            finally
            {
                if (selection != null) Marshal.ReleaseComObject(selection);
                if (ws != null) Marshal.ReleaseComObject(ws);
                if (wb != null) Marshal.ReleaseComObject(wb);
            }
        }

        // === DEFINED NAMES ===

        private void SnapshotDefinedNames(MSExcel.Workbook wb, string workbookName)
        {
            try
            {
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (MSExcel.Name name in wb.Names)
                {
                    try
                    {
                        names[name.Name] = name.RefersTo?.ToString() ?? "";
                    }
                    catch { }
                }
                _definedNamesSnapshot[workbookName] = names;
            }
            catch (Exception ex)
            {
                Log.Debug("Error snapshotting defined names: {Error}", ex.Message);
            }
        }

        private void DetectDefinedNameChanges(MSExcel.Workbook wb, string workbookName, string workbookPath)
        {
            if (!_definedNamesSnapshot.TryGetValue(workbookName, out var oldNames)) return;

            var currentNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (MSExcel.Name name in wb.Names)
            {
                try
                {
                    currentNames[name.Name] = name.RefersTo?.ToString() ?? "";
                }
                catch { }
            }

            // Detect added and changed
            foreach (var kvp in currentNames)
            {
                if (oldNames.TryGetValue(kvp.Key, out var oldRef))
                {
                    if (oldRef != kvp.Value)
                    {
                        EnqueueEvent(new AuditEvent
                        {
                            EventType = AuditEventType.DefinedNameChange,
                            WorkbookName = workbookName,
                            WorkbookPath = workbookPath,
                            OldValue = oldRef,
                            NewValue = kvp.Value,
                            Details = $"Name={kvp.Key}"
                        });
                        IncrementWorkbookEventCount(workbookName);
                    }
                }
                else
                {
                    EnqueueEvent(new AuditEvent
                    {
                        EventType = AuditEventType.DefinedNameAdd,
                        WorkbookName = workbookName,
                        WorkbookPath = workbookPath,
                        NewValue = kvp.Value,
                        Details = $"Name={kvp.Key}"
                    });
                    IncrementWorkbookEventCount(workbookName);
                }
            }

            // Detect deleted
            foreach (var kvp in oldNames)
            {
                if (!currentNames.ContainsKey(kvp.Key))
                {
                    EnqueueEvent(new AuditEvent
                    {
                        EventType = AuditEventType.DefinedNameDelete,
                        WorkbookName = workbookName,
                        WorkbookPath = workbookPath,
                        OldValue = kvp.Value,
                        Details = $"Name={kvp.Key}"
                    });
                    IncrementWorkbookEventCount(workbookName);
                }
            }

            _definedNamesSnapshot[workbookName] = currentNames;
        }

        // === AFTER-CALCULATE DIFF (Dependent cells + formatting) ===

        private void OnAfterCalculate()
        {
            // Debounce: reset timer on each recalc, fire diff after 250ms of quiet
            // Capture correlation ID on first fire — this is the edit that started the cascade
            if (_afterCalculateTimer == null)
            {
                _debouncedCorrelationId = _lastChangeCorrelationId;
                _afterCalculateTimer = new Timer(OnAfterCalculateDebounced, null,
                    AFTER_CALCULATE_DEBOUNCE_MS, Timeout.Infinite);
            }
            else
            {
                _afterCalculateTimer.Change(AFTER_CALCULATE_DEBOUNCE_MS, Timeout.Infinite);
            }
        }

        private void OnAfterCalculateDebounced(object state)
        {
            // Must run COM reads on the main Excel thread
            try
            {
                ExcelAsyncUtil.QueueAsMacro(PerformCacheDiff);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error queuing AfterCalculate diff");
            }
        }

        private void PerformCacheDiff()
        {
            try
            {
                var correlationId = _debouncedCorrelationId ?? _lastChangeCorrelationId;
                var valueSnapshot = _cellValueCache.GetSnapshot();
                var formatSnapshot = _cellFormatCache.GetSnapshot();

                // Build a set of format cache keys for quick lookup
                var formatsByKey = new Dictionary<string, string>(formatSnapshot.Count);
                foreach (var kvp in formatSnapshot)
                {
                    formatsByKey[kvp.Key] = kvp.Value;
                }

                // Group cache entries by workbook+sheet for batch reading
                var sheetGroups = new Dictionary<string, List<KeyValuePair<string, object>>>();
                foreach (var kvp in valueSnapshot)
                {
                    // cacheKey format: "workbookName!sheetName!cellAddress"
                    var firstBang = kvp.Key.IndexOf('!');
                    if (firstBang < 0) continue;
                    var secondBang = kvp.Key.IndexOf('!', firstBang + 1);
                    if (secondBang < 0) continue;

                    var sheetKey = kvp.Key.Substring(0, secondBang); // "workbookName!sheetName"
                    if (!sheetGroups.ContainsKey(sheetKey))
                        sheetGroups[sheetKey] = new List<KeyValuePair<string, object>>();
                    sheetGroups[sheetKey].Add(kvp);
                }

                foreach (var group in sheetGroups)
                {
                    var firstBang = group.Key.IndexOf('!');
                    var workbookName = group.Key.Substring(0, firstBang);
                    var sheetName = group.Key.Substring(firstBang + 1);

                    // Only process registered workbooks
                    var modelId = _modelService.GetCachedModelId(workbookName);
                    if (string.IsNullOrEmpty(modelId)) continue;

                    MSExcel.Workbook wb = null;
                    MSExcel.Worksheet ws = null;
                    try
                    {
                        wb = _app.Workbooks[workbookName];
                        ws = wb.Worksheets[sheetName] as MSExcel.Worksheet;
                        if (ws == null) continue;

                        var workbookPath = GetSafeWorkbookPath(wb);

                        foreach (var entry in group.Value)
                        {
                            var cacheKey = entry.Key;
                            var secondBang = cacheKey.IndexOf('!', firstBang + 1);
                            var cellAddress = cacheKey.Substring(secondBang + 1);

                            MSExcel.Range cell = null;
                            try
                            {
                                cell = ws.Range[cellAddress];

                                // --- Value diff ---
                                var currentValue = cell.Value;
                                if (!AreValuesEqual(entry.Value, currentValue))
                                {
                                    var oldVal = entry.Value?.ToString() ?? "";
                                    var newVal = currentValue?.ToString() ?? "";

                                    string formula = null;
                                    if (_config.IncludeFormulas)
                                        formula = GetCellFormula(cell);

                                    EnqueueEvent(new AuditEvent
                                    {
                                        EventType = AuditEventType.CalculatedCellChange,
                                        WorkbookName = workbookName,
                                        WorkbookPath = workbookPath,
                                        SheetName = sheetName,
                                        CellAddress = cellAddress,
                                        CellCount = 1,
                                        OldValue = oldVal,
                                        NewValue = newVal,
                                        Formula = formula,
                                        DisplayValue = GetCellDisplayValue(cell),
                                        CorrelationId = correlationId
                                    });

                                    _cellValueCache.Set(cacheKey, currentValue);
                                    IncrementWorkbookEventCount(workbookName);
                                }

                                // --- Formatting diff ---
                                var currentFormat = GetFormatFingerprint(cell);
                                if (formatsByKey.TryGetValue(cacheKey, out var oldFormat))
                                {
                                    if (oldFormat != currentFormat)
                                    {
                                        EnqueueEvent(new AuditEvent
                                        {
                                            EventType = AuditEventType.FormatChange,
                                            WorkbookName = workbookName,
                                            WorkbookPath = workbookPath,
                                            SheetName = sheetName,
                                            CellAddress = cellAddress,
                                            CellCount = 1,
                                            OldValue = oldFormat,
                                            NewValue = currentFormat,
                                            DisplayValue = GetCellDisplayValue(cell),
                                            CorrelationId = correlationId
                                        });

                                        _cellFormatCache.Set(cacheKey, currentFormat);
                                        IncrementWorkbookEventCount(workbookName);
                                    }
                                }
                                else
                                {
                                    // First time seeing this cell's format — cache it
                                    _cellFormatCache.Set(cacheKey, currentFormat);
                                }
                            }
                            catch (Exception ex)
                            {
                                if (!HandleIfDisconnected(ex))
                                    Log.Debug("Error diffing cell {Cell}: {Error}", cacheKey, ex.Message);
                            }
                            finally
                            {
                                if (cell != null) Marshal.ReleaseComObject(cell);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!HandleIfDisconnected(ex))
                            Log.Debug("Error diffing sheet {Sheet}: {Error}", group.Key, ex.Message);
                    }
                    finally
                    {
                        if (ws != null) Marshal.ReleaseComObject(ws);
                        if (wb != null) Marshal.ReleaseComObject(wb);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!HandleIfDisconnected(ex))
                    Log.Warning(ex, "Error in AfterCalculate diff");
            }
        }

        private static string GetFormatFingerprint(MSExcel.Range cell)
        {
            try
            {
                var font = cell.Font;
                var interior = cell.Interior;
                var fp = string.Concat(
                    font.Color?.ToString() ?? "", "|",
                    font.Bold?.ToString() ?? "", "|",
                    font.Italic?.ToString() ?? "", "|",
                    font.Size?.ToString() ?? "", "|",
                    font.Name?.ToString() ?? "", "|",
                    interior.Color?.ToString() ?? "", "|",
                    cell.NumberFormat?.ToString() ?? "");
                return fp;
            }
            catch
            {
                return "";
            }
        }

        // === COM RECONNECTION ===

        private const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);

        /// <summary>
        /// Detaches all event handlers and re-attaches them.
        /// Called when COM objects disconnect (e.g. AutoSave in Excel 365).
        /// </summary>
        private void ReconnectEventHandlers()
        {
            if (Interlocked.CompareExchange(ref _isReconnecting, 1, 0) != 0)
                return; // Another thread is already reconnecting

            try
            {
                Log.Warning("=== COM RECONNECT: Re-subscribing Excel event handlers ===");

                // Detach all handlers (swallow errors — some may already be dead)
                try { _app.WorkbookOpen -= OnWorkbookOpen; } catch { }
                try { _app.WorkbookBeforeClose -= OnWorkbookBeforeClose; } catch { }
                try { _app.WorkbookAfterSave -= OnWorkbookAfterSave; } catch { }
                try { _app.WorkbookActivate -= OnWorkbookActivate; } catch { }
                try { _app.WorkbookDeactivate -= OnWorkbookDeactivate; } catch { }
                try { ((MSExcel.AppEvents_Event)_app).NewWorkbook -= OnNewWorkbook; } catch { }
                try { _app.WorkbookNewSheet -= OnWorkbookNewSheet; } catch { }
                try { _app.SheetChange -= OnSheetChange; } catch { }
                try { _app.SheetBeforeDoubleClick -= OnSheetBeforeDoubleClick; } catch { }
                try { _app.SheetActivate -= OnSheetActivate; } catch { }
                try { _app.SheetSelectionChange -= OnSheetSelectionChange; } catch { }
                try { ((MSExcel.AppEvents_Event)_app).AfterCalculate -= OnAfterCalculate; } catch { }

                // Re-attach all handlers
                _app.WorkbookOpen += OnWorkbookOpen;
                _app.WorkbookBeforeClose += OnWorkbookBeforeClose;
                _app.WorkbookAfterSave += OnWorkbookAfterSave;
                _app.WorkbookActivate += OnWorkbookActivate;
                _app.WorkbookDeactivate += OnWorkbookDeactivate;
                ((MSExcel.AppEvents_Event)_app).NewWorkbook += OnNewWorkbook;
                _app.WorkbookNewSheet += OnWorkbookNewSheet;
                _app.SheetChange += OnSheetChange;
                _app.SheetBeforeDoubleClick += OnSheetBeforeDoubleClick;
                _app.SheetActivate += OnSheetActivate;
                ((MSExcel.AppEvents_Event)_app).AfterCalculate += OnAfterCalculate;

                if (_config.TrackSelectionChanges)
                {
                    _app.SheetSelectionChange += OnSheetSelectionChange;
                }

                Log.Information("=== COM RECONNECT: Event handlers re-subscribed successfully ===");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "COM RECONNECT: Failed to re-subscribe event handlers");
            }
            finally
            {
                Interlocked.Exchange(ref _isReconnecting, 0);
            }
        }

        /// <summary>
        /// Returns true if the exception is a COM disconnection error,
        /// and if so, triggers reconnection.
        /// </summary>
        private bool HandleIfDisconnected(Exception ex)
        {
            if (ex is COMException comEx && comEx.ErrorCode == RPC_E_DISCONNECTED)
            {
                Log.Warning("COM object disconnected — scheduling reconnect");
                // Reconnect on a separate thread to avoid re-entrancy issues
                ThreadPool.QueueUserWorkItem(_ => ReconnectEventHandlers());
                return true;
            }
            return false;
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

            // Gate on registration: only enqueue events for registered workbooks
            var workbookName = evt.WorkbookName;
            if (!string.IsNullOrEmpty(workbookName))
            {
                var modelId = _modelService.GetCachedModelId(workbookName);
                if (string.IsNullOrEmpty(modelId))
                {
                    // Workbook is not registered — drop the event entirely
                    return;
                }
                evt.ModelId = modelId;
            }
            else
            {
                // System-level events (SessionStart/End, AddInLoad/Unload) have no workbook
                // Allow them through without a model ID
            }

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

        private string GetCellDisplayValue(MSExcel.Range range)
        {
            try
            {
                if (range == null) return "";
                if (range.Cells.Count == 1)
                    return range.Text?.ToString() ?? "";
                return "";
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error reading cell display value");
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

        private void IncrementWorkbookEventCount(string workbookName, int count = 1)
        {
            // CRITICAL for compliance tracking: Thread-safe atomic increment
            if (workbookName == _currentWorkbookName)
            {
                Interlocked.Add(ref _currentWorkbookEventCount, count);
            }

            // Increment count in concurrent dictionary (thread-safe)
            _workbookEventCounts.AddOrUpdate(
                workbookName,
                count, // Initial value if key doesn't exist
                (key, oldValue) => oldValue + count); // Increment if exists
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

                                        // Cache cells that have values, and their formatting
                                        if (cell.Value != null)
                                        {
                                            _cellValueCache.Set(cacheKey, cell.Value);
                                            _cellFormatCache.Set(cacheKey, GetFormatFingerprint(cell));
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
            _afterCalculateTimer?.Dispose();
            _calcModeTimer?.Dispose();
            _cellValueCache.Clear();
            _cellFormatCache.Clear();
            _workbookEventCounts.Clear();
            _definedNamesSnapshot.Clear();
            _sheetSnapshots.Clear();
            _workbookPaths.Clear();
        }
    }
}
