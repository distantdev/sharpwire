using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sharpwire.Core.Secrets;

/// <summary>Reads API keys from environment, then user secrets; writes only to user secrets JSON.</summary>
public sealed class LlmApiKeyStore
{
    public const string GoogleConfigurationKey = "GoogleAI:ApiKey";
    public const string OpenAiConfigurationKey = "OpenAI:ApiKey";
    public const string AnthropicConfigurationKey = "Anthropic:ApiKey";
    
    private const string GoogleEnvVarName = "GOOGLE_AI_API_KEY";
    private const string OpenAiEnvVarName = "OPENAI_API_KEY";
    private const string AnthropicEnvVarName = "ANTHROPIC_API_KEY";

    /// <summary>Must match <c>UserSecretsId</c> in the project file.</summary>
    public const string UserSecretsId = "30c5c37e-4b8d-4a7a-9f1e-2c3d4e5f6a7b";

    private static string GetUserSecretsDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "UserSecrets", UserSecretsId);

    private static string GetSecretsJsonPath() =>
        Path.Combine(GetUserSecretsDirectory(), "secrets.json");

    public string? GetGoogleApiKey() => GetKey(GoogleEnvVarName, GoogleConfigurationKey);
    public string? GetOpenAiApiKey() => GetKey(OpenAiEnvVarName, OpenAiConfigurationKey);
    public string? GetAnthropicApiKey() => GetKey(AnthropicEnvVarName, AnthropicConfigurationKey);

    private string? GetKey(string envVar, string configKey)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        var path = GetSecretsJsonPath();
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var node = JsonNode.Parse(json)?.AsObject();
            if (node == null)
                return null;
            
            // Try flat key
            if (node.TryGetPropertyValue(configKey, out var v))
                return v?.GetValue<string>();

            // Try nested (e.g. "GoogleAI": { "ApiKey": "..." })
            var parts = configKey.Split(':');
            if (parts.Length == 2)
            {
                if (node.TryGetPropertyValue(parts[0], out var g) && g is JsonObject go
                    && go.TryGetPropertyValue(parts[1], out var ak))
                    return ak?.GetValue<string>();
            }
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    public void SetGoogleApiKey(string? key) => SetKey(GoogleConfigurationKey, key);
    public void SetOpenAiApiKey(string? key) => SetKey(OpenAiConfigurationKey, key);
    public void SetAnthropicApiKey(string? key) => SetKey(AnthropicConfigurationKey, key);

    private void SetKey(string configKey, string? apiKey)
    {
        var dir = GetUserSecretsDirectory();
        var path = GetSecretsJsonPath();
        Directory.CreateDirectory(dir);

        JsonObject root;
        if (File.Exists(path))
        {
            try
            {
                var existing = File.ReadAllText(path);
                root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
            }
            catch
            {
                root = new JsonObject();
            }
        }
        else
            root = new JsonObject();

        if (string.IsNullOrWhiteSpace(apiKey))
            root.Remove(configKey);
        else
            root[configKey] = apiKey.Trim();

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, root.ToJsonString(opts));
    }

    /// <summary>One-time migration from legacy workspace settings.json.</summary>
    public static bool TryMigrateKeyFromSettingsJson(string settingsJsonPath, LlmApiKeyStore store)
    {
        if (!File.Exists(settingsJsonPath))
            return false;

        string json;
        try
        {
            json = File.ReadAllText(settingsJsonPath);
        }
        catch
        {
            return false;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject();
        }
        catch
        {
            return false;
        }

        if (root == null || !root.TryGetPropertyValue("GoogleApiKey", out var keyNode))
            return false;

        var key = keyNode?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                store.SetGoogleApiKey(key);
            }
            catch
            {
                return false;
            }
        }

        root.Remove("GoogleApiKey");
        try
        {
            File.WriteAllText(settingsJsonPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            /* ignore */
        }

        return true;
    }
}
