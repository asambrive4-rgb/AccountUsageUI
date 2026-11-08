using System.Text.Json;
using System.Text.Json.Serialization;
using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Core.Usage;
using AccountUsageUI.Infrastructure.Profiles;

namespace AccountUsageUI.Infrastructure.Usage;

public sealed class FileProfileUsageSnapshotCache
    : IProfileUsageSnapshotCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly Dictionary<ProfileId, CachedProfileUsageEntry>
        _entries = [];

    public FileProfileUsageSnapshotCache()
        : this(CreateDefaultPath())
    {
    }

    public FileProfileUsageSnapshotCache(string filePath)
    {
        _filePath = filePath;
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var loaded = await Task.Run(
                ReadFromDisk,
                cancellationToken)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _entries.Clear();
            foreach (var entry in loaded)
            {
                _entries[entry.ProfileId] = entry;
            }
        }
    }

    public bool TryGet(
        ProfileId profileId,
        out CachedProfileUsageEntry entry)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(profileId, out entry!);
        }
    }

    public async Task SetAsync(
        CachedProfileUsageEntry entry,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _entries[entry.ProfileId] = entry;
        }

        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(
        ProfileId profileId,
        CancellationToken cancellationToken)
    {
        var changed = false;
        lock (_gate)
        {
            changed = _entries.Remove(profileId);
        }

        if (changed)
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RemoveMissingAsync(
        IReadOnlyCollection<ProfileId> keep,
        CancellationToken cancellationToken)
    {
        var keepSet = keep.ToHashSet();
        var changed = false;
        lock (_gate)
        {
            foreach (var id in _entries.Keys
                         .Where(id => !keepSet.Contains(id))
                         .ToArray())
            {
                _entries.Remove(id);
                changed = true;
            }
        }

        if (changed)
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        CachedProfileUsageEntry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries.Values.ToArray();
        }

        await _persistGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await WriteToDiskAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private IReadOnlyList<CachedProfileUsageEntry> ReadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            using var stream = File.OpenRead(_filePath);
            var dto = JsonSerializer.Deserialize<CacheFileDto>(
                stream,
                JsonOptions);
            if (dto?.Entries is null || dto.Version != 1)
            {
                return [];
            }

            var result = new List<CachedProfileUsageEntry>();
            foreach (var item in dto.Entries)
            {
                if (!ProfileId.TryParse(item.ProfileId, out var profileId))
                {
                    continue;
                }

                if (!Enum.TryParse<ProfileQuotaStatus>(
                        item.Status,
                        ignoreCase: true,
                        out var status) ||
                    status == ProfileQuotaStatus.Loading)
                {
                    continue;
                }

                var models = item.Models?
                    .Where(m => !string.IsNullOrWhiteSpace(m.DisplayName))
                    .Select(
                        m => new ModelQuotaWindow(
                            m.ModelId ?? m.DisplayName!,
                            m.DisplayName!,
                            m.RemainingFraction,
                            m.ResetsAt,
                            m.FamilyGroup,
                            m.Window,
                            m.Description,
                            m.Disabled))
                    .ToArray() ?? [];

                result.Add(
                    new CachedProfileUsageEntry(
                        profileId,
                        status,
                        models,
                        item.AccountEmail,
                        item.AccountPlan,
                        item.LastSelectedModelId,
                        item.MostRecentlyUsedModelId,
                        item.LastSuccessfulAt,
                        item.CapturedAt));
            }

            return result;
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  JsonException or
                  NotSupportedException)
        {
            return [];
        }
    }

    private async Task WriteToDiskAsync(
        IReadOnlyList<CachedProfileUsageEntry> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            var dto = new CacheFileDto
            {
                Version = 1,
                Entries = entries
                    .Where(entry => entry.Status != ProfileQuotaStatus.Loading)
                    .Select(
                        entry => new CacheEntryDto
                        {
                            ProfileId = entry.ProfileId.ToString(),
                            Status = entry.Status.ToString(),
                            Models = entry.Models
                                .Select(
                                    m => new ModelWindowDto
                                    {
                                        ModelId = m.ModelId,
                                        DisplayName = m.DisplayName,
                                        RemainingFraction = m.RemainingFraction,
                                        ResetsAt = m.ResetsAt,
                                        FamilyGroup = m.FamilyGroup,
                                        Window = m.Window,
                                        Description = m.Description,
                                        Disabled = m.Disabled
                                    })
                                .ToList(),
                            AccountEmail = entry.AccountEmail,
                            AccountPlan = entry.AccountPlan,
                            LastSelectedModelId = entry.LastSelectedModelId,
                            MostRecentlyUsedModelId = entry.MostRecentlyUsedModelId,
                            LastSuccessfulAt = entry.LastSuccessfulAt,
                            CapturedAt = entry.CapturedAt
                        })
                    .ToList()
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                dto,
                JsonOptions);
            await AtomicFileWriter.WriteAsync(
                    _filePath,
                    bytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  JsonException)
        {
            // 캐시 저장 실패가 앱 동작을 막으면 안 된다.
        }
    }

    private static string CreateDefaultPath()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(
            localAppData,
            "AccountUsageUI",
            "usage-snapshot-cache.v1.json");
    }

    private sealed class CacheFileDto
    {
        public int Version { get; set; }

        public List<CacheEntryDto>? Entries { get; set; }
    }

    private sealed class CacheEntryDto
    {
        public string? ProfileId { get; set; }

        public string? Status { get; set; }

        public List<ModelWindowDto>? Models { get; set; }

        public string? AccountEmail { get; set; }

        public string? AccountPlan { get; set; }

        public string? LastSelectedModelId { get; set; }

        public string? MostRecentlyUsedModelId { get; set; }

        public DateTimeOffset? LastSuccessfulAt { get; set; }

        public DateTimeOffset CapturedAt { get; set; }
    }

    private sealed class ModelWindowDto
    {
        public string? ModelId { get; set; }

        public string? DisplayName { get; set; }

        public double RemainingFraction { get; set; }

        public DateTimeOffset? ResetsAt { get; set; }

        public string? FamilyGroup { get; set; }

        public string? Window { get; set; }

        public string? Description { get; set; }

        public bool Disabled { get; set; }
    }
}
