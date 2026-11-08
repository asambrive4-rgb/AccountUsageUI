using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AccountUsageUI.Core.Usage;

namespace AccountUsageUI.Infrastructure.Providers.Antigravity;

public static partial class AntigravityQuotaResponseParser
{
    /// <summary>
    /// Antigravity의 RetrieveUserQuotaSummary (로컬 Connect RPC 또는 CloudCode /v1internal:retrieveUserQuotaSummary)
    /// JSON 응답을 파싱하여 그룹/버킷 구조의 ModelQuotaWindow 목록으로 변환한다.
    /// </summary>
    public static ProfileQuotaReadResult ParseRetrieveUserQuotaSummaryJson(
        string json,
        string? accountEmail = null,
        string? accountPlan = null,
        string? externalLastSelectedModelId = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // Connect RPC는 {"response": {"groups": [...]}} 형태이고,
            // CloudCode 직접 호출은 {"groups": [...]} 형태일 수 있으므로 둘 다 지원한다.
            var payload = root;
            if (root.TryGetProperty("response", out var responseObj) &&
                responseObj.ValueKind == JsonValueKind.Object)
            {
                payload = responseObj;
            }

            if (!payload.TryGetProperty("groups", out var groupsArr) ||
                groupsArr.ValueKind != JsonValueKind.Array)
            {
                return new ProfileQuotaReadResult(
                    ProfileQuotaStatus.ResponseFormatChanged,
                    []);
            }

            var models = new List<ModelQuotaWindow>();

            foreach (var group in groupsArr.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var groupDisplayName = TryGetString(group, "displayName")?.Trim();
                if (!group.TryGetProperty("buckets", out var bucketsArr) ||
                    bucketsArr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var bucket in bucketsArr.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var bucketId = TryGetString(bucket, "bucketId") ?? "";
                    var bucketDisplayName = TryGetString(bucket, "displayName") ?? bucketId;
                    var bucketDescription = TryGetString(bucket, "description");
                    var window = TryGetString(bucket, "window");
                    var disabled = bucket.TryGetProperty("disabled", out var disProp) &&
                                   disProp.ValueKind == JsonValueKind.True;

                    var fraction = 1.0;
                    if (bucket.TryGetProperty("remainingFraction", out var remProp) &&
                        remProp.ValueKind == JsonValueKind.Number &&
                        remProp.TryGetDouble(out var parsedFraction))
                    {
                        fraction = Math.Clamp(parsedFraction, 0.0, 1.0);
                    }
                    else if (bucket.TryGetProperty("remaining", out var remOneof) &&
                             remOneof.ValueKind == JsonValueKind.Object &&
                             remOneof.TryGetProperty("remainingFraction", out var innerRem) &&
                             innerRem.ValueKind == JsonValueKind.Number &&
                             innerRem.TryGetDouble(out var parsedInner))
                    {
                        fraction = Math.Clamp(parsedInner, 0.0, 1.0);
                    }

                    DateTimeOffset? resetsAt = null;
                    var resetTimeStr = TryGetString(bucket, "resetTime");
                    if (!string.IsNullOrWhiteSpace(resetTimeStr) &&
                        DateTimeOffset.TryParse(
                            resetTimeStr,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var parsedReset))
                    {
                        resetsAt = parsedReset;
                    }

                    var effectiveGroupName = !string.IsNullOrWhiteSpace(groupDisplayName)
                        ? groupDisplayName
                        : UsageDisplaySettings.FormatWindowLabel(window, bucketDisplayName);

                    models.Add(
                        new ModelQuotaWindow(
                            ModelId: string.IsNullOrWhiteSpace(bucketId) ? effectiveGroupName : bucketId,
                            DisplayName: bucketDisplayName,
                            RemainingFraction: fraction,
                            ResetsAt: resetsAt,
                            FamilyGroup: effectiveGroupName,
                            Window: window,
                            Description: bucketDescription,
                            Disabled: disabled));
                }
            }

            if (models.Count == 0)
            {
                return new ProfileQuotaReadResult(
                    ProfileQuotaStatus.ResponseFormatChanged,
                    []);
            }

            return new ProfileQuotaReadResult(
                ProfileQuotaStatus.Available,
                models,
                AccountEmail: accountEmail,
                AccountPlan: accountPlan,
                LastSelectedModelId: externalLastSelectedModelId,
                MostRecentlyUsedModelId: externalLastSelectedModelId);
        }
        catch (JsonException)
        {
            return new ProfileQuotaReadResult(
                ProfileQuotaStatus.ResponseFormatChanged,
                []);
        }
    }

    /// <summary>
    /// 로컬 language_server의 GetUserStatus Connect RPC JSON 응답을 파싱한다.
    /// </summary>
    public static ProfileQuotaReadResult ParseGetUserStatusJson(
        string json,
        string? externalLastSelectedModelId = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("userStatus", out var userStatus) ||
                userStatus.ValueKind != JsonValueKind.Object)
            {
                return new ProfileQuotaReadResult(
                    ProfileQuotaStatus.ResponseFormatChanged,
                    []);
            }

            var email = TryGetString(userStatus, "email");
            string? planName = null;
            if (userStatus.TryGetProperty("userTier", out var userTier) &&
                userTier.ValueKind == JsonValueKind.Object)
            {
                planName = TryGetString(userTier, "name");
            }

            if (string.IsNullOrWhiteSpace(planName) &&
                userStatus.TryGetProperty("planStatus", out var planStatus) &&
                planStatus.ValueKind == JsonValueKind.Object &&
                planStatus.TryGetProperty("planInfo", out var planInfo) &&
                planInfo.ValueKind == JsonValueKind.Object)
            {
                planName = TryGetString(planInfo, "planName");
            }

            string? defaultOverrideModelId = null;
            var models = new List<ModelQuotaWindow>();

            if (userStatus.TryGetProperty(
                    "cascadeModelConfigData",
                    out var cascadeData) &&
                cascadeData.ValueKind == JsonValueKind.Object)
            {
                if (cascadeData.TryGetProperty(
                        "defaultOverrideModelConfig",
                        out var overrideConfig) &&
                    overrideConfig.ValueKind == JsonValueKind.Object)
                {
                    defaultOverrideModelId = ExtractModelOrAlias(overrideConfig);
                }

                if (cascadeData.TryGetProperty(
                        "clientModelConfigs",
                        out var configs) &&
                    configs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in configs.EnumerateArray())
                    {
                        var label = TryGetString(item, "label");
                        if (string.IsNullOrWhiteSpace(label))
                        {
                            continue;
                        }

                        var modelId = ExtractModelOrAlias(item) ?? label;
                        var (fraction, resetsAt) = ExtractQuotaInfo(item);
                        models.Add(
                            new ModelQuotaWindow(
                                modelId,
                                label,
                                fraction,
                                resetsAt,
                                FavoriteModelsPolicy.NormalizeFamilyName(label)));
                    }
                }
            }

            if (models.Count == 0)
            {
                return new ProfileQuotaReadResult(
                    ProfileQuotaStatus.ResponseFormatChanged,
                    [],
                    AccountEmail: email,
                    AccountPlan: planName);
            }

            var selectedId = !string.IsNullOrWhiteSpace(externalLastSelectedModelId)
                ? externalLastSelectedModelId
                : defaultOverrideModelId;

            return new ProfileQuotaReadResult(
                ProfileQuotaStatus.Available,
                models,
                AccountEmail: email,
                AccountPlan: planName,
                LastSelectedModelId: selectedId,
                MostRecentlyUsedModelId: selectedId);
        }
        catch (JsonException)
        {
            return new ProfileQuotaReadResult(
                ProfileQuotaStatus.ResponseFormatChanged,
                []);
        }
    }

    /// <summary>
    /// CloudCode /v1internal:fetchAvailableModels 직접 HTTP JSON 응답을 파싱한다.
    /// </summary>
    public static ProfileQuotaReadResult ParseFetchAvailableModelsJson(
        string json,
        string? accountEmail = null,
        string? accountPlan = null,
        string? externalLastSelectedModelId = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("models", out var modelsObj) ||
                modelsObj.ValueKind != JsonValueKind.Object)
            {
                return new ProfileQuotaReadResult(
                    ProfileQuotaStatus.ResponseFormatChanged,
                    []);
            }

            var defaultModelId = TryGetString(root, "defaultAgentModelId");
            var models = new List<ModelQuotaWindow>();

            foreach (var prop in modelsObj.EnumerateObject())
            {
                var key = prop.Name;
                var value = prop.Value;
                if (value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var displayName = TryGetString(value, "displayName");
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var modelEnum = TryGetString(value, "model") ?? key;
                var (fraction, resetsAt) = ExtractQuotaInfo(value);
                models.Add(
                    new ModelQuotaWindow(
                        modelEnum,
                        displayName,
                        fraction,
                        resetsAt,
                        FavoriteModelsPolicy.NormalizeFamilyName(displayName)));
            }

            if (models.Count == 0)
            {
                return new ProfileQuotaReadResult(
                    ProfileQuotaStatus.ResponseFormatChanged,
                    []);
            }

            var selectedId = !string.IsNullOrWhiteSpace(externalLastSelectedModelId)
                ? externalLastSelectedModelId
                : defaultModelId;

            return new ProfileQuotaReadResult(
                ProfileQuotaStatus.Available,
                models,
                AccountEmail: accountEmail,
                AccountPlan: accountPlan,
                LastSelectedModelId: selectedId,
                MostRecentlyUsedModelId: selectedId);
        }
        catch (JsonException)
        {
            return new ProfileQuotaReadResult(
                ProfileQuotaStatus.ResponseFormatChanged,
                []);
        }
    }

    /// <summary>
    /// %USERPROFILE%\.gemini\antigravity\antigravity_state.pbtxt 에서
    /// last_selected_agent_model 값을 읽는다.
    /// </summary>
    public static string? TryParseLastSelectedModelFromPbtxt(string? pbtxtContent)
    {
        if (string.IsNullOrWhiteSpace(pbtxtContent))
        {
            return null;
        }

        var match = LastSelectedModelPattern().Match(pbtxtContent);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["model"].Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static (double RemainingFraction, DateTimeOffset? ResetsAt) ExtractQuotaInfo(
        JsonElement modelElement)
    {
        var fraction = 1.0;
        DateTimeOffset? resetsAt = null;

        if (modelElement.TryGetProperty("quotaInfo", out var quotaInfo) &&
            quotaInfo.ValueKind == JsonValueKind.Object)
        {
            if (quotaInfo.TryGetProperty("remainingFraction", out var remProp) &&
                remProp.ValueKind == JsonValueKind.Number &&
                remProp.TryGetDouble(out var parsedFraction))
            {
                fraction = Math.Clamp(parsedFraction, 0.0, 1.0);
            }

            var resetTimeStr = TryGetString(quotaInfo, "resetTime");
            if (!string.IsNullOrWhiteSpace(resetTimeStr) &&
                DateTimeOffset.TryParse(
                    resetTimeStr,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedReset))
            {
                resetsAt = parsedReset;
            }
        }

        return (fraction, resetsAt);
    }

    private static string? ExtractModelOrAlias(JsonElement element)
    {
        if (element.TryGetProperty("modelOrAlias", out var modelOrAlias) &&
            modelOrAlias.ValueKind == JsonValueKind.Object)
        {
            return TryGetString(modelOrAlias, "model") ??
                   TryGetString(modelOrAlias, "alias");
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    [GeneratedRegex(
        @"last_selected_agent_model\s*:\s*""?(?<model>[A-Za-z0-9_.\-]+)""?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LastSelectedModelPattern();
}
