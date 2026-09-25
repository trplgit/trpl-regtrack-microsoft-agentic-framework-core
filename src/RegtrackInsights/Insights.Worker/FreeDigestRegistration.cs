using System.Globalization;
using Insights.Agents;
using Insights.Data;
using Insights.Data.Email;
using Insights.Persistence;
using Insights.Presentation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insights.Worker;

/// <summary>
/// Registers the free weekly digest: LLM client, prompt loader, writer, renderer, email sender,
/// pipeline and <see cref="IFreeDigestService"/>.
///
/// Call AFTER AddInsightsData - the pipeline depends on IFreeDigestRepository, IScopeRepository
/// and IDictionaryRepository from there.
///
/// Every value comes from configuration. Nothing here is a literal, which is the point: the code
/// path a manual test exercises is the one the scheduler and the RegTrack API use, so a passing
/// test says something about production.
/// </summary>
public static class FreeDigestRegistration
{
    private const string LlmClientName = "insights-llm";
    private const string EmailClientName = "insights-email";
    private const string InsightApiClientName = "insights-insight-api";

    public static IServiceCollection AddInsightsFreeDigest(this IServiceCollection services, IConfiguration configuration)
    {
        /*  [TRAP] A FACTORY LAMBDA IS NOT STARTUP VALIDATION.
            Registering these as `_ => Require(configuration, ...)` compiles, and
            BuildServiceProvider(ValidateOnBuild: true) still reports success - the validator
            checks that the dependency GRAPH can be satisfied, and cannot see inside a lambda it
            has not run. Missing configuration would then surface on the first tenant of the
            first weekly run, in production, at 3am.

            So every required value is read HERE, eagerly, and captured. A missing key throws
            while the host is still starting, which is the whole reason for reading it early.   */
        var settings = BuildSettings(configuration);
        var promptDirectory = Require(configuration, "Agents:PromptDirectory");
        var templateDirectory = Require(configuration, "Email:TemplatePath");
        var chatClientFactory = BuildChatClientFactory(configuration);

        /*  [MEASURED on tenant 1082, 2026-09-24] The card is two sentences chosen from a closed set
            of numbers, not analysis, and it does not need the email's thinking budget: at medium
            effort the same card cost 1,664 output tokens, at low 927, and the text that shipped
            passed the same validator either way. Reasoning tokens are billed as output, so this is
            the whole saving. It gets its own knob rather than sharing the email's, so raising the
            email's effort later cannot quietly re-inflate a call that never needed it.          */
        var cardChatClientFactory = BuildChatClientFactory(
            configuration, configuration["Llm:AzureOpenAi:InsightCardReasoningEffort"] ?? "low");

        var emailSenderRegistryFactory = BuildEmailSenderRegistryFactory(configuration);
        var defaultEmailGateway = ReadDefaultEmailGateway(configuration);
        var emailHttpTimeout = TimeSpan.FromSeconds(RequirePositive(configuration, "Email:HttpTimeoutSeconds", 30));

        /*  Named HttpClients via the factory, never `new HttpClient()`. A long-lived host that
            constructs its own clients exhausts sockets; one holding a single static client never
            picks up DNS changes. The factory solves both.                                      */
        services.AddHttpClient(LlmClientName);

        /*  An explicit timeout, not HttpClient's 100s default: one wedged provider call would
            otherwise hold a recipient for ~6.5 minutes across the three retry attempts.        */
        services.AddHttpClient(EmailClientName, http => http.Timeout = emailHttpTimeout);
        services.AddHttpClient(InsightApiClientName, http => http.Timeout = settings.InsightApiTimeout);

        services.AddSingleton(settings);
        services.AddSingleton<FreeDigestMetrics>();
        services.AddSingleton<IPromptLoader>(_ => new FilePromptLoader(promptDirectory));
        services.AddSingleton(sp => new FreeDigestEmailRenderer(
            templateDirectory,
            sp.GetRequiredService<FreeDigestSettings>().CdnBaseUrl));

        services.AddSingleton(sp =>
            chatClientFactory(sp.GetRequiredService<IHttpClientFactory>().CreateClient(LlmClientName)));

        /*  Per-tenant email routing (2026-09-25). Every send goes through the registry, and every
            sender in it is individually rate-limited - nothing sends un-rate-limited. There is
            deliberately NO plain IEmailSender registration any more: a consumer that asked for
            "the" sender would bypass per-tenant routing.                                        */
        services.AddSingleton<IEmailSenderRegistry>(sp =>
            emailSenderRegistryFactory(sp.GetRequiredService<IHttpClientFactory>().CreateClient(EmailClientName)));

        var gatewayConnectionString = Require(configuration, "ConnectionStrings:RegTrack");
        services.AddSingleton<IEmailGatewayResolver>(_ => new SqlEmailGatewayResolver(gatewayConnectionString, defaultEmailGateway));

        services.AddSingleton<InsightNarrativeWriter>();

        // ADR-0004 (2026-09-23) - the insight card lane: the two lines of card text from the
        // monthly slot input, and the composer that turns slot data into the full card.
        services.AddSingleton(sp => new InsightCardWriter(
            cardChatClientFactory(sp.GetRequiredService<IHttpClientFactory>().CreateClient(LlmClientName)),
            sp.GetRequiredService<IPromptLoader>()));
        services.AddTransient<InsightCardComposer>();

        /*  The digest email content (spec 2026-09-18) - ComposeDigestActivity always uses these.
            Settings are read and validated HERE, eagerly - including that every configured prompt
            version exists on disk - for the same reason as BuildSettings above.                  */
        var monthlySettings = FreeMonthlySettings.Build(configuration, promptDirectory);
        services.AddSingleton(monthlySettings);

        /*  FreeDigest:Preview:DraftsDir re-runs the deterministic layers over drafts the model
            already wrote (see RecordedDraftClient) - no billed call. Development only, like
            ReplayDir below: ignored unless the preview is enabled.                            */
        var draftsDir = configuration.GetValue("FreeDigest:Preview:Enabled", false)
            ? configuration["FreeDigest:Preview:DraftsDir"]
            : null;

        if (draftsDir is { Length: > 0 })
            services.AddSingleton(sp => new FreeMonthlyDigestWriter(new RecordedDraftClient(Path.GetFullPath(draftsDir)), sp.GetRequiredService<IPromptLoader>()));
        else
            services.AddSingleton<FreeMonthlyDigestWriter>();
        /*  FreeDigest:Preview:ReplayDir replaces SQL with slot data captured earlier to disk, so
            prompt and model tuning can continue when the UAT database is unavailable. Development
            only: it is ignored unless FreeDigest:Preview:Enabled is also true, which no deployed
            worker sets. See CapturedFreeMonthlyDigestRepository.                                */
        var replayDir = configuration.GetValue("FreeDigest:Preview:Enabled", false)
            ? configuration["FreeDigest:Preview:ReplayDir"]
            : null;

        if (replayDir is { Length: > 0 })
            services.AddSingleton<IFreeMonthlyDigestRepository>(_ => new CapturedFreeMonthlyDigestRepository(Path.GetFullPath(replayDir)));
        else
            services.AddSingleton<IFreeMonthlyDigestRepository>(_ => new SqlFreeMonthlyDigestRepository(Require(configuration, "ConnectionStrings:RegTrack")));
        services.AddTransient<FreeMonthlyDigestComposer>();

        // ADR-0002 (2026-09-11) - the insight JSON POST lane. Registered unconditionally (same
        // "inert unless configured" pattern as FreeDigestScheduler below) - a named HttpClient with
        // no traffic costs nothing, and gating registration itself on InsightApiEnabled would mean
        // flipping the flag on later requires a redeploy just to register the dependency.
        services.AddSingleton<IInsightJsonRepository>(_ => new SqlInsightJsonRepository(Require(configuration, "ConnectionStrings:RegTrack")));
        services.AddTransient<Orchestration.Activities.PostInsightJsonActivity>(sp => new Orchestration.Activities.PostInsightJsonActivity(
            sp.GetRequiredService<IInsightJsonRepository>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(InsightApiClientName),
            settings,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Orchestration.Activities.PostInsightJsonActivity>>()));

        /*  ADR-0001 (2026-09-10) - the artifact index (sql/29) and its blob store. Reuses the SAME
            encryption scheme the paid pipeline uses (RegisterReportCodec, TryAddSingleton - safe
            to call again even if the paid write path already registered it, see that method's own
            doc comment), but writes to a SEPARATE container so nothing here can collide with, slow
            down, or be confused with a paid report. A dedicated AzureReportBlobWriter instance is
            constructed INLINE rather than through DI's own AzureReportBlobWriter registration,
            because TryAddSingleton keys on TYPE - a second `TryAddSingleton<AzureReportBlobWriter>`
            here would silently be skipped and this would end up writing digests into the PAID
            container instead of its own.                                                        */
        WorkerRegistration.RegisterReportCodec(services, configuration);

        var digestBlobConnectionString = Require(configuration, "Azure:BlobConnectionString");
        var digestBlobContainer = configuration["Azure:DigestBlobContainer"] ?? "insights-digests";
        services.AddSingleton<IDigestArtifactStore>(sp => new AzureDigestArtifactStore(
            sp.GetRequiredService<IReportEncryptor>(),
            sp.GetRequiredService<IReportDecryptor>(),
            new AzureReportBlobWriter(digestBlobConnectionString, digestBlobContainer)));

        var regTrackConnectionString = Require(configuration, "ConnectionStrings:RegTrack");
        services.AddSingleton<IFreeDigestArtifactRepository>(_ => new SqlFreeDigestArtifactRepository(regTrackConnectionString));

        /*  The weekly lane (ADR-0001). Registered unconditionally but INERT unless
            FreeDigest:Schedule:Enabled is true - a worker started for any other reason must not
            begin mailing customers because it happened to boot.                                */
        services.AddHostedService<FreeDigestScheduler>();

        return services;
    }

    private static FreeDigestSettings BuildSettings(IConfiguration configuration)
    {
        var settings = BuildSettingsCore(configuration);

        // ADR-0003 D6: auth is mandatory once this lane is live - a lambda-deferred check would
        // let the worker boot clean and only discover a missing key on the first Sunday POST, in
        // production. Refuse to start instead (same "fail at startup, not at 3am" stance as
        // Require() below).
        if (settings.InsightApiEnabled)
        {
            if (string.IsNullOrWhiteSpace(settings.InsightApiUrl) || !Uri.TryCreate(settings.InsightApiUrl, UriKind.Absolute, out _))
                throw new InvalidOperationException("FreeDigest:InsightApi:BaseUrl must be an absolute URL when FreeDigest:InsightApi:Enabled is true.");

            if (string.IsNullOrWhiteSpace(settings.InsightApiKey))
                throw new InvalidOperationException("FreeDigest:InsightApi:ApiKey must be set when FreeDigest:InsightApi:Enabled is true.");
        }

        return settings;
    }

    private static FreeDigestSettings BuildSettingsCore(IConfiguration configuration) => new()
    {
        InsightJsonTokenCap = configuration.GetValue("Budget:InsightJsonTokenCap", 3000),
        FromAddress = Require(configuration, "Email:FromAddress"),
        FromName = configuration["Email:FromName"] ?? "RegTrack Insights",
        CdnBaseUrl = Require(configuration, "Email:CdnBaseUrl").TrimEnd('/'),
        UpgradeUrl = Require(configuration, "Email:UpgradeUrl"),
        PortalUrl = configuration["Email:PortalUrl"],
        UnsubscribeBaseUrl = Require(configuration, "Email:UnsubscribeBaseUrl"),
        UnsubscribeSigningKey = Require(configuration, "Email:UnsubscribeSigningKey"),
        RecipientOverride = configuration["Email:RecipientOverride"],
        DebugDumpHtmlDir = configuration["FreeDigest:DebugDumpHtmlDir"],
        ScheduleEnabled = configuration.GetValue("FreeDigest:Schedule:Enabled", false),
        ScheduleCheckInterval = TimeSpan.FromMinutes(configuration.GetValue("FreeDigest:Schedule:CheckIntervalMinutes", 60)),
        SchedulePerTenantDelay = TimeSpan.FromMilliseconds(configuration.GetValue("FreeDigest:Schedule:PerTenantDelayMs", 250)),

        // ADR-0001 (2026-09-10) - the two-phase schedule. Default IST: confirmed by the product
        // owner as the intended zone for "9am" (see FreeDigestScheduler's own doc comment).
        ScheduleTimeZone = TimeZoneInfo.FindSystemTimeZoneById(configuration["FreeDigest:Schedule:TimeZone"] ?? "India Standard Time"),
        GenerateDay = Enum.Parse<DayOfWeek>(configuration["FreeDigest:Schedule:GenerateDay"] ?? "Sunday"),
        SendDay = Enum.Parse<DayOfWeek>(configuration["FreeDigest:Schedule:SendDay"] ?? "Monday"),
        GenerateHourLocal = RequireHour(configuration, "FreeDigest:Schedule:GenerateHourLocal", 0),
        SendHourLocal = RequireHour(configuration, "FreeDigest:Schedule:SendHourLocal", 8),
        ArtifactFreshnessDays = configuration.GetValue("FreeDigest:Artifact:FreshnessDays", 3),
        ArtifactRetentionDays = configuration.GetValue("FreeDigest:Artifact:RetentionDays", 90),

        // ADR-0002 (2026-09-11) - deliberately NOT Require()'d. The destination endpoint is not
        // configured yet (the user will supply it later); the worker must still boot with this
        // lane simply inert (Enabled=false) rather than refusing to start.
        InsightApiEnabled = configuration.GetValue("FreeDigest:InsightApi:Enabled", false),
        InsightApiUrl = configuration["FreeDigest:InsightApi:BaseUrl"],
        InsightApiKey = configuration["FreeDigest:InsightApi:ApiKey"],
        InsightApiTimeout = TimeSpan.FromSeconds(configuration.GetValue("FreeDigest:InsightApi:TimeoutSeconds", 30)),
    };

    /*  Both factories validate their provider name AND its keys eagerly, then return a closure
        that only needs the HttpClient. Adding a provider is one case arm plus a config value.

        Both FAIL CLOSED on an unrecognised name. Falling back to a default would mean shipping
        mail, or spending LLM budget, through something nobody selected - the same silent-default
        failure the dictionary's coverage check exists to prevent elsewhere.                    */
    /// <param name="reasoningEffortOverride">
    /// A lane that needs less thinking than the deployment's default (the insight card, ADR-0004).
    /// Applied ONLY when the deployment is already a reasoning one: setting an effort on a
    /// non-reasoning deployment switches the request to <c>max_completion_tokens</c> and it would
    /// reject the call outright.
    /// </param>
    private static Func<HttpClient, IClaudeClient> BuildChatClientFactory(IConfiguration configuration, string? reasoningEffortOverride = null)
    {
        var provider = Require(configuration, "Llm:Provider");

        switch (Normalise(provider))
        {
            case "azureopenai":
                var endpoint = Require(configuration, "Llm:AzureOpenAi:Endpoint");
                var deployment = Require(configuration, "Llm:AzureOpenAi:Deployment");
                var azureKey = Require(configuration, "Llm:AzureOpenAi:ApiKey");

                /*  Optional, and deliberately so: absent means no temperature is sent and the
                    deployment's own default applies. An explicit value is rejected outright by some
                    deployments, so this must stay "unset" rather than defaulting to a number.     */
                var temperature = configuration["Llm:AzureOpenAi:Temperature"] is { Length: > 0 } rawTemperature
                    ? double.TryParse(rawTemperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 2
                        ? parsed
                        : throw new InvalidOperationException("Llm:AzureOpenAi:Temperature must be a number between 0 and 2, or absent to use the deployment default.")
                    : (double?)null;

                /*  ReasoningEffort present = this is a reasoning deployment (gpt-5.6-luna and
                    siblings). It steers accuracy in place of temperature, which such a model
                    rejects outright - so the two are mutually exclusive and setting both stops
                    startup rather than failing on the first call at 3am. Leave it empty for
                    gpt-4o-mini and nothing about the request changes.                          */
                var effort = configuration["Llm:AzureOpenAi:ReasoningEffort"]?.Trim().ToLowerInvariant();
                if (effort is { Length: > 0 })
                {
                    if (effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
                        throw new InvalidOperationException($"Llm:AzureOpenAi:ReasoningEffort '{effort}' is not a known level (none|minimal|low|medium|high|xhigh|max).");

                    if (temperature is not null)
                        throw new InvalidOperationException(
                            "Llm:AzureOpenAi:Temperature and ReasoningEffort are both set. A reasoning model rejects "
                            + "temperature - clear Temperature and use ReasoningEffort as the accuracy control.");
                }

                var verbosity = configuration["Llm:AzureOpenAi:Verbosity"]?.Trim().ToLowerInvariant();
                if (verbosity is { Length: > 0 } and not ("low" or "medium" or "high"))
                    throw new InvalidOperationException($"Llm:AzureOpenAi:Verbosity '{verbosity}' is not a known level (low|medium|high).");

                /*  Sized in THOUSANDS when reasoning is on: reasoning tokens are spent from this same
                    budget, so a reply-sized number lets the model think itself out of an answer and
                    return an empty body.                                                          */
                int? maxOutputTokens = int.TryParse(configuration["Llm:AzureOpenAi:MaxOutputTokens"], out var parsedMax) ? parsedMax : null;
                if (effort is { Length: > 0 } && maxOutputTokens is null or < 1000)
                    throw new InvalidOperationException(
                        "Llm:AzureOpenAi:MaxOutputTokens must be at least 1000 when ReasoningEffort is set. "
                        + "Reasoning tokens come out of it, so a small budget returns an empty body. 8000 is a sensible start.");

                /*  A lane may think less than the deployment's default, never more cheaply than it
                    can: an override on a deployment with no reasoning effort at all is ignored,
                    because turning reasoning ON for one lane would change the request shape.    */
                if (reasoningEffortOverride is { Length: > 0 } && effort is { Length: > 0 })
                {
                    effort = reasoningEffortOverride.Trim().ToLowerInvariant();
                    if (effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
                        throw new InvalidOperationException(
                            $"Llm:AzureOpenAi:InsightCardReasoningEffort '{effort}' is not a known level (none|minimal|low|medium|high|xhigh|max).");
                }

                return http => new AzureOpenAiChatClient(
                    http, endpoint, deployment, azureKey, temperature, effort, verbosity, maxOutputTokens);

            case "openai":
                var openAiKey = Require(configuration, "Llm:OpenAi:ApiKey");
                var openAiModel = configuration["Llm:OpenAi:Model"] ?? "gpt-4o-mini";
                return http => new OpenAiChatClient(http, openAiKey, openAiModel);

            case "anthropic":
                var anthropicKey = Require(configuration, "Llm:Anthropic:ApiKey");
                var anthropicModel = Require(configuration, "Llm:Anthropic:Model");
                return http => new AnthropicClaudeClient(http, anthropicKey, anthropicModel);

            default:
                throw new InvalidOperationException(
                    $"Unknown Llm:Provider '{provider}'. Expected azure_openai, openai or anthropic.");
        }
    }

    /*  Per-tenant routing (2026-09-25): ANY tenant can be routed to EITHER provider by
        dbo.EmailDeliveryGatewayCustomization, so both keys and both rate limits are required at
        startup - a missing SendGrid key must stop the host now, not fail the first SendGrid-routed
        tenant on a Monday morning.                                                             */
    private static Func<HttpClient, IEmailSenderRegistry> BuildEmailSenderRegistryFactory(IConfiguration configuration)
    {
        /*  Email:Provider used to pick ONE provider for every tenant. Silently ignoring a leftover
            value would let someone believe they had forced all mail through one provider when
            they had not - so its presence stops startup and says what replaced it.             */
        if (configuration["Email:Provider"] is { Length: > 0 } retired)
            throw new InvalidOperationException(
                $"Email:Provider ('{retired}') is retired - the provider is now chosen per tenant from "
                + "dbo.EmailDeliveryGatewayCustomization (no row = Email:DefaultGatewayId). Remove the key.");

        var elasticKey = Require(configuration, "Email:ElasticEmail:ApiKey");
        var sendGridKey = Require(configuration, "Email:SendGrid:ApiKey");

        var elasticRate = RequirePositive(configuration, "Email:ElasticEmail:RateLimit:RequestsPerSecond", 5);
        var elasticAcquire = TimeSpan.FromSeconds(RequirePositive(configuration, "Email:ElasticEmail:RateLimit:AcquireTimeoutSeconds", 30));
        var sendGridRate = RequirePositive(configuration, "Email:SendGrid:RateLimit:RequestsPerSecond", 5);
        var sendGridAcquire = TimeSpan.FromSeconds(RequirePositive(configuration, "Email:SendGrid:RateLimit:AcquireTimeoutSeconds", 30));

        return http => new EmailSenderRegistry(new Dictionary<EmailGateway, IEmailSender>
        {
            [EmailGateway.ElasticEmail] = new RateLimitedEmailSender(new ElasticEmailSender(http, elasticKey), elasticRate, elasticAcquire),
            [EmailGateway.SendGrid] = new RateLimitedEmailSender(new SendGridEmailSender(http, sendGridKey), sendGridRate, sendGridAcquire),
        });
    }

    /// <summary>Email:DefaultGatewayId - the provider for a tenant with no active gateway row. An EmailGatewayMaster ID; default 1 (Elastic Email).</summary>
    private static EmailGateway ReadDefaultEmailGateway(IConfiguration configuration)
    {
        var id = configuration.GetValue("Email:DefaultGatewayId", (int)EmailGateway.ElasticEmail);

        return Enum.IsDefined(typeof(EmailGateway), id)
            ? (EmailGateway)id
            : throw new InvalidOperationException(
                $"Email:DefaultGatewayId {id} is not a known gateway. Expected 1 (Elastic Email) or 2 (SendGrid).");
    }

    private static int RequirePositive(IConfiguration configuration, string key, int defaultValue)
    {
        var value = configuration.GetValue(key, defaultValue);
        return value > 0 ? value : throw new InvalidOperationException($"{key} must be greater than 0 (was {value}).");
    }

    private static int RequireHour(IConfiguration configuration, string key, int defaultValue)
    {
        var value = configuration.GetValue(key, defaultValue);
        return value is >= 0 and <= 23 ? value : throw new InvalidOperationException($"{key} must be an hour from 0 to 23 (was {value}).");
    }

    /// <summary>
    /// Provider names are compared case- and separator-insensitively, so "ElasticEmail",
    /// "elastic_email" and "elastic-email" all select the same implementation.
    ///
    /// This is NOT a loosening of the fail-closed rule - an unrecognised name still throws. It
    /// only stops a config file written in one casing convention from refusing to start against
    /// code written in another, a failure that says nothing about correctness.
    /// </summary>
    private static string Normalise(string provider) =>
        provider.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

    /// <summary>
    /// Fails at STARTUP on a missing value, not at the first send - see the trap note above for
    /// why that distinction needed work to be true. Same stance AddInsightsData takes on the
    /// connection string.
    /// </summary>
    private static string Require(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set it in appsettings for local work, or via user-secrets / environment configuration elsewhere.");
}





