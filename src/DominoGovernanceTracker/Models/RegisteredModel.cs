using System;
using System.Text.Json.Serialization;

namespace DominoGovernanceTracker.Models
{
    /// <summary>
    /// Represents a registered workbook model returned from the API
    /// </summary>
    public class RegisteredModel
    {
        [JsonPropertyName("modelId")]
        public string ModelId { get; set; }

        [JsonPropertyName("modelName")]
        public string ModelName { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("registeredBy")]
        public string RegisteredBy { get; set; }

        [JsonPropertyName("machineName")]
        public string MachineName { get; set; }

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Request payload for model registration
    /// </summary>
    public class ModelRegistrationRequest
    {
        [JsonPropertyName("modelName")]
        public string ModelName { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("registeredBy")]
        public string RegisteredBy { get; set; }

        [JsonPropertyName("machineName")]
        public string MachineName { get; set; }

        [JsonPropertyName("existingModelId")]
        public string ExistingModelId { get; set; }
    }
}
