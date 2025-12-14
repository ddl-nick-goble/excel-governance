using System;
using System.IO;
using System.Text.Json;
using DominoGovernanceTracker.Models;
using Serilog;

namespace DominoGovernanceTracker.Config
{
    /// <summary>
    /// Manages loading and saving DGT configuration
    /// Thread-safe using Lazy<T> pattern (no volatile, no lock needed)
    /// </summary>
    public static class ConfigManager
    {
        private static readonly string DefaultConfigFileName = "config.json";
        private static readonly object _configLock = new object();
        private static Lazy<DgtConfig> _configLazy = new Lazy<DgtConfig>(() => LoadConfigInternal(null),
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// Loads configuration from JSON file (thread-safe)
        /// Thread-safe: locks to prevent race with ReloadConfig reassignment
        /// </summary>
        public static DgtConfig LoadConfig(string configPath = null)
        {
            // If custom path is specified, load directly (not cached)
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                return LoadConfigInternal(configPath);
            }

            // Use thread-safe lazy loading for default config
            // Lock prevents reading stale reference during ReloadConfig()
            lock (_configLock)
            {
                return _configLazy.Value;
            }
        }

        /// <summary>
        /// Internal config loading logic
        /// </summary>
        private static DgtConfig LoadConfigInternal(string configPath)
        {
            try
            {
                // Try to load from specified path or default location
                var path = GetConfigPath(configPath);

                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var config = JsonSerializer.Deserialize<DgtConfig>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });

                    Log.Information("Configuration loaded from {Path}", path);
                    return config;
                }
                else
                {
                    // Create default config
                    var config = new DgtConfig();
                    SaveConfig(config, path);
                    Log.Information("Default configuration created at {Path}", path);
                    return config;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load configuration, using defaults");
                return new DgtConfig();
            }
        }

        /// <summary>
        /// Saves configuration to JSON file
        /// </summary>
        public static void SaveConfig(DgtConfig config, string configPath = null)
        {
            try
            {
                var path = GetConfigPath(configPath);
                var directory = Path.GetDirectoryName(path);

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                File.WriteAllText(path, json);
                Log.Debug("Configuration saved to {Path}", path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save configuration");
            }
        }

        /// <summary>
        /// Gets the configuration file path
        /// </summary>
        private static string GetConfigPath(string configPath)
        {
            if (!string.IsNullOrWhiteSpace(configPath))
                return configPath;

            // Try current directory first (for development)
            var currentDir = Path.GetDirectoryName(typeof(ConfigManager).Assembly.Location);
            var currentDirConfig = Path.Combine(currentDir, DefaultConfigFileName);

            if (File.Exists(currentDirConfig))
                return currentDirConfig;

            // Fall back to LocalApplicationData
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "DominoGovernanceTracker", DefaultConfigFileName);
        }

        /// <summary>
        /// Reloads configuration from disk (creates new Lazy instance)
        /// Thread-safe: locks to prevent race condition during reassignment
        /// </summary>
        public static DgtConfig ReloadConfig(string configPath = null)
        {
            // Reset the lazy instance to force reload (thread-safe with lock)
            lock (_configLock)
            {
                _configLazy = new Lazy<DgtConfig>(() => LoadConfigInternal(null),
                    System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
            }
            return LoadConfig(configPath);
        }

        /// <summary>
        /// Gets current configuration without reloading
        /// </summary>
        public static DgtConfig GetCurrentConfig()
        {
            return LoadConfig();
        }
    }
}
