using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PokeStudio.Core.Safety;

namespace PokeStudio.Core.Storage;

public static class JsonStore
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
    }

    /// <summary>Atomic write confined to the file's own folder.</summary>
    public static Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var writer = new SafeFileWriter(Path.GetDirectoryName(full)!);
        return writer.WriteAllTextAsync(full, JsonSerializer.Serialize(value, Options), cancellationToken);
    }
}

public static class Hashing
{
    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}
