using System.Text.Json;
using LinguaFlow.Core;

namespace LinguaFlow.Infrastructure;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsPath;

    public JsonSettingsService()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("LINGUAFLOW_DATA_DIR");
        var dataDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : configuredDirectory;
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
    }

    public async Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new ApplicationSettings();
        }

        await using var stream = File.OpenRead(_settingsPath);
        return await JsonSerializer.DeserializeAsync<ApplicationSettings>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? new ApplicationSettings();
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await using var stream = new FileStream(_settingsPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }
}
