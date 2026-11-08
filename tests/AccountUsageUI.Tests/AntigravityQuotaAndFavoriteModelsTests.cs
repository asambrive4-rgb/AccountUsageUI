using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Core.Usage;
using AccountUsageUI.Infrastructure.Providers.Antigravity;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace AccountUsageUI.Tests;

[TestClass]
public sealed class AntigravityQuotaAndFavoriteModelsTests
{
    [TestMethod]
    public void ParseRetrieveUserQuotaSummaryJson_ParsesGroupsAndBucketsExactlyLikeAntigravityUsagePopup()
    {
        const string json = """
        {
          "response": {
            "groups": [
              {
                "displayName": "Gemini Models",
                "description": "Models within this group: Gemini Flash, Gemini Pro",
                "buckets": [
                  {
                    "bucketId": "gemini-weekly",
                    "displayName": "Weekly Limit Remaining",
                    "window": "weekly",
                    "remainingFraction": 1.0,
                    "resetTime": "2026-11-15T20:25:34Z"
                  },
                  {
                    "bucketId": "gemini-5h",
                    "displayName": "Five Hour Limit Remaining",
                    "window": "5h",
                    "remainingFraction": 1.0,
                    "resetTime": "2026-11-09T01:25:34Z"
                  }
                ]
              },
              {
                "displayName": "Claude and GPT models",
                "description": "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS",
                "buckets": [
                  {
                    "bucketId": "3p-weekly",
                    "displayName": "Weekly Limit Remaining",
                    "window": "weekly",
                    "remainingFraction": 1.0,
                    "resetTime": "2026-11-15T20:30:07Z"
                  },
                  {
                    "bucketId": "3p-5h",
                    "displayName": "Five Hour Limit Remaining",
                    "window": "5h",
                    "remainingFraction": 1.0,
                    "resetTime": "2026-11-09T01:30:07Z"
                  }
                ]
              },
              {
                "displayName": "Gemini 4 Argon",
                "description": "For paid Pro plans, Argon quota is separate.",
                "buckets": [
                  {
                    "bucketId": "gemini-argon-wtu-weekly",
                    "displayName": "Gemini 4 Argon Weekly Limit",
                    "description": "You have used some of your weekly limit, it will fully refresh in 6 days, 23 hours.",
                    "window": "weekly",
                    "remainingFraction": 0.88,
                    "resetTime": "2026-11-15T20:07:35Z"
                  }
                ]
              }
            ]
          }
        }
        """;

        var result = AntigravityQuotaResponseParser.ParseRetrieveUserQuotaSummaryJson(
            json,
            accountEmail: "user@example.com",
            accountPlan: "Google AI Pro");

        Assert.AreEqual(ProfileQuotaStatus.Available, result.Status);
        Assert.HasCount(5, result.Models);

        Assert.AreEqual("Gemini Models", result.Models[0].FamilyGroup);
        Assert.AreEqual("Weekly limit", result.Models[0].WindowLabel);
        Assert.AreEqual("100% left", UsageDisplaySettings.FormatPercentLeft(result.Models[0].RemainingFraction));
        Assert.IsNull(result.Models[0].ResetSubtext);

        Assert.AreEqual("Gemini Models", result.Models[1].FamilyGroup);
        Assert.AreEqual("5h limit", result.Models[1].WindowLabel);

        Assert.AreEqual("Claude and GPT models", result.Models[2].FamilyGroup);
        Assert.AreEqual("Weekly limit", result.Models[2].WindowLabel);
        Assert.AreEqual("5h limit", result.Models[3].WindowLabel);

        Assert.AreEqual("Gemini 4 Argon", result.Models[4].FamilyGroup);
        Assert.AreEqual("Weekly limit", result.Models[4].WindowLabel);
        Assert.AreEqual("88% left", UsageDisplaySettings.FormatPercentLeft(result.Models[4].RemainingFraction));
        Assert.IsNotNull(result.Models[4].ResetSubtext);
    }

    [TestMethod]
    public void UsageDisplaySettings_FormatResetSubtext_MatchesAntigravityResetsInFormula()
    {
        var now = new DateTimeOffset(2026, 11, 8, 20, 35, 0, TimeSpan.Zero);
        var resetsAt = new DateTimeOffset(2026, 11, 15, 20, 7, 35, TimeSpan.Zero);

        // When description is present (used quota), shows "Resets in 6d 23h"
        var subtext = UsageDisplaySettings.FormatResetSubtext(
            "You have used some of your weekly limit",
            resetsAt,
            nowOverride: now);
        Assert.AreEqual("Resets in 6d 23h", subtext);

        // When description is null/empty (100% left), hides reset subtext
        var hidden = UsageDisplaySettings.FormatResetSubtext(
            description: null,
            resetsAt,
            nowOverride: now);
        Assert.IsNull(hidden);
    }

    [TestMethod]
    public void ParseGetUserStatusJson_ParsesModelsPlanEmailAndDefaultOverride()
    {
        const string json = """
        {
          "userStatus": {
            "email": "user@example.com",
            "planStatus": {
              "planInfo": {
                "planName": "Google AI Pro"
              }
            },
            "cascadeModelConfigData": {
              "clientModelConfigs": [
                {
                  "label": "Gemini 4 Argon (Medium)",
                  "modelOrAlias": { "model": "MODEL_PLACEHOLDER_M47" },
                  "quotaInfo": {
                    "remainingFraction": 0.65,
                    "resetTime": "2026-11-09T08:00:00Z"
                  }
                },
                {
                  "label": "Gemini 4 Argon (High)",
                  "modelOrAlias": { "model": "MODEL_PLACEHOLDER_M48" },
                  "quotaInfo": {
                    "remainingFraction": 0.65,
                    "resetTime": "2026-11-09T08:00:00Z"
                  }
                },
                {
                  "label": "Claude Sonnet 5.5 (High)",
                  "modelOrAlias": { "model": "MODEL_PLACEHOLDER_M35" },
                  "quotaInfo": {
                    "remainingFraction": 1.0,
                    "resetTime": "2026-11-09T09:00:00Z"
                  }
                }
              ],
              "defaultOverrideModelConfig": {
                "modelOrAlias": { "model": "MODEL_PLACEHOLDER_M47" }
              }
            }
          }
        }
        """;

        var result = AntigravityQuotaResponseParser.ParseGetUserStatusJson(
            json,
            externalLastSelectedModelId: null);

        Assert.AreEqual(ProfileQuotaStatus.Available, result.Status);
        Assert.AreEqual("user@example.com", result.AccountEmail);
        Assert.AreEqual("Google AI Pro", result.AccountPlan);
        Assert.HasCount(3, result.Models);
        Assert.AreEqual("MODEL_PLACEHOLDER_M47", result.MostRecentlyUsedModelId);
    }

    [TestMethod]
    public void ParseFetchAvailableModelsJson_ParsesDictionaryModels()
    {
        const string json = """
        {
          "models": {
            "MODEL_PLACEHOLDER_M47": {
              "displayName": "Gemini 4 Argon (Medium)",
              "quotaInfo": {
                "remainingFraction": 0.42,
                "resetTime": "2026-11-09T07:30:00Z"
              }
            },
            "MODEL_PLACEHOLDER_M26": {
              "displayName": "Claude Opus 4.6 (Thinking)",
              "quotaInfo": {
                "remainingFraction": 0.88,
                "resetTime": "2026-11-09T10:00:00Z"
              }
            }
          }
        }
        """;

        var result = AntigravityQuotaResponseParser.ParseFetchAvailableModelsJson(
            json,
            accountEmail: "pro@example.com",
            accountPlan: "Ultra");

        Assert.AreEqual(ProfileQuotaStatus.Available, result.Status);
        Assert.AreEqual("pro@example.com", result.AccountEmail);
        Assert.AreEqual("Ultra", result.AccountPlan);
        Assert.HasCount(2, result.Models);
        Assert.IsTrue(
            result.Models.Any(
                m => m.DisplayName.Contains("Gemini 4 Argon") && Math.Abs(m.RemainingFraction - 0.42) < 0.001));
    }

    [TestMethod]
    public void SelectFavoriteModels_PreservesAllBucketsInDefaultUsageGroups()
    {
        var models = new[]
        {
            new ModelQuotaWindow("gemini-weekly", "Weekly Limit Remaining", 1.0, null, "Gemini Models", "weekly"),
            new ModelQuotaWindow("gemini-5h", "Five Hour Limit Remaining", 1.0, null, "Gemini Models", "5h"),
            new ModelQuotaWindow("3p-weekly", "Weekly Limit Remaining", 1.0, null, "Claude and GPT models", "weekly"),
            new ModelQuotaWindow("3p-5h", "Five Hour Limit Remaining", 1.0, null, "Claude and GPT models", "5h"),
            new ModelQuotaWindow("gemini-argon-wtu-weekly", "Gemini 4 Argon Weekly Limit", 0.88, null, "Gemini 4 Argon", "weekly", "Used")
        };

        var filtered = FavoriteModelsPolicy.SelectFavoriteModels(
            models,
            FavoriteModelsPolicy.DefaultFavoriteModels);

        Assert.HasCount(5, filtered);
        Assert.AreEqual("Gemini Models", filtered[0].FamilyGroup);
        Assert.AreEqual("Weekly limit", filtered[0].WindowLabel);
        Assert.AreEqual("5h limit", filtered[1].WindowLabel);
        Assert.AreEqual("Claude and GPT models", filtered[2].FamilyGroup);
        Assert.AreEqual("Gemini 4 Argon", filtered[4].FamilyGroup);
    }

    [TestMethod]
    public void SelectRecentlyUsedModel_DetectsDecrementedQuotaOrUsedBucket()
    {
        var profileId = ProfileId.New();
        var now = DateTimeOffset.UtcNow;

        var current = new ProfileQuotaSnapshot(
            profileId,
            ProfileQuotaStatus.Available,
            [
                new ModelQuotaWindow("gemini-weekly", "Weekly Limit Remaining", 1.0, now.AddDays(6), "Gemini Models", "weekly"),
                new ModelQuotaWindow("gemini-5h", "Five Hour Limit Remaining", 1.0, now.AddHours(5), "Gemini Models", "5h"),
                new ModelQuotaWindow("3p-weekly", "Weekly Limit Remaining", 1.0, now.AddDays(6), "Claude and GPT models", "weekly"),
                new ModelQuotaWindow("3p-5h", "Five Hour Limit Remaining", 1.0, now.AddHours(5), "Claude and GPT models", "5h"),
                new ModelQuotaWindow("gemini-argon-wtu-weekly", "Gemini 4 Argon Weekly Limit", 0.88, now.AddDays(6), "Gemini 4 Argon", "weekly", "Used")
            ],
            "user@example.com",
            "Pro",
            null,
            null,
            now);

        var selected = FavoriteModelsPolicy.SelectRecentlyUsedModel(
            current,
            previousSnapshot: null,
            FavoriteModelsPolicy.DefaultFavoriteModels);

        Assert.IsNotNull(selected);
        Assert.AreEqual("Gemini 4 Argon", selected.DisplayName);
        Assert.AreEqual(88, selected.RemainingPercent);
        Assert.AreEqual("Weekly limit", selected.WindowLabel);
    }

    [TestMethod]
    public void TryParseLastSelectedModelFromPbtxt_ExtractsModelId()
    {
        const string pbtxt = """
        window_bounds {
          width: 1280
        }
        last_selected_agent_model: MODEL_PLACEHOLDER_M47
        """;

        var model = AntigravityQuotaResponseParser.TryParseLastSelectedModelFromPbtxt(pbtxt);
        Assert.AreEqual("MODEL_PLACEHOLDER_M47", model);
    }

    [TestMethod]
    public void SelectRecentlyUsedModel_HandlesDuplicateKeysInPreviousSnapshotWithoutThrowing()
    {
        var profileId = ProfileId.New();
        var now = DateTimeOffset.UtcNow;

        // Duplicate entries like Claude Opus 5.5 without window
        var previous = new ProfileQuotaSnapshot(
            profileId,
            ProfileQuotaStatus.Available,
            [
                new ModelQuotaWindow("claude-opus-high", "Claude Opus 5.5 (High)", 1.0, now.AddDays(5), null, null),
                new ModelQuotaWindow("claude-opus-thinking", "Claude Opus 5.5 (Thinking)", 1.0, now.AddDays(5), null, null),
                new ModelQuotaWindow("gemini-flash-lite-high", "Gemini 3.5 Flash Lite (High)", 1.0, now.AddDays(5), null, null),
                new ModelQuotaWindow("gemini-flash-lite-low", "Gemini 3.5 Flash Lite (Low)", 1.0, now.AddDays(5), null, null)
            ],
            "user@example.com",
            "Pro",
            null,
            null,
            now);

        var current = new ProfileQuotaSnapshot(
            profileId,
            ProfileQuotaStatus.Available,
            [
                new ModelQuotaWindow("claude-opus-high", "Claude Opus 5.5 (High)", 0.75, now.AddDays(5), null, null),
                new ModelQuotaWindow("claude-opus-thinking", "Claude Opus 5.5 (Thinking)", 0.80, now.AddDays(5), null, null),
                new ModelQuotaWindow("gemini-flash-lite-high", "Gemini 3.5 Flash Lite (High)", 1.0, now.AddDays(5), null, null)
            ],
            "user@example.com",
            "Pro",
            null,
            null,
            now);

        var selected = FavoriteModelsPolicy.SelectRecentlyUsedModel(
            current,
            previousSnapshot: previous,
            FavoriteModelsPolicy.DefaultFavoriteModels);

        Assert.IsNotNull(selected);
        Assert.AreEqual("Claude Opus 5.5", selected.DisplayName);
    }
}
