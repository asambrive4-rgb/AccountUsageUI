namespace AccountUsageUI.Core.Usage;

public sealed record AppSettings(
    IReadOnlyList<string> FavoriteModels)
{
    public static AppSettings Default =>
        new(FavoriteModelsPolicy.DefaultFavoriteModels);
}

public interface IUserSettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken);
}

public static class FavoriteModelsPolicy
{
    /// <summary>
    /// Antigravity 기본 Usage 팝업의 기본 그룹 목록 (서버 RetrieveUserQuotaSummary의 기본 그룹명과 일치).
    /// 서버가 새로운 그룹을 반환하면 동적으로 감지되어 함께 표시된다.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultFavoriteModels =
    [
        "Gemini Models",
        "Claude and GPT models",
        "Gemini 4 Argon"
    ];

    public static readonly IReadOnlyList<string> CatalogModels =
    [
        "Gemini Models",
        "Claude and GPT models",
        "Gemini 4 Argon"
    ];

    /// <summary>
    /// 모델/그룹 표시 이름에서 (High), (Medium), (Low), (Thinking) 등 사고 수준 접미사를 제거해
    /// 패밀리/그룹 이름을 정규화한다.
    /// </summary>
    public static string NormalizeFamilyName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "";
        }

        var trimmed = displayName.Trim();
        var openParen = trimmed.LastIndexOf('(');
        if (openParen > 0 && trimmed.EndsWith(')'))
        {
            var suffix = trimmed[(openParen + 1)..^1].Trim();
            if (IsTierSuffix(suffix))
            {
                return trimmed[..openParen].TrimEnd();
            }
        }

        return trimmed;
    }

    /// <summary>
    /// 전체 버킷/모델 목록에서 사용자가 선택한 주요 그룹/모델(FavoriteModels)에 해당하는 항목들을 반환한다.
    /// - 버킷 구조(FamilyGroup이 지정되고 Window가 있는 경우)에서는 동일 그룹 내 여러 버킷(Weekly, 5h 등)을 모두 보존한다.
    /// - 개별 모델 목록(Window가 없는 경우)에서는 동일 패밀리(High/Medium/Low)를 하나로 병합한다.
    /// - 만약 서버가 새로운 그룹 이름을 반환해 기존 기본값과 매칭되지 않으면 서버가 반환한 전체 그룹을 자동으로 표시한다.
    /// </summary>
    public static IReadOnlyList<ModelQuotaWindow> SelectFavoriteModels(
        IReadOnlyList<ModelQuotaWindow> allModels,
        IReadOnlyList<string> favoriteFilters,
        bool groupByFamily = true)
    {
        if (allModels.Count == 0)
        {
            return [];
        }

        var isBucketSummaryMode = allModels.Any(m => !string.IsNullOrWhiteSpace(m.Window));
        var candidates = !isBucketSummaryMode && groupByFamily
            ? CollapseByFamily(allModels)
            : allModels;

        var activeFilters = favoriteFilters
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToArray();

        if (activeFilters.Length == 0)
        {
            return candidates;
        }

        var selected = new List<ModelQuotaWindow>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filter in activeFilters)
        {
            foreach (var model in candidates)
            {
                var uniqueKey = $"{model.FamilyGroup}::{model.ModelId}::{model.Window}";
                if (usedKeys.Contains(uniqueKey))
                {
                    continue;
                }

                if (MatchesFilter(model, filter))
                {
                    selected.Add(model);
                    usedKeys.Add(uniqueKey);
                }
            }
        }

        if (selected.Count > 0)
        {
            // 기본 그룹 설정 상태에서 서버가 새로운 그룹(예: 추후 추가된 그룹)을 내려준 경우 자동 포함
            if (IsUsingDefaultFilters(activeFilters) && isBucketSummaryMode)
            {
                foreach (var model in candidates)
                {
                    var uniqueKey = $"{model.FamilyGroup}::{model.ModelId}::{model.Window}";
                    if (!usedKeys.Contains(uniqueKey))
                    {
                        selected.Add(model);
                        usedKeys.Add(uniqueKey);
                    }
                }
            }

            return selected;
        }

        return candidates;
    }

    /// <summary>
    /// 미니 팝업(오버레이)에 표시할 '최근 사용 모델/버킷'을 결정한다.
    /// 우선순위:
    /// 1) 이전 스냅샷 대비 남은 쿼터가 감소한 버킷/모델
    /// 2) 스냅샷에 기록된 MostRecentlyUsedModelId / LastSelectedModelId 매칭 항목
    /// 3) 남은 비율이 100% 미만인 항목 중 가장 적게 남은 항목 (예: Gemini 4 Argon 88% left)
    /// 4) 첫 번째 주요 항목
    /// </summary>
    public static ModelQuotaWindow? SelectRecentlyUsedModel(
        ProfileQuotaSnapshot currentSnapshot,
        ProfileQuotaSnapshot? previousSnapshot,
        IReadOnlyList<string> favoriteFilters)
    {
        var allModels = currentSnapshot.ModelList;
        if (allModels.Count == 0)
        {
            return null;
        }

        var isBucketSummaryMode = allModels.Any(m => !string.IsNullOrWhiteSpace(m.Window));
        var candidates = isBucketSummaryMode
            ? allModels
            : CollapseByFamily(allModels);

        // 1. 이전 스냅샷 대비 남은 비율이 실제로 줄어든 항목 탐지
        if (previousSnapshot is not null && previousSnapshot.ModelList.Count > 0)
        {
            var prevByKey = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in previousSnapshot.ModelList)
            {
                var key = GetComparisonKey(model);
                if (!prevByKey.TryGetValue(key, out var existingFraction) || model.RemainingFraction < existingFraction)
                {
                    prevByKey[key] = model.RemainingFraction;
                }
            }

            ModelQuotaWindow? decremented = null;
            var maxDrop = 0.0001;
            foreach (var model in candidates)
            {
                var key = GetComparisonKey(model);
                if (prevByKey.TryGetValue(key, out var prevFraction))
                {
                    var drop = prevFraction - model.RemainingFraction;
                    if (drop > maxDrop)
                    {
                        maxDrop = drop;
                        decremented = FormatForPopupDisplay(model);
                    }
                }
            }

            if (decremented is not null)
            {
                return decremented;
            }
        }

        // 2. MostRecentlyUsedModelId 또는 LastSelectedModelId 매칭
        foreach (var targetId in new[]
                 {
                     currentSnapshot.MostRecentlyUsedModelId,
                     currentSnapshot.LastSelectedModelId
                 })
        {
            if (string.IsNullOrWhiteSpace(targetId))
            {
                continue;
            }

            var exact = candidates.FirstOrDefault(
                m => StringComparer.OrdinalIgnoreCase.Equals(m.ModelId, targetId) ||
                     StringComparer.OrdinalIgnoreCase.Equals(m.DisplayName, targetId) ||
                     StringComparer.OrdinalIgnoreCase.Equals(m.FamilyGroup, targetId));
            if (exact is not null)
            {
                return FormatForPopupDisplay(exact);
            }
        }

        // 3. 주요 항목 중 사용 흔적(남은 비율 < 100%)이 있는 항목 우선
        var favorites = SelectFavoriteModels(allModels, favoriteFilters);
        var usedFavorite = favorites
            .Where(m => m.RemainingFraction < 0.995)
            .OrderBy(m => m.RemainingFraction)
            .FirstOrDefault();
        if (usedFavorite is not null)
        {
            return FormatForPopupDisplay(usedFavorite);
        }

        // 4. 전체 항목 중 사용 흔적이 있는 항목
        var usedAny = candidates
            .Where(m => m.RemainingFraction < 0.995)
            .OrderBy(m => m.RemainingFraction)
            .FirstOrDefault();
        if (usedAny is not null)
        {
            return FormatForPopupDisplay(usedAny);
        }

        var first = favorites.FirstOrDefault() ?? candidates.FirstOrDefault() ?? allModels.FirstOrDefault();
        return first is not null ? FormatForPopupDisplay(first) : null;
    }

    public static IReadOnlyList<ModelQuotaWindow> CollapseByFamily(
        IReadOnlyList<ModelQuotaWindow> models)
    {
        var result = new List<ModelQuotaWindow>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in models)
        {
            var familyName = !string.IsNullOrWhiteSpace(model.FamilyGroup)
                ? model.FamilyGroup.Trim()
                : NormalizeFamilyName(model.DisplayName);
            if (string.IsNullOrWhiteSpace(familyName))
            {
                continue;
            }

            if (!indexByKey.TryGetValue(familyName, out var existingIndex))
            {
                indexByKey[familyName] = result.Count;
                result.Add(
                    model with
                    {
                        DisplayName = familyName,
                        FamilyGroup = familyName
                    });
                continue;
            }

            var existing = result[existingIndex];
            if (model.RemainingFraction < existing.RemainingFraction)
            {
                result[existingIndex] = model with
                {
                    DisplayName = familyName,
                    FamilyGroup = familyName
                };
            }
        }

        return result;
    }

    private static ModelQuotaWindow FormatForPopupDisplay(ModelQuotaWindow model)
    {
        if (!string.IsNullOrWhiteSpace(model.FamilyGroup))
        {
            return model with
            {
                DisplayName = model.FamilyGroup
            };
        }

        return model;
    }

    private static string GetComparisonKey(ModelQuotaWindow model)
    {
        if (!string.IsNullOrWhiteSpace(model.Window))
        {
            return $"{model.FamilyGroup ?? model.DisplayName}::{model.ModelId}::{model.Window}";
        }

        if (!string.IsNullOrWhiteSpace(model.ModelId))
        {
            return $"{model.FamilyGroup ?? NormalizeFamilyName(model.DisplayName)}::{model.ModelId}";
        }

        return NormalizeFamilyName(model.DisplayName);
    }

    private static bool IsUsingDefaultFilters(IReadOnlyList<string> activeFilters)
    {
        if (activeFilters.Count != DefaultFavoriteModels.Count)
        {
            return false;
        }

        for (var i = 0; i < DefaultFavoriteModels.Count; i++)
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(activeFilters[i], DefaultFavoriteModels[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesFilter(
        ModelQuotaWindow model,
        string filter)
    {
        var groupName = model.FamilyGroup ?? "";
        var family = NormalizeFamilyName(model.DisplayName);

        if (StringComparer.OrdinalIgnoreCase.Equals(groupName, filter) ||
            StringComparer.OrdinalIgnoreCase.Equals(family, filter) ||
            StringComparer.OrdinalIgnoreCase.Equals(model.DisplayName, filter) ||
            StringComparer.OrdinalIgnoreCase.Equals(model.ModelId, filter))
        {
            return true;
        }

        return groupName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               family.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               model.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTierSuffix(string suffix)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(suffix, "Low") ||
               StringComparer.OrdinalIgnoreCase.Equals(suffix, "Medium") ||
               StringComparer.OrdinalIgnoreCase.Equals(suffix, "High") ||
               StringComparer.OrdinalIgnoreCase.Equals(suffix, "Max") ||
               StringComparer.OrdinalIgnoreCase.Equals(suffix, "Thinking");
    }
}
