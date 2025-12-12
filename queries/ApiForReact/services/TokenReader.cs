using System;
using System.IO;
using System.Text.Json;

namespace queries.services.TokenReader;

public class TokenReader
{
    private class BotSettings
    {
        public string SupabaseUrl { get; set; } = string.Empty;
        public string SupabaseKey { get; set; } = string.Empty;
    }

    private class TokensFile
    {
        public BotSettings BotSettings { get; set; } = new();
    }

    private readonly BotSettings _settings;

    public TokenReader(string? filePath = null)
    {
        // Use provided path, or default to tokens.json in the services directory
        filePath ??= Path.Combine(AppContext.BaseDirectory, "services", "tokens.json");

        TokensFile tokens;
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            tokens = JsonSerializer.Deserialize<TokensFile>(json)
                     ?? throw new Exception("Failed to parse tokens file.");
        }
        else
        {
            tokens = new TokensFile();
        }

        static string? GetEnv(params string[] names)
        {
            foreach (var n in names)
            {
                var v = Environment.GetEnvironmentVariable(n);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        var bot = tokens.BotSettings ?? new BotSettings();

        // Prefer environment variables; support a few common names. Trim whitespace/newlines.
        bot.SupabaseUrl = (GetEnv("SupabaseUrl", "SUPABASE_URL", "BotSettings__SupabaseUrl") ?? bot.SupabaseUrl)?.Trim();
        bot.SupabaseKey = (GetEnv("SupabaseKey", "SUPABASE_KEY", "BotSettings__SupabaseKey") ?? bot.SupabaseKey)?.Trim();

        _settings = bot;
    }

    public string GetSupabaseUrl() => _settings.SupabaseUrl;
    public string GetSupabaseKey() => _settings.SupabaseKey;
}