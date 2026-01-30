using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DominoGovernanceTracker.Models;
using Serilog;
using MSExcel = Microsoft.Office.Interop.Excel;

namespace DominoGovernanceTracker.Services
{
    /// <summary>
    /// Manages model registration with the Domino backend and
    /// reads/writes Custom Document Properties on workbooks.
    /// </summary>
    public class ModelRegistrationService : IDisposable
    {
        private const string PROP_MODEL_ID = "DGT_ModelId";
        private const string PROP_MODEL_NAME = "DGT_ModelName";
        private const string PROP_VERSION = "DGT_Version";

        private readonly DgtConfig _config;
        private readonly HttpClient _httpClient;

        // In-memory cache: workbook name -> model ID (or null if not registered)
        // Avoids repeated COM property lookups on every event
        private readonly ConcurrentDictionary<string, string> _registrationCache
            = new ConcurrentDictionary<string, string>();

        public ModelRegistrationService(DgtConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_config.HttpTimeoutSeconds),
                BaseAddress = new Uri(_config.ModelRegistrationEndpoint.TrimEnd('/') + "/")
            };
        }

        // === HTTP API CALLS ===

        /// <summary>
        /// Register a new model or re-register (fork) an existing one via the API.
        /// </summary>
        public async Task<RegisteredModel> RegisterAsync(ModelRegistrationRequest request)
        {
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("register", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("Model registration failed: {StatusCode} {Body}", response.StatusCode, body);
                throw new HttpRequestException(
                    $"Registration failed ({response.StatusCode}): {body}");
            }

            var model = JsonSerializer.Deserialize<RegisteredModel>(body);
            Log.Information("Model registered: {ModelId} {ModelName} v{Version}",
                model.ModelId, model.ModelName, model.Version);
            return model;
        }

        /// <summary>
        /// Check if a model ID is registered in the backend. Returns null if not found.
        /// </summary>
        public async Task<RegisteredModel> CheckRegistrationAsync(string modelId)
        {
            try
            {
                var response = await _httpClient.GetAsync(modelId);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;

                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<RegisteredModel>(body);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to check model registration for {ModelId}", modelId);
                return null;
            }
        }

        // === WORKBOOK CUSTOM DOCUMENT PROPERTIES ===

        /// <summary>
        /// Reads the DGT_ModelId custom property from a workbook. Returns null if not set.
        /// </summary>
        public string GetWorkbookModelId(MSExcel.Workbook wb)
        {
            try
            {
                dynamic customProps = wb.CustomDocumentProperties;
                foreach (dynamic prop in customProps)
                {
                    try
                    {
                        if (prop.Name == PROP_MODEL_ID)
                        {
                            return prop.Value?.ToString();
                        }
                    }
                    catch
                    {
                        // Skip unreadable properties
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read model ID from workbook: {Error}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Gets the model name stored in the workbook custom properties. Returns null if not set.
        /// </summary>
        public string GetWorkbookModelName(MSExcel.Workbook wb)
        {
            try
            {
                dynamic customProps = wb.CustomDocumentProperties;
                foreach (dynamic prop in customProps)
                {
                    try
                    {
                        if (prop.Name == PROP_MODEL_NAME)
                            return prop.Value?.ToString();
                    }
                    catch { }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the version stored in the workbook custom properties. Returns 0 if not set.
        /// </summary>
        public int GetWorkbookVersion(MSExcel.Workbook wb)
        {
            try
            {
                dynamic customProps = wb.CustomDocumentProperties;
                foreach (dynamic prop in customProps)
                {
                    try
                    {
                        if (prop.Name == PROP_VERSION)
                        {
                            if (int.TryParse(prop.Value?.ToString(), out int v))
                                return v;
                        }
                    }
                    catch { }
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Writes model registration properties to the workbook's custom document properties.
        /// The workbook must be saved afterward for the properties to persist.
        /// </summary>
        public void SetWorkbookProperties(MSExcel.Workbook wb, string modelId, string modelName, int version)
        {
            try
            {
                dynamic customProps = wb.CustomDocumentProperties;
                SetOrCreateProperty(customProps, PROP_MODEL_ID, modelId);
                SetOrCreateProperty(customProps, PROP_MODEL_NAME, modelName);
                SetOrCreateProperty(customProps, PROP_VERSION, version.ToString());

                Log.Information("Workbook properties set: ModelId={ModelId}, Name={ModelName}, Version={Version}",
                    modelId, modelName, version);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to set workbook properties");
                throw;
            }
        }

        private void SetOrCreateProperty(dynamic customProps, string name, string value)
        {
            // Try to update existing property
            foreach (dynamic prop in customProps)
            {
                try
                {
                    if (prop.Name == name)
                    {
                        prop.Value = value;
                        return;
                    }
                }
                catch { }
            }

            // Property doesn't exist — create it (msoPropertyTypeString = 4)
            customProps.Add(name, false, 4, value);
        }

        // === REGISTRATION CACHE ===

        /// <summary>
        /// Checks whether a workbook is registered. Uses in-memory cache for fast lookups.
        /// Call CacheWorkbookRegistration on workbook open/activate to populate.
        /// </summary>
        public bool IsWorkbookRegistered(string workbookName)
        {
            return _registrationCache.TryGetValue(workbookName, out var modelId)
                   && !string.IsNullOrEmpty(modelId);
        }

        /// <summary>
        /// Gets the cached model ID for a workbook. Returns null if not registered.
        /// </summary>
        public string GetCachedModelId(string workbookName)
        {
            _registrationCache.TryGetValue(workbookName, out var modelId);
            return modelId;
        }

        /// <summary>
        /// Reads custom document properties from the workbook and caches the registration status.
        /// Should be called on WorkbookOpen and WorkbookActivate.
        /// </summary>
        public void CacheWorkbookRegistration(MSExcel.Workbook wb, string workbookName)
        {
            var modelId = GetWorkbookModelId(wb);
            if (!string.IsNullOrEmpty(modelId))
            {
                _registrationCache[workbookName] = modelId;
                Log.Debug("Cached registration for {Workbook}: {ModelId}", workbookName, modelId);
            }
            else
            {
                _registrationCache[workbookName] = null;
                Log.Debug("Workbook {Workbook} is not registered", workbookName);
            }
        }

        /// <summary>
        /// Removes a workbook from the registration cache (e.g., on close).
        /// </summary>
        public void RemoveFromCache(string workbookName)
        {
            _registrationCache.TryRemove(workbookName, out _);
        }

        /// <summary>
        /// Updates the cache after a successful registration.
        /// </summary>
        public void UpdateCache(string workbookName, string modelId)
        {
            _registrationCache[workbookName] = modelId;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
