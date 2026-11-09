// Avoid touching real user settings or startup registry in the regression harness.
namespace Beam.Services;
internal sealed class BeamSettings
{
    public static BeamSettings Current { get; } = new();
    public string GetApiKeyForProvider(string provider) => "";
    public static string InferProviderFromModel(string? model) => model switch
    {
        "gpt-test" or "openai" => "openai",
        "claude-test" or "anthropic" => "anthropic",
        "llama-test" or "groq" => "groq",
        _ => "gemini"
    };
}
