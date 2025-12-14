using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using DominoGovernanceTracker.Models;
using Serilog;

namespace DominoGovernanceTracker.Publishing
{
    /// <summary>
    /// Local JSON Lines buffer for events that failed to send to API
    /// Provides durability and retry capability when API is unavailable
    /// </summary>
    public class LocalBuffer : IDisposable
    {
        private readonly string _bufferPath;
        private readonly long _maxFileSizeBytes;
        private readonly object _fileLock = new object();
        private long _eventsBuffered;

        public LocalBuffer(string bufferPath, int maxFileSizeMB = 50)
        {
            _bufferPath = bufferPath;
            _maxFileSizeBytes = maxFileSizeMB * 1024L * 1024L;

            // Ensure directory exists
            var directory = Path.GetDirectoryName(_bufferPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Log.Information("Local buffer initialized at {Path}", _bufferPath);
        }

        /// <summary>
        /// Appends events to the buffer file (JSON Lines format)
        /// </summary>
        public bool AppendEvents(IEnumerable<AuditEvent> events)
        {
            if (events == null)
                return false;

            lock (_fileLock)
            {
                try
                {
                    // Check file size before appending
                    if (File.Exists(_bufferPath))
                    {
                        var fileInfo = new FileInfo(_bufferPath);
                        if (fileInfo.Length > _maxFileSizeBytes)
                        {
                            RotateBufferFile();
                        }
                    }

                    // Append events in JSON Lines format (one JSON object per line)
                    using (var writer = new StreamWriter(_bufferPath, append: true))
                    {
                        foreach (var evt in events)
                        {
                            var json = JsonSerializer.Serialize(evt, new JsonSerializerOptions
                            {
                                WriteIndented = false  // Single line per event
                            });
                            writer.WriteLine(json);
                            Interlocked.Increment(ref _eventsBuffered);
                        }
                    }

                    Log.Debug("Appended events to local buffer");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to append events to local buffer");
                    return false;
                }
            }
        }

        /// <summary>
        /// Reads all buffered events from the file
        /// </summary>
        public List<AuditEvent> ReadEvents()
        {
            var events = new List<AuditEvent>();

            lock (_fileLock)
            {
                try
                {
                    if (!File.Exists(_bufferPath))
                        return events;

                    using (var reader = new StreamReader(_bufferPath))
                    {
                        string line;
                        int lineNumber = 0;
                        while ((line = reader.ReadLine()) != null)
                        {
                            lineNumber++;
                            if (string.IsNullOrWhiteSpace(line))
                                continue;

                            try
                            {
                                var evt = JsonSerializer.Deserialize<AuditEvent>(line);
                                if (evt != null)
                                {
                                    events.Add(evt);
                                }
                            }
                            catch (JsonException jex)
                            {
                                Log.Warning(jex, "Failed to deserialize buffered event at line {Line}", lineNumber);
                            }
                        }
                    }

                    Log.Debug("Read {Count} events from local buffer", events.Count);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to read events from local buffer");
                }
            }

            return events;
        }

        /// <summary>
        /// Deletes the buffer file after successful processing
        /// </summary>
        public bool ClearBuffer()
        {
            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(_bufferPath))
                    {
                        File.Delete(_bufferPath);
                        Log.Information("Local buffer cleared");
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to clear local buffer");
                    return false;
                }
            }
        }

        /// <summary>
        /// Rotates the buffer file when it exceeds max size
        /// </summary>
        private void RotateBufferFile()
        {
            try
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var rotatedPath = _bufferPath.Replace(".jsonl", $"_{timestamp}.jsonl");

                File.Move(_bufferPath, rotatedPath);
                Log.Warning("Buffer file rotated to {Path} (exceeded max size)", rotatedPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to rotate buffer file");
            }
        }

        /// <summary>
        /// Gets the current buffer file size in bytes
        /// </summary>
        public long GetBufferSize()
        {
            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(_bufferPath))
                    {
                        return new FileInfo(_bufferPath).Length;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to get buffer size");
                }
            }
            return 0;
        }

        /// <summary>
        /// Checks if buffer file exists and has content
        /// </summary>
        public bool HasBufferedEvents()
        {
            lock (_fileLock)
            {
                try
                {
                    return File.Exists(_bufferPath) && new FileInfo(_bufferPath).Length > 0;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to check buffered events");
                    return false;
                }
            }
        }

        /// <summary>
        /// Gets total events buffered (lifetime counter)
        /// </summary>
        public long TotalEventsBuffered => Interlocked.Read(ref _eventsBuffered);

        /// <summary>
        /// Finds all rotated buffer files in the same directory
        /// </summary>
        public List<string> GetRotatedBufferFiles()
        {
            var rotatedFiles = new List<string>();

            lock (_fileLock)
            {
                try
                {
                    var directory = Path.GetDirectoryName(_bufferPath);
                    if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                        return rotatedFiles;

                    var bufferFileName = Path.GetFileNameWithoutExtension(_bufferPath);
                    var searchPattern = $"{bufferFileName}_*.jsonl";

                    var files = Directory.GetFiles(directory, searchPattern);
                    rotatedFiles.AddRange(files);

                    Log.Debug("Found {Count} rotated buffer files", rotatedFiles.Count);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to find rotated buffer files");
                }
            }

            return rotatedFiles;
        }

        /// <summary>
        /// Reads events from a specific buffer file (including rotated files)
        /// </summary>
        public List<AuditEvent> ReadEventsFromFile(string filePath)
        {
            var events = new List<AuditEvent>();

            lock (_fileLock)
            {
                try
                {
                    if (!File.Exists(filePath))
                        return events;

                    using (var reader = new StreamReader(filePath))
                    {
                        string line;
                        int lineNumber = 0;
                        while ((line = reader.ReadLine()) != null)
                        {
                            lineNumber++;
                            if (string.IsNullOrWhiteSpace(line))
                                continue;

                            try
                            {
                                var evt = JsonSerializer.Deserialize<AuditEvent>(line);
                                if (evt != null)
                                {
                                    events.Add(evt);
                                }
                            }
                            catch (JsonException jex)
                            {
                                Log.Warning(jex, "Failed to deserialize buffered event at line {Line} in {File}",
                                    lineNumber, Path.GetFileName(filePath));
                            }
                        }
                    }

                    Log.Debug("Read {Count} events from {File}", events.Count, Path.GetFileName(filePath));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to read events from {File}", filePath);
                }
            }

            return events;
        }

        /// <summary>
        /// Deletes a specific buffer file
        /// </summary>
        public bool DeleteFile(string filePath)
        {
            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        Log.Information("Deleted buffer file: {File}", Path.GetFileName(filePath));
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to delete buffer file: {File}", filePath);
                }
            }

            return false;
        }

        public void Dispose()
        {
            // Don't delete buffer on dispose - it's meant to persist
            Log.Debug("Local buffer disposed (file preserved)");
        }
    }
}
