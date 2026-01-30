using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DominoGovernanceTracker.Core;
using DominoGovernanceTracker.Models;
using DominoGovernanceTracker.Services;
using ExcelDna.Integration;
using ExcelDna.Integration.CustomUI;
using Serilog;
using MSExcel = Microsoft.Office.Interop.Excel;
using CompositingQuality = System.Drawing.Drawing2D.CompositingQuality;
using GraphicsPath = System.Drawing.Drawing2D.GraphicsPath;
using PathGradientBrush = System.Drawing.Drawing2D.PathGradientBrush;

namespace DominoGovernanceTracker.UI
{
    /// <summary>
    /// Minimal Ribbon UI for DGT - shows tracking status and event counter
    /// </summary>
    [ComVisible(true)]
    public class DgtRibbon : ExcelRibbon
    {
        private IRibbonUI _ribbon;
        private WatchdogTimer _updateTimer;
        private static DgtRibbon _instance;

        // Cached bitmaps to prevent GDI resource leak
        private Bitmap _greenIndicator;
        private Bitmap _orangeIndicator;
        private Bitmap _grayIndicator;
        private readonly object _bitmapLock = new object();

        public DgtRibbon()
        {
            _instance = this;
        }

        public static DgtRibbon Instance => _instance;

        public override string GetCustomUI(string ribbonId)
        {
            // Minimal ribbon with subtle status indicator
            return @"
            <customUI xmlns='http://schemas.microsoft.com/office/2009/07/customui' onLoad='OnRibbonLoad'>
              <ribbon>
                <tabs>
                  <tab id='dgtTab' label='DGT' insertAfterMso='TabView'>
                    <group id='dgtStatusGroup' label='Governance Tracker'>
                      <button id='btnStatusIndicator'
                              getLabel='GetStatusLabel'
                              getImage='GetStatusImage'
                              size='normal'
                              screentip='DGT Tracking Status'
                              supertip='Green = Tracking normally | Orange = API error (buffering events locally) | Gray = Inactive'/>
                      <labelControl id='lblEventCount'
                                   getLabel='GetEventCountLabel'
                                   screentip='Number of events tracked for current workbook'/>
                      <separator id='sep1'/>
                      <button id='btnRefresh'
                              label='Refresh'
                              imageMso='Refresh'
                              size='normal'
                              onAction='OnRefreshClick'
                              screentip='Refresh tracking status'/>
                    </group>
                    <group id='dgtModelGroup' label='Model Registration'>
                      <button id='btnRegisterModel'
                              label='Register'
                              imageMso='ModuleInsert'
                              size='normal'
                              onAction='OnRegisterModelClick'
                              screentip='Register this workbook as a Domino model'
                              supertip='Opens a dialog to register or re-register this workbook. Events are only tracked for registered workbooks.'/>
                      <labelControl id='lblModelStatus'
                                   getLabel='GetModelStatusLabel'
                                   screentip='Model registration status for active workbook'/>
                    </group>
                    <group id='dgtBufferGroup' label='Event Buffer'>
                      <labelControl id='lblBufferCount'
                                   getLabel='GetBufferCountLabel'
                                   screentip='Number of events in local buffer (failed to send to API)'/>
                      <button id='btnFlushBuffer'
                              label='Flush Buffer'
                              imageMso='RecordsDeleteRecord'
                              size='normal'
                              onAction='OnFlushBufferClick'
                              screentip='Attempt to send buffered events to API'
                              supertip='Tries to send all locally buffered events to the API. Use this if events failed to send earlier.'/>
                      <button id='btnClearBuffer'
                              label='Clear Buffer'
                              imageMso='Delete'
                              size='normal'
                              onAction='OnClearBufferClick'
                              screentip='Delete all buffered events'
                              supertip='Permanently deletes all buffered events. Use during development to clear old incompatible events.'/>
                    </group>
                  </tab>
                </tabs>
              </ribbon>
            </customUI>";
        }

        public void OnRibbonLoad(IRibbonUI ribbon)
        {
            _ribbon = ribbon;
            Log.Debug("DGT Ribbon loaded");

            // Set up auto-refresh timer with watchdog (every 2 seconds)
            _updateTimer = new WatchdogTimer("RibbonUpdate", OnUpdateTimerElapsed, TimeSpan.FromSeconds(2));
            _updateTimer.Start();
        }

        private void OnUpdateTimerElapsed(object state)
        {
            try
            {
                // Invalidate labels to trigger refresh
                _ribbon?.Invalidate();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error refreshing ribbon");
            }
        }

        // === RIBBON CALLBACKS ===

        public string GetStatusLabel(IRibbonControl control)
        {
            try
            {
                var addIn = AddIn.Instance;
                if (addIn == null)
                    return "Unknown";

                var healthStatus = addIn.GetHealthStatus();

                switch (healthStatus)
                {
                    case TrackingHealthStatus.Healthy:
                        return "Tracking";
                    case TrackingHealthStatus.Degraded:
                        return "API Error";
                    case TrackingHealthStatus.Inactive:
                        return "Inactive";
                    default:
                        return "Unknown";
                }
            }
            catch
            {
                return "Error";
            }
        }

        public Bitmap GetStatusImage(IRibbonControl control)
        {
            try
            {
                var addIn = AddIn.Instance;
                if (addIn == null)
                {
                    // Thread-safe lazy-create cached bitmap
                    if (_grayIndicator == null)
                    {
                        lock (_bitmapLock)
                        {
                            if (_grayIndicator == null)
                                _grayIndicator = CreateGrayIndicator();
                        }
                    }
                    return _grayIndicator;
                }

                var healthStatus = addIn.GetHealthStatus();

                switch (healthStatus)
                {
                    case TrackingHealthStatus.Healthy:
                        // Thread-safe lazy-create cached bitmap
                        if (_greenIndicator == null)
                        {
                            lock (_bitmapLock)
                            {
                                if (_greenIndicator == null)
                                    _greenIndicator = CreateModernGreenIndicator();
                            }
                        }
                        return _greenIndicator;

                    case TrackingHealthStatus.Degraded:
                        // Thread-safe lazy-create cached bitmap
                        if (_orangeIndicator == null)
                        {
                            lock (_bitmapLock)
                            {
                                if (_orangeIndicator == null)
                                    _orangeIndicator = CreateOrangeIndicator();
                            }
                        }
                        return _orangeIndicator;

                    case TrackingHealthStatus.Inactive:
                    default:
                        // Thread-safe lazy-create cached bitmap
                        if (_grayIndicator == null)
                        {
                            lock (_bitmapLock)
                            {
                                if (_grayIndicator == null)
                                    _grayIndicator = CreateGrayIndicator();
                            }
                        }
                        return _grayIndicator;
                }
            }
            catch
            {
                // Thread-safe lazy-create cached bitmap
                if (_grayIndicator == null)
                {
                    lock (_bitmapLock)
                    {
                        if (_grayIndicator == null)
                            _grayIndicator = CreateGrayIndicator();
                    }
                }
                return _grayIndicator;
            }
        }

        public string GetEventCountLabel(IRibbonControl control)
        {
            try
            {
                var addIn = AddIn.Instance;
                if (addIn == null)
                    return "Events: -";

                var count = addIn.GetCurrentWorkbookEventCount();
                return $"Events: {count:N0}";
            }
            catch
            {
                return "Events: Error";
            }
        }

        public void OnRefreshClick(IRibbonControl control)
        {
            try
            {
                _ribbon?.Invalidate();
                Log.Debug("Ribbon manually refreshed");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error refreshing ribbon");
            }
        }

        // === MODEL REGISTRATION CALLBACKS ===

        public void OnRegisterModelClick(IRibbonControl control)
        {
            try
            {
                var app = (MSExcel.Application)ExcelDnaUtil.Application;
                var wb = app.ActiveWorkbook;

                if (wb == null)
                {
                    System.Windows.Forms.MessageBox.Show(
                        "No active workbook to register.",
                        "DGT - Register Model",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                    return;
                }

                var modelService = AddIn.Instance?.ModelService;
                if (modelService == null)
                {
                    System.Windows.Forms.MessageBox.Show(
                        "Model registration service not available.",
                        "DGT - Register Model",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                    return;
                }

                // Check if already registered (for re-register flow)
                var existingModelId = modelService.GetWorkbookModelId(wb);
                var existingModelName = modelService.GetWorkbookModelName(wb);
                var existingVersion = modelService.GetWorkbookVersion(wb);

                string existingDescription = null;
                // If re-registering, try to fetch current description from API
                if (!string.IsNullOrEmpty(existingModelId))
                {
                    try
                    {
                        var existing = Task.Run(() => modelService.CheckRegistrationAsync(existingModelId)).Result;
                        existingDescription = existing?.Description;
                    }
                    catch
                    {
                        // API might be down; proceed with what we have
                    }
                }

                var form = new ModelRegistrationForm(existingModelName, existingVersion, existingDescription);
                var result = form.ShowDialog();

                if (result != System.Windows.Forms.DialogResult.OK)
                    return;

                // Build registration request
                var request = new ModelRegistrationRequest
                {
                    ModelName = form.ModelName,
                    Description = form.Description,
                    RegisteredBy = Environment.UserName,
                    MachineName = Environment.MachineName,
                    ExistingModelId = existingModelId
                };

                // Call API asynchronously but wait for result (we're in a UI callback)
                RegisteredModel registered = null;
                try
                {
                    registered = Task.Run(() => modelService.RegisterAsync(request)).Result;
                }
                catch (Exception ex)
                {
                    var innerMsg = ex.InnerException?.Message ?? ex.Message;
                    System.Windows.Forms.MessageBox.Show(
                        $"Registration failed:\n\n{innerMsg}",
                        "DGT - Registration Error",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Error);
                    return;
                }

                // Save properties to workbook
                modelService.SetWorkbookProperties(wb, registered.ModelId, registered.ModelName, registered.Version);

                // Update cache
                var workbookName = wb.Name;
                modelService.UpdateCache(workbookName, registered.ModelId);

                // Emit a ModelRegistration audit event
                try
                {
                    var registrationEvent = new AuditEvent
                    {
                        EventType = AuditEventType.ModelRegistration,
                        UserName = Environment.UserName,
                        MachineName = Environment.MachineName,
                        UserDomain = Environment.UserDomainName,
                        WorkbookName = wb.Name,
                        WorkbookPath = wb.FullName,
                        ModelId = registered.ModelId,
                        Details = $"Model '{registered.ModelName}' registered as version {registered.Version}",
                    };
                    AddIn.Instance?.EventQueue?.Enqueue(registrationEvent);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to emit ModelRegistration event");
                }

                // Invalidate ribbon to show updated status
                _ribbon?.Invalidate();

                System.Windows.Forms.MessageBox.Show(
                    $"Workbook registered successfully!\n\n" +
                    $"Model: {registered.ModelName}\n" +
                    $"Version: {registered.Version}\n" +
                    $"Model ID: {registered.ModelId}\n\n" +
                    "Events will now be tracked for this workbook. " +
                    "Save the workbook to persist the registration.",
                    "DGT - Registration Complete",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in register model click handler");
                System.Windows.Forms.MessageBox.Show(
                    $"Error: {ex.Message}",
                    "DGT - Register Model Error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        public string GetModelStatusLabel(IRibbonControl control)
        {
            try
            {
                var addIn = AddIn.Instance;
                if (addIn?.ModelService == null)
                    return "Model: -";

                var app = (MSExcel.Application)ExcelDnaUtil.Application;
                var wb = app.ActiveWorkbook;
                if (wb == null)
                    return "Model: No workbook";

                var workbookName = wb.Name;
                var modelService = addIn.ModelService;

                if (modelService.IsWorkbookRegistered(workbookName))
                {
                    var modelName = modelService.GetWorkbookModelName(wb);
                    var version = modelService.GetWorkbookVersion(wb);
                    var display = !string.IsNullOrEmpty(modelName)
                        ? $"{modelName} v{version}"
                        : "Registered";
                    return $"Model: {display}";
                }

                return "Model: Not Registered";
            }
            catch
            {
                return "Model: Error";
            }
        }

        public string GetBufferCountLabel(IRibbonControl control)
        {
            try
            {
                var addIn = AddIn.Instance;
                if (addIn?.Publisher?.Buffer == null)
                    return "Buffered: -";

                var count = addIn.Publisher.Buffer.GetBufferedEventCount();
                if (count == 0)
                    return "Buffered: 0";

                return $"Buffered: {count:N0}";
            }
            catch
            {
                return "Buffered: Error";
            }
        }

        public void OnFlushBufferClick(IRibbonControl control)
        {
            try
            {
                var addIn = AddIn.Instance;
                if (addIn?.Publisher == null)
                {
                    System.Windows.Forms.MessageBox.Show(
                        "Publisher not available.",
                        "DGT - Flush Buffer",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                    return;
                }

                var count = addIn.Publisher.Buffer?.GetBufferedEventCount() ?? 0;
                if (count == 0)
                {
                    System.Windows.Forms.MessageBox.Show(
                        "No buffered events to flush.",
                        "DGT - Flush Buffer",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                    return;
                }

                var result = System.Windows.Forms.MessageBox.Show(
                    $"Attempt to send {count:N0} buffered events to the API?\n\n" +
                    "This will try to publish all locally buffered events. " +
                    "If the API is unavailable or events are incompatible, they will remain buffered.",
                    "DGT - Flush Buffer",
                    System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Question);

                if (result == System.Windows.Forms.DialogResult.Yes)
                {
                    // Trigger buffer flush asynchronously
                    Task.Run(async () =>
                    {
                        try
                        {
                            await addIn.Publisher.FlushBufferAsync();
                            Log.Information("Buffer flush completed");

                            // Refresh ribbon on UI thread
                            ExcelDnaUtil.Application.GetType().InvokeMember("Run",
                                System.Reflection.BindingFlags.InvokeMethod,
                                null,
                                ExcelDnaUtil.Application,
                                new object[] { "DgtRibbon.InvalidateRibbon" });
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error flushing buffer");
                        }
                    });

                    System.Windows.Forms.MessageBox.Show(
                        "Buffer flush started. Check the status indicator for results.",
                        "DGT - Flush Buffer",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in flush buffer click handler");
                System.Windows.Forms.MessageBox.Show(
                    $"Error flushing buffer: {ex.Message}",
                    "DGT - Flush Buffer Error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        public void OnClearBufferClick(IRibbonControl control)
        {
            try
            {
                var addIn = AddIn.Instance;
                if (addIn?.Publisher?.Buffer == null)
                {
                    System.Windows.Forms.MessageBox.Show(
                        "Buffer not available.",
                        "DGT - Clear Buffer",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                    return;
                }

                var count = addIn.Publisher.Buffer.GetBufferedEventCount();
                if (count == 0)
                {
                    System.Windows.Forms.MessageBox.Show(
                        "No buffered events to clear.",
                        "DGT - Clear Buffer",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                    return;
                }

                var result = System.Windows.Forms.MessageBox.Show(
                    $"Permanently delete {count:N0} buffered events?\n\n" +
                    "WARNING: This cannot be undone. Use this during development to clear " +
                    "old incompatible events that are preventing the circuit breaker from closing.",
                    "DGT - Clear Buffer",
                    System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Warning);

                if (result == System.Windows.Forms.DialogResult.Yes)
                {
                    var cleared = addIn.Publisher.Buffer.ClearBuffer();
                    if (cleared)
                    {
                        Log.Information("Buffer manually cleared ({Count} events deleted)", count);
                        _ribbon?.Invalidate();

                        System.Windows.Forms.MessageBox.Show(
                            $"Successfully deleted {count:N0} buffered events.",
                            "DGT - Clear Buffer",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Information);
                    }
                    else
                    {
                        System.Windows.Forms.MessageBox.Show(
                            "Failed to clear buffer. Check logs for details.",
                            "DGT - Clear Buffer",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in clear buffer click handler");
                System.Windows.Forms.MessageBox.Show(
                    $"Error clearing buffer: {ex.Message}",
                    "DGT - Clear Buffer Error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Manually invalidates the ribbon to force update
        /// </summary>
        public void InvalidateRibbon()
        {
            try
            {
                _ribbon?.Invalidate();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error invalidating ribbon");
            }
        }

        /// <summary>
        /// Triggers recovery of the update timer (useful after sleep/wake)
        /// </summary>
        public void RecoverUpdateTimer()
        {
            try
            {
                Log.Information("Recovering ribbon update timer");
                _updateTimer?.TriggerRecovery();
                InvalidateRibbon();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error recovering update timer");
            }
        }

        /// <summary>
        /// Creates a modern, subtle green glowing indicator icon
        /// </summary>
        private Bitmap CreateModernGreenIndicator()
        {
            int size = 16;
            var bitmap = new Bitmap(size, size);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;

                int centerX = size / 2;
                int centerY = size / 2;
                int dotRadius = 3;

                // Modern green color (softer, professional green like VS Code)
                Color modernGreen = Color.FromArgb(34, 197, 94); // #22C55E - emerald-500

                // Layer 1: Outermost glow (very soft, largest)
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(centerX - 7, centerY - 7, 14, 14);
                    using (var pgb = new PathGradientBrush(path))
                    {
                        pgb.CenterColor = Color.FromArgb(30, modernGreen);
                        pgb.SurroundColors = new[] { Color.FromArgb(0, modernGreen) };
                        g.FillPath(pgb, path);
                    }
                }

                // Layer 2: Middle glow
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(centerX - 5, centerY - 5, 10, 10);
                    using (var pgb = new PathGradientBrush(path))
                    {
                        pgb.CenterColor = Color.FromArgb(60, modernGreen);
                        pgb.SurroundColors = new[] { Color.FromArgb(0, modernGreen) };
                        g.FillPath(pgb, path);
                    }
                }

                // Layer 3: Inner glow
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(centerX - 4, centerY - 4, 8, 8);
                    using (var pgb = new PathGradientBrush(path))
                    {
                        pgb.CenterColor = Color.FromArgb(100, modernGreen);
                        pgb.SurroundColors = new[] { Color.FromArgb(30, modernGreen) };
                        g.FillPath(pgb, path);
                    }
                }

                // Layer 4: Core dot with gradient
                using (var brush = new LinearGradientBrush(
                    new Rectangle(centerX - dotRadius, centerY - dotRadius, dotRadius * 2, dotRadius * 2),
                    Color.FromArgb(255, 74, 222, 128),  // Lighter green at top
                    modernGreen,                         // Darker at bottom
                    45f))
                {
                    g.FillEllipse(brush, centerX - dotRadius, centerY - dotRadius, dotRadius * 2, dotRadius * 2);
                }

                // Layer 5: Highlight for depth
                using (var highlightBrush = new SolidBrush(Color.FromArgb(100, 255, 255, 255)))
                {
                    g.FillEllipse(highlightBrush, centerX - 1.5f, centerY - 2, 2, 2);
                }
            }

            return bitmap;
        }

        /// <summary>
        /// Creates an orange/amber warning indicator for degraded state
        /// </summary>
        private Bitmap CreateOrangeIndicator()
        {
            int size = 16;
            var bitmap = new Bitmap(size, size);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;

                int centerX = size / 2;
                int centerY = size / 2;
                int dotRadius = 3;

                // Modern orange/amber color for warning state
                Color modernOrange = Color.FromArgb(249, 115, 22); // #F97316 - orange-500

                // Layer 1: Outermost glow
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(centerX - 7, centerY - 7, 14, 14);
                    using (var pgb = new PathGradientBrush(path))
                    {
                        pgb.CenterColor = Color.FromArgb(30, modernOrange);
                        pgb.SurroundColors = new[] { Color.FromArgb(0, modernOrange) };
                        g.FillPath(pgb, path);
                    }
                }

                // Layer 2: Middle glow
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(centerX - 5, centerY - 5, 10, 10);
                    using (var pgb = new PathGradientBrush(path))
                    {
                        pgb.CenterColor = Color.FromArgb(60, modernOrange);
                        pgb.SurroundColors = new[] { Color.FromArgb(0, modernOrange) };
                        g.FillPath(pgb, path);
                    }
                }

                // Layer 3: Inner glow
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(centerX - 4, centerY - 4, 8, 8);
                    using (var pgb = new PathGradientBrush(path))
                    {
                        pgb.CenterColor = Color.FromArgb(100, modernOrange);
                        pgb.SurroundColors = new[] { Color.FromArgb(30, modernOrange) };
                        g.FillPath(pgb, path);
                    }
                }

                // Layer 4: Core dot with gradient
                using (var brush = new LinearGradientBrush(
                    new Rectangle(centerX - dotRadius, centerY - dotRadius, dotRadius * 2, dotRadius * 2),
                    Color.FromArgb(255, 251, 146, 60),  // Lighter orange at top
                    modernOrange,                        // Darker at bottom
                    45f))
                {
                    g.FillEllipse(brush, centerX - dotRadius, centerY - dotRadius, dotRadius * 2, dotRadius * 2);
                }

                // Layer 5: Highlight for depth
                using (var highlightBrush = new SolidBrush(Color.FromArgb(100, 255, 255, 255)))
                {
                    g.FillEllipse(highlightBrush, centerX - 1.5f, centerY - 2, 2, 2);
                }
            }

            return bitmap;
        }

        /// <summary>
        /// Creates a subtle gray inactive indicator icon
        /// </summary>
        private Bitmap CreateGrayIndicator()
        {
            int size = 16;
            var bitmap = new Bitmap(size, size);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;

                int centerX = size / 2;
                int centerY = size / 2;
                int dotRadius = 3;

                // Subtle gray color
                Color subtleGray = Color.FromArgb(156, 163, 175); // #9CA3AF - gray-400

                // Very subtle shadow/depth (no glow for inactive)
                using (var shadowBrush = new SolidBrush(Color.FromArgb(15, 0, 0, 0)))
                {
                    g.FillEllipse(shadowBrush, centerX - dotRadius, centerY - dotRadius + 0.5f, dotRadius * 2, dotRadius * 2);
                }

                // Core gray dot with subtle gradient
                using (var brush = new LinearGradientBrush(
                    new Rectangle(centerX - dotRadius, centerY - dotRadius, dotRadius * 2, dotRadius * 2),
                    Color.FromArgb(255, 209, 213, 219),  // Lighter gray at top
                    subtleGray,                           // Darker at bottom
                    45f))
                {
                    g.FillEllipse(brush, centerX - dotRadius, centerY - dotRadius, dotRadius * 2, dotRadius * 2);
                }

                // Subtle highlight for depth
                using (var highlightBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
                {
                    g.FillEllipse(highlightBrush, centerX - 1.5f, centerY - 2, 2, 2);
                }
            }

            return bitmap;
        }

        /// <summary>
        /// Cleanup
        /// </summary>
        public void Dispose()
        {
            _updateTimer?.Dispose();
            _ribbon = null;

            // Dispose cached bitmaps to prevent GDI resource leak
            _greenIndicator?.Dispose();
            _orangeIndicator?.Dispose();
            _grayIndicator?.Dispose();
        }
    }
}
