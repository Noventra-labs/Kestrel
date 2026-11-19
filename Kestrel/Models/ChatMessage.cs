using System.Text.Json.Serialization;

namespace Kestrel.Models;

public sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);
