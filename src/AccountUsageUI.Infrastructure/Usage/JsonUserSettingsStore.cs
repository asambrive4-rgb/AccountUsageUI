using System.Text.Json;
using AccountUsageUI.Core.Usage;
using AccountUsageUI.Infrastructure.Profiles;

namespace AccountUsageUI.Infrastructure.Usage;

public sealed class JsonUserSettingsStore : IUserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonUserSettingsStore()
        : this(CreateDefaultPath())
    {
    }

    public JsonUserSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    private static readonly HashSet<string> LegacyDefaultModelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gemini 3.8 Flash",
        "Gemini 3.1 Pro",
        "Gemini 3.5 Flash",
        "Gemini 3 Flash",
        "Gemini 2.5 Pro",
        "Claude Sonnet 5.5",
        "Claude Opus 5.5",
        "Claude Sonnet 4.6",
        "Claude Opus 4.6",
        "GPT-OSS 120B"
    };

    public async Task<AppSettings> LoadAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return AppSettings.Default;
            }

            await using var stream = File.OpenRead(_filePath);
            var dto = await JsonSerializer.DeserializeAsync<SettingsDto>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (dto?.FavoriteModels is null || dto.FavoriteModels.Count == 0)
            {
                return AppSettings.Default;
            }

            var cleaned = dto.FavoriteModels
                .Where(m => !string.IsNullOrWhiteSpace(m) && !LegacyDefaultModelNames.Contains(m.Trim()))
                .Select(m => m.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (cleaned.Length == 0 ||
                (cleaned.Length == 1 && StringComparer.OrdinalIgnoreCase.Equals(cleaned[0], "Gemini 4 Argon")))
            {
                return AppSettings.Default;
            }

            return new AppSettings(cleaned);
        }
        catch
        {
            return AppSettings.Default;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dto = new SettingsDto
            {
                Version = 1,
                FavoriteModels = settings.FavoriteModels
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
            await AtomicFileWriter.WriteAsync(
                _filePath,
                bytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 설정 저장 실패가 앱 실행을 중단시키면 안 된다.
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CreateDefaultPath()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(
            localAppData,
            "AccountUsageUI",
            "user-settings.v1.json");
    }

    private sealed class SettingsDto
    {
        public int Version { get; set; }

        public List<string>? FavoriteModels { get; set; }
    }
}
