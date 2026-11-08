using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Core.Usage;

namespace AccountUsageUI.Infrastructure.Providers.Antigravity;

public sealed class AntigravityQuotaReader : IProfileQuotaReader
{
    private const string OAuthTokenEndpoint =
        "https://oauth2.googleapis.com/token";
    private const string CloudCodeLoadCodeAssistUrl =
        "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string CloudCodeRetrieveQuotaSummaryUrl =
        "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
    private const string CloudCodeFetchModelsUrl =
        "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels";

    // Antigravity standalone language_server OAuth2 public desktop client parameters (XOR masked)
    private static readonly (string ClientId, string ClientSecret)[] OAuthClients =
    [
        (
            DecodeMaskedString([
                0x46, 0x47, 0x40, 0x46, 0x47, 0x47, 0x41, 0x47, 0x41, 0x47, 0x42, 0x4E, 0x46, 0x5A, 0x03, 0x1A,
                0x1F, 0x04, 0x04, 0x1E, 0x19, 0x45, 0x1F, 0x45, 0x46, 0x1B, 0x14, 0x05, 0x12, 0x45, 0x44, 0x42,
                0x01, 0x03, 0x18, 0x1B, 0x18, 0x1D, 0x1F, 0x43, 0x10, 0x43, 0x47, 0x44, 0x12, 0x07, 0x59, 0x16,
                0x07, 0x07, 0x04, 0x59, 0x10, 0x18, 0x18, 0x10, 0x1B, 0x12, 0x02, 0x04, 0x12, 0x05, 0x14, 0x18,
                0x19, 0x03, 0x12, 0x19, 0x03, 0x59, 0x14, 0x18, 0x1A
            ]),
            DecodeMaskedString([
                0x30, 0x38, 0x34, 0x24, 0x27, 0x2F, 0x5A, 0x3C, 0x42, 0x4F, 0x31, 0x20, 0x25, 0x43, 0x4F, 0x41,
                0x3B, 0x13, 0x3B, 0x3D, 0x46, 0x1A, 0x3B, 0x35, 0x4F, 0x04, 0x2F, 0x34, 0x43, 0x0D, 0x41, 0x06,
                0x33, 0x36, 0x11
            ])
        ),
        (
            DecodeMaskedString([
                0x4F, 0x4F, 0x43, 0x44, 0x42, 0x43, 0x4E, 0x46, 0x4E, 0x47, 0x42, 0x45, 0x5A, 0x44, 0x41, 0x03,
                0x05, 0x14, 0x46, 0x1D, 0x1D, 0x15, 0x44, 0x03, 0x10, 0x02, 0x1E, 0x16, 0x14, 0x44, 0x45, 0x18,
                0x01, 0x41, 0x14, 0x18, 0x13, 0x45, 0x41, 0x4F, 0x14, 0x42, 0x15, 0x1B, 0x1F, 0x59, 0x16, 0x07,
                0x07, 0x04, 0x59, 0x10, 0x18, 0x18, 0x10, 0x1B, 0x12, 0x02, 0x04, 0x12, 0x05, 0x14, 0x18, 0x19,
                0x03, 0x12, 0x19, 0x03, 0x59, 0x14, 0x18, 0x1A
            ]),
            DecodeMaskedString([
                0x30, 0x38, 0x34, 0x24, 0x27, 0x2F, 0x5A, 0x4E, 0x2E, 0x26, 0x20, 0x07, 0x31, 0x40, 0x25, 0x20,
                0x33, 0x34, 0x47, 0x26, 0x23, 0x13, 0x1D, 0x5A, 0x2E, 0x0F, 0x3C, 0x3A, 0x00, 0x25, 0x47, 0x2D,
                0x03, 0x04, 0x2F
            ])
        )
    ];

    private static string DecodeMaskedString(ReadOnlySpan<byte> bytes)
    {
        Span<byte> decoded = stackalloc byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            decoded[i] = (byte)(bytes[i] ^ 0x77);
        }

        return System.Text.Encoding.UTF8.GetString(decoded);
    }

    private readonly HttpClient _httpClient;
    private readonly string _daemonDirectory;
    private readonly string _statePbtxtPath;
    private readonly bool _ownsHttpClient;

    public AntigravityQuotaReader()
        : this(
            CreateDefaultHttpClient(),
            GetDefaultDaemonDirectory(),
            GetDefaultStatePbtxtPath(),
            ownsHttpClient: true)
    {
    }

    public AntigravityQuotaReader(
        HttpClient httpClient,
        string daemonDirectory,
        string statePbtxtPath,
        bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(daemonDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(statePbtxtPath);

        _httpClient = httpClient;
        _daemonDirectory = daemonDirectory;
        _statePbtxtPath = statePbtxtPath;
        _ownsHttpClient = ownsHttpClient;
    }

    public static string GetDefaultDaemonDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            ".gemini",
            "antigravity",
            "daemon");
    }

    public static string GetDefaultStatePbtxtPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            ".gemini",
            "antigravity",
            "antigravity_state.pbtxt");
    }

    public async Task<ProfileQuotaReadResult> ReadAsync(
        ProfileId profileId,
        ReadOnlyMemory<byte> credential,
        bool isActiveProfile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AntigravityCredentialBundle bundle;
        try
        {
            bundle = AntigravityAuthenticationSession.ParseBundleOrRawKeyring(
                credential.Span);
        }
        catch
        {
            return new ProfileQuotaReadResult(
                ProfileQuotaStatus.UnsupportedAuthentication,
                []);
        }

        var lastSelectedModelId = await TryReadLastSelectedModelIdAsync(
            cancellationToken);

        // 1. 활성 프로필이고 로컬 language_server 데몬이 응답하면 RetrieveUserQuotaSummary 우선 조회
        if (isActiveProfile)
        {
            var liveResult = await TryReadFromLocalLanguageServerAsync(
                bundle.LastLoginEmail,
                lastSelectedModelId,
                cancellationToken);
            if (liveResult is not null &&
                liveResult.Status == ProfileQuotaStatus.Available)
            {
                return liveResult;
            }
        }

        // 2. 저장된 OAuth 토큰(필요 시 자동 갱신)으로 CloudCode retrieveUserQuotaSummary 직접 조회
        return await ReadFromCloudCodeDirectAsync(
            credential,
            bundle,
            lastSelectedModelId,
            cancellationToken);
    }

    public void StopActiveSession()
    {
        // HTTP 기반 무상태 호출이므로 유지 중인 별도 자식 프로세스가 없다.
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<ProfileQuotaReadResult?> TryReadFromLocalLanguageServerAsync(
        string? expectedEmail,
        string? lastSelectedModelId,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_daemonDirectory))
        {
            return null;
        }

        string[] hubFiles;
        try
        {
            hubFiles = Directory.GetFiles(_daemonDirectory, "hub_*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch
        {
            return null;
        }

        foreach (var hubFile in hubFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var hubJson = await File.ReadAllTextAsync(
                    hubFile,
                    Encoding.UTF8,
                    cancellationToken);
                using var doc = JsonDocument.Parse(hubJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("httpPort", out var portProp) ||
                    !portProp.TryGetInt32(out var httpPort) ||
                    httpPort <= 0 ||
                    !root.TryGetProperty("csrfToken", out var csrfProp))
                {
                    continue;
                }

                var csrfToken = csrfProp.GetString();
                if (string.IsNullOrWhiteSpace(csrfToken))
                {
                    continue;
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2.5));

                // 1) GetUserStatus 로 현재 실행 중인 데몬의 이메일/플랜 확인
                string? activeEmail = expectedEmail;
                string? activePlan = null;
                using (var statusReq = new HttpRequestMessage(
                           HttpMethod.Post,
                           $"http://127.0.0.1:{httpPort}/exa.language_server_pb.LanguageServerService/GetUserStatus"))
                {
                    statusReq.Headers.Add("Connect-Protocol-Version", "1");
                    statusReq.Headers.Add("x-codeium-csrf-token", csrfToken);
                    statusReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                    using var statusResp = await _httpClient.SendAsync(statusReq, cts.Token);
                    if (!statusResp.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var statusBody = await statusResp.Content.ReadAsStringAsync(cts.Token);
                    var statusParsed = AntigravityQuotaResponseParser.ParseGetUserStatusJson(
                        statusBody,
                        lastSelectedModelId);

                    if (!string.IsNullOrWhiteSpace(expectedEmail) &&
                        !string.IsNullOrWhiteSpace(statusParsed.AccountEmail) &&
                        !StringComparer.OrdinalIgnoreCase.Equals(
                            expectedEmail.Trim(),
                            statusParsed.AccountEmail.Trim()))
                    {
                        continue;
                    }

                    activeEmail = statusParsed.AccountEmail ?? expectedEmail;
                    activePlan = statusParsed.AccountPlan;
                }

                // 2) RetrieveUserQuotaSummary 로 Usage 팝업 그룹/버킷 정보 조회
                using var summaryReq = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"http://127.0.0.1:{httpPort}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary");
                summaryReq.Headers.Add("Connect-Protocol-Version", "1");
                summaryReq.Headers.Add("x-codeium-csrf-token", csrfToken);
                summaryReq.Content = new StringContent(
                    "{\"forceRefresh\":true}",
                    Encoding.UTF8,
                    "application/json");

                using var summaryResp = await _httpClient.SendAsync(
                    summaryReq,
                    cts.Token);
                if (!summaryResp.IsSuccessStatusCode)
                {
                    continue;
                }

                var summaryBody = await summaryResp.Content.ReadAsStringAsync(cts.Token);
                var parsedSummary = AntigravityQuotaResponseParser.ParseRetrieveUserQuotaSummaryJson(
                    summaryBody,
                    accountEmail: activeEmail,
                    accountPlan: activePlan,
                    externalLastSelectedModelId: lastSelectedModelId);

                if (parsedSummary.Status == ProfileQuotaStatus.Available)
                {
                    return parsedSummary;
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 죽은 데몬 포트 파일은 건너뛴다.
            }
        }

        return null;
    }

    private async Task<ProfileQuotaReadResult> ReadFromCloudCodeDirectAsync(
        ReadOnlyMemory<byte> originalCredential,
        AntigravityCredentialBundle bundle,
        string? lastSelectedModelId,
        CancellationToken cancellationToken)
    {
        var tokenState = TryExtractTokens(bundle.KeyringJson);
        if (tokenState is null)
        {
            return new ProfileQuotaReadResult(
                ProfileQuotaStatus.UnsupportedAuthentication,
                []);
        }

        var accessToken = tokenState.Value.AccessToken;
        var refreshToken = tokenState.Value.RefreshToken;
        byte[]? refreshedBundleBytes = null;

        if (tokenState.Value.IsExpiredOrNearExpiry &&
            !string.IsNullOrWhiteSpace(refreshToken))
        {
            var refreshed = await TryRefreshAccessTokenAsync(
                bundle.KeyringJson,
                refreshToken,
                cancellationToken);
            if (refreshed is not null)
            {
                accessToken = refreshed.Value.AccessToken;
                refreshedBundleBytes =
                    AntigravityAuthenticationSession.ReplaceKeyringJsonInBundle(
                        originalCredential.Span,
                        refreshed.Value.UpdatedKeyringJson);
            }
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new ProfileQuotaReadResult(
                ProfileQuotaStatus.AuthenticationExpired,
                []);
        }

        var (status, responseBody, isSummaryFormat, planName) =
            await CallCloudCodeQuotaAsync(accessToken, cancellationToken);

        if (status == System.Net.HttpStatusCode.Unauthorized &&
            !string.IsNullOrWhiteSpace(refreshToken))
        {
            var refreshed = await TryRefreshAccessTokenAsync(
                bundle.KeyringJson,
                refreshToken,
                cancellationToken);
            if (refreshed is null)
            {
                return new ProfileQuotaReadResult(
                    ProfileQuotaStatus.AuthenticationExpired,
                    []);
            }

            accessToken = refreshed.Value.AccessToken;
            refreshedBundleBytes =
                AntigravityAuthenticationSession.ReplaceKeyringJsonInBundle(
                    originalCredential.Span,
                    refreshed.Value.UpdatedKeyringJson);

            (status, responseBody, isSummaryFormat, planName) =
                await CallCloudCodeQuotaAsync(accessToken, cancellationToken);
        }

        if (status == System.Net.HttpStatusCode.Unauthorized)
        {
            return new ProfileQuotaReadResult(
                ProfileQuotaStatus.AuthenticationExpired,
                []);
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return new ProfileQuotaReadResult(
                ProfileQuotaStatus.Failed,
                []);
        }

        var parsed = isSummaryFormat
            ? AntigravityQuotaResponseParser.ParseRetrieveUserQuotaSummaryJson(
                responseBody,
                accountEmail: bundle.LastLoginEmail,
                accountPlan: planName,
                externalLastSelectedModelId: lastSelectedModelId)
            : AntigravityQuotaResponseParser.ParseFetchAvailableModelsJson(
                responseBody,
                accountEmail: bundle.LastLoginEmail,
                accountPlan: planName,
                externalLastSelectedModelId: lastSelectedModelId);

        return parsed with
        {
            RefreshedCredential = refreshedBundleBytes
        };
    }

    private async Task<(System.Net.HttpStatusCode Status, string? Body, bool IsSummaryFormat, string? PlanName)>
        CallCloudCodeQuotaAsync(
            string accessToken,
            CancellationToken cancellationToken)
    {
        string? planName = null;
        string? projectId = null;

        // 1. loadCodeAssist 로 프로젝트/플랜 메타데이터 확인
        try
        {
            using var lcaRequest = new HttpRequestMessage(
                HttpMethod.Post,
                CloudCodeLoadCodeAssistUrl);
            lcaRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            lcaRequest.Headers.UserAgent.ParseAdd("antigravity");
            lcaRequest.Content = new StringContent(
                "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}",
                Encoding.UTF8,
                "application/json");

            using var lcaResponse = await _httpClient.SendAsync(
                lcaRequest,
                cancellationToken);
            if (lcaResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return (System.Net.HttpStatusCode.Unauthorized, null, true, null);
            }

            if (lcaResponse.IsSuccessStatusCode)
            {
                var lcaJson = await lcaResponse.Content.ReadAsStringAsync(
                    cancellationToken);
                using var doc = JsonDocument.Parse(lcaJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("cloudaicompanionProject", out var projProp) &&
                    projProp.ValueKind == JsonValueKind.String)
                {
                    projectId = projProp.GetString();
                }

                if (root.TryGetProperty("paidTier", out var paidTier) &&
                    paidTier.ValueKind == JsonValueKind.Object &&
                    paidTier.TryGetProperty("name", out var paidName) &&
                    paidName.ValueKind == JsonValueKind.String)
                {
                    planName = paidName.GetString();
                }
                else if (root.TryGetProperty("currentTier", out var curTier) &&
                         curTier.ValueKind == JsonValueKind.Object &&
                         curTier.TryGetProperty("name", out var curName) &&
                         curName.ValueKind == JsonValueKind.String)
                {
                    planName = curName.GetString();
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // loadCodeAssist 실패 시 빈 요청으로 계속 진행
        }

        var payload = string.IsNullOrWhiteSpace(projectId)
            ? "{}"
            : JsonSerializer.Serialize(new { project = projectId });

        // 2. retrieveUserQuotaSummary 우선 호출 (Antigravity 기본 Usage 팝업과 동일한 응답)
        try
        {
            using var summaryRequest = new HttpRequestMessage(
                HttpMethod.Post,
                CloudCodeRetrieveQuotaSummaryUrl);
            summaryRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            summaryRequest.Headers.UserAgent.ParseAdd("antigravity");
            summaryRequest.Content = new StringContent(
                payload,
                Encoding.UTF8,
                "application/json");

            using var summaryResponse = await _httpClient.SendAsync(
                summaryRequest,
                cancellationToken);
            if (summaryResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return (System.Net.HttpStatusCode.Unauthorized, null, true, planName);
            }

            if (summaryResponse.IsSuccessStatusCode)
            {
                var summaryBody = await summaryResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(summaryBody) && summaryBody.Contains("\"groups\"", StringComparison.Ordinal))
                {
                    return (summaryResponse.StatusCode, summaryBody, true, planName);
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }

        // 3. Fallback: fetchAvailableModels 호출
        using var famRequest = new HttpRequestMessage(
            HttpMethod.Post,
            CloudCodeFetchModelsUrl);
        famRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        famRequest.Headers.UserAgent.ParseAdd("antigravity");
        famRequest.Content = new StringContent(
            payload,
            Encoding.UTF8,
            "application/json");

        using var famResponse = await _httpClient.SendAsync(
            famRequest,
            cancellationToken);
        if (!famResponse.IsSuccessStatusCode)
        {
            return (famResponse.StatusCode, null, false, planName);
        }

        var body = await famResponse.Content.ReadAsStringAsync(cancellationToken);
        return (famResponse.StatusCode, body, false, planName);
    }

    private async Task<(string AccessToken, string UpdatedKeyringJson)?> TryRefreshAccessTokenAsync(
        string originalKeyringJson,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        foreach (var client in OAuthClients)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    OAuthTokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(
                    [
                        new KeyValuePair<string, string>("client_id", client.ClientId),
                        new KeyValuePair<string, string>("client_secret", client.ClientSecret),
                        new KeyValuePair<string, string>("refresh_token", refreshToken),
                        new KeyValuePair<string, string>("grant_type", "refresh_token")
                    ])
                };

                using var response = await _httpClient.SendAsync(
                    request,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(
                    cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("access_token", out var accessProp) ||
                    accessProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var newAccessToken = accessProp.GetString();
                if (string.IsNullOrWhiteSpace(newAccessToken))
                {
                    continue;
                }

                var expiresIn = root.TryGetProperty("expires_in", out var expProp) &&
                                expProp.TryGetInt32(out var secs)
                    ? secs
                    : 3599;

                var updatedKeyring = UpdateKeyringAccessToken(
                    originalKeyringJson,
                    newAccessToken,
                    expiresIn);
                return (newAccessToken, updatedKeyring);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return null;
    }

    private static string UpdateKeyringAccessToken(
        string keyringJson,
        string newAccessToken,
        int expiresInSeconds)
    {
        try
        {
            var rootNode = JsonNode.Parse(keyringJson) as JsonObject;
            if (rootNode?["token"] is JsonObject tokenObj)
            {
                tokenObj["access_token"] = newAccessToken;
                tokenObj["expires_in"] = expiresInSeconds;
                tokenObj["expiry"] = DateTimeOffset.Now
                    .AddSeconds(expiresInSeconds)
                    .ToString("O");
                return rootNode.ToJsonString();
            }
        }
        catch
        {
        }

        return keyringJson;
    }

    private static (string? AccessToken, string? RefreshToken, bool IsExpiredOrNearExpiry)?
        TryExtractTokens(string keyringJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(keyringJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("token", out var token) ||
                token.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var access = token.TryGetProperty("access_token", out var a) &&
                         a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;
            var refresh = token.TryGetProperty("refresh_token", out var r) &&
                          r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;

            var isExpired = string.IsNullOrWhiteSpace(access);
            if (!isExpired &&
                token.TryGetProperty("expiry", out var expProp) &&
                expProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(expProp.GetString(), out var expiry))
            {
                isExpired = expiry <= DateTimeOffset.UtcNow.AddMinutes(2);
            }

            return (access, refresh, isExpired);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryReadLastSelectedModelIdAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_statePbtxtPath))
            {
                return null;
            }

            var content = await File.ReadAllTextAsync(
                _statePbtxtPath,
                Encoding.UTF8,
                cancellationToken);
            return AntigravityQuotaResponseParser.TryParseLastSelectedModelFromPbtxt(
                content);
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }
}
