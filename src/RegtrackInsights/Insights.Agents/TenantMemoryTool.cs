using System.ComponentModel;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Insights.Data;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-09-22] Cross-run per-tenant narrative memory - see
/// docs/superpowers/specs/2026-09-22-tenant-memory-blob-design.md for the full design. One blob per
/// tenant (&lt;tenantId&gt;/history.md.enc), one "## {DimensionName}" section per dimension inside
/// it (TenantMemorySections owns that structure). Same envelope encryption as report blobs
/// (IReportEncryptor/IReportDecryptor -> AdalKeyVaultReportEncryptor), reused as-is.
///
/// WHAT STAYS DETERMINISTIC even though the model authors the section TEXT: the blob path (tenant
/// id only, never model-supplied), which dimension's section a given call may touch (validated
/// against <see cref="AllowedDimensions"/>, a closed set built by the caller - never open-ended),
/// the encryption itself, and the optimistic-concurrency retry loop. This is the same "model
/// authors content, C# owns scope/safety" split as ReadOnlySqlFetchTool.
///
/// A memory read or write failure must NEVER fail or degrade the actual report - memory is an
/// enhancement layered on an already-complete report, not something the report depends on. Every
/// public method here catches its own failures and degrades (empty history on read; an error
/// string, never a thrown exception, on write) rather than letting a Key Vault or blob outage
/// propagate up into the narrate call.
/// </summary>
public sealed class TenantMemoryTool
{
    public const int MaxSectionChars = 6000;

    /// <summary>
    /// [ADDED 2026-09-28] Sanity ceiling on what the model may SEND in one call - well above
    /// MaxSectionChars, since anything between the two is compacted (TenantMemoryCompactor), not
    /// refused. Stops a runaway tool argument from being encrypted and uploaded.
    /// </summary>
    public const int MaxInputChars = 30000;
    // [CORRECTED 2026-09-23] Was 5, sized against a guessed ~4-7 dimensions for fixed_holistic
    // (FixedHolisticComposition.Dimensions' own original guess) - that list is now corrected to
    // the real 15 dimensions FetchDimensionsActivity actually attempts for this ReportType, so a
    // single v1 narrate call's allowedDimensions can legitimately be 15 long. 15, not higher: the
    // real, repeated finding all session is that the model almost never calls this tool at all
    // (natural runs: 0 for 7+ across both the SQL fetch tool and this one) - this cap exists as a
    // cost backstop for the rare case it decides to, not a limit expected to bind in practice.
    public const int MaxCallsPerRun = 15;
    public const int MaxWriteRetries = 3;

    // [FOUND DURING IMPLEMENTATION, 2026-09-22] BlobServiceClient's default retry policy took over
    // two minutes to give up against a genuinely unreachable host - unacceptable for a feature that
    // is explicitly supposed to fail SOFT, not hang the whole narrate call. A memory outage must
    // degrade fast, not slowly. Bounded here: 1 retry, 5s network timeout per attempt.
    private static readonly BlobClientOptions ClientOptions = new()
    {
        Retry =
        {
            MaxRetries = 1,
            NetworkTimeout = TimeSpan.FromSeconds(5),
        },
    };

    private readonly IReportEncryptor encryptor;
    private readonly IReportDecryptor decryptor;
    private readonly string blobConnectionString;
    private readonly string containerName;
    private readonly int tenantId;
    private readonly ITenantMemorySummarizer? summarizer;
    private int callCount;

    public IReadOnlyList<string> AllowedDimensions { get; }

    public TenantMemoryTool(
        IReportEncryptor encryptor, IReportDecryptor decryptor,
        string blobConnectionString, string containerName,
        int tenantId, IReadOnlyList<string> allowedDimensions,
        ITenantMemorySummarizer? summarizer = null)
    {
        this.summarizer = summarizer;
        this.encryptor = encryptor;
        this.decryptor = decryptor;
        this.blobConnectionString = blobConnectionString;
        this.containerName = containerName;
        this.tenantId = tenantId;
        AllowedDimensions = allowedDimensions;
    }

    /// <summary>
    /// Plain C# method - NOT an AI function. Called deterministically before building the narrate
    /// payload, for every dimension in <see cref="AllowedDimensions"/> at once, so the agent has
    /// its own dimensions' history already in context without needing a read tool call (same "read
    /// side: always injected, not a tool" reasoning as ReadOnlySqlFetchTool's dimension_rows).
    /// Never throws - a Key Vault/blob failure degrades to every section being "", same as a
    /// tenant's genuine first-ever run.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadSectionsAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>();
        foreach (var dimension in AllowedDimensions)
            result[dimension] = "";

        try
        {
            var current = await FetchCurrentAsync(cancellationToken);
            if (!current.Exists)
                return result;

            foreach (var dimension in AllowedDimensions)
                result[dimension] = TenantMemorySections.ExtractSection(current.Content, dimension);
        }
        catch
        {
            // Fail-soft by design - see class doc comment. Every section stays "".
        }

        return result;
    }

    [Description(
        "Saves this dimension's notes to the tenant's persistent cross-run memory, for comparing " +
        "runs (month to month). Your text REPLACES the whole section, so carry forward what still " +
        "matters. Format: a '### Keep' block with facts that must survive every future compaction " +
        "(baselines with their period and date, recurring problems, first-seen dates), then one " +
        "'### YYYY-MM-DD (period)' entry per run, newest first, whose FIRST point is that run's " +
        "most important finding. Over 6000 characters, runs older than the two newest are " +
        "automatically summarised into one '### Summary of older runs' block that keeps every date, " +
        "baseline and first point; Keep and the two newest runs are stored word for word. Only " +
        "call this for the dimension you are narrating THIS run.")]
    public async Task<string> WriteTenantMemoryAsync(
        [Description("Must be exactly one of the dimensions this run is narrating.")]
        string dimensionName,
        [Description("The new full markdown body for this dimension's section (not the heading itself). " +
            "Use '### Keep' for must-survive facts and '### YYYY-MM-DD (period)' per run (first point = " +
            "most important). Aim for under ~3000 characters; over 6000, older runs are summarised " +
            "automatically. Refused only if the Keep block alone is over 3000 characters.")]
        string newSectionMarkdown)
    {
        if (callCount >= MaxCallsPerRun)
            return Error($"Budget exhausted - at most {MaxCallsPerRun} memory writes per run.");

        if (!AllowedDimensions.Contains(dimensionName, StringComparer.Ordinal))
            return Error($"'{dimensionName}' is not one of the dimensions this run is narrating ({string.Join(", ", AllowedDimensions)}).");

        if (string.IsNullOrWhiteSpace(newSectionMarkdown))
            return Error("Empty content - nothing to save.");

        if (newSectionMarkdown.Length > MaxInputChars)
            return Error($"Content is {newSectionMarkdown.Length} characters, over the {MaxInputChars}-character input limit - condense and resubmit.");

        // [ADDED 2026-09-28] A "## " line would start ANOTHER dimension's section in the tenant file
        // and could overwrite its memory - demoted to "### " before anything is stored. Then the
        // section is compacted to MaxSectionChars the way TenantMemoryCompactor documents (Keep never
        // touched, oldest runs shortened to their most important point first) instead of refused.
        var sanitized = TenantMemoryCompactor.SanitizeHeadings(newSectionMarkdown);
        var compacted = TenantMemoryCompactor.Compact(sanitized, MaxSectionChars);
        if (compacted.Refusal == TenantMemoryCompactor.KeepTooLarge)
            return Error($"The '### Keep' block alone is over {MaxSectionChars / 2} characters - you decide what matters most: shorten Keep and resubmit.");

        // [CHANGED 2026-09-28] Over the limit, older runs are SUMMARISED (not cut): the summariser
        // sees only the runs older than the two newest, and its block is checked before it is
        // stored. Keep and the two newest runs are stored word for word. The deterministic cut
        // (compacted, above) is only the fallback when no summariser is wired or it fails.
        var summarized = 0;
        if (compacted.Changed && summarizer is not null && TenantMemoryCompactor.PlanSummary(sanitized, MaxSectionChars) is { } plan)
        {
            try
            {
                var summary = await summarizer.SummarizeAsync(dimensionName, plan.OlderEntries, plan.SummaryBudget);
                if (TenantMemoryCompactor.IsValidSummary(summary, plan.SummaryBudget))
                {
                    compacted = new TenantMemoryCompactor.Result(TenantMemoryCompactor.Assemble(plan, summary!), Changed: true, 0, 0);
                    summarized = plan.OlderCount;
                }
            }
            catch
            {
                // Fall back to the deterministic compaction below - never lose this run's write.
            }
        }

        if (compacted.Text is null)
            return Error($"A single entry is too long to fit in {MaxSectionChars} characters even after compaction - split it into short points and resubmit.");
        newSectionMarkdown = compacted.Text;

        callCount++;

        for (var attempt = 0; attempt <= MaxWriteRetries; attempt++)
        {
            try
            {
                var current = await FetchCurrentAsync(CancellationToken.None);
                var updatedDocument = TenantMemorySections.ReplaceSection(current.Content, dimensionName, newSectionMarkdown);
                var envelope = await encryptor.EncryptAsync(updatedDocument);

                var service = new BlobServiceClient(blobConnectionString, ClientOptions);
                var container = service.GetBlobContainerClient(containerName);
                await container.CreateIfNotExistsAsync();
                var blob = container.GetBlobClient(BlobPath);

                var uploadOptions = new BlobUploadOptions
                {
                    Conditions = current.Exists
                        ? new BlobRequestConditions { IfMatch = current.ETag }
                        : new BlobRequestConditions { IfNoneMatch = ETag.All },
                    Metadata = new Dictionary<string, string>
                    {
                        ["keyvault_object_name"] = envelope.KeyVaultObjectName,
                        ["keyvault_object_version"] = envelope.KeyVaultObjectVersion,
                        ["encrypted_aes_key"] = Convert.ToBase64String(envelope.EncryptedAesKey),
                    },
                };

                using var contentStream = new MemoryStream(envelope.Content, writable: false);
                await blob.UploadAsync(contentStream, uploadOptions);

                return summarized > 0
                    ? JsonSerializer.Serialize(new { ok = true, summarized_older_runs = summarized, stored_chars = newSectionMarkdown.Length })
                    : compacted.Changed
                        ? JsonSerializer.Serialize(new { ok = true, compacted = true, shortened_older_runs = compacted.Shortened, removed_oldest_runs = compacted.Removed, stored_chars = newSectionMarkdown.Length })
                        : "{\"ok\":true}";
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Someone else wrote in between - re-fetch and reapply on the next loop iteration.
                // [FIX 2026-09-27] No attempt guard here: a conflict on the LAST attempt used to fall
                // into the generic catch below and report "Memory write failed: <412 text>" instead of
                // the accurate "gave up after N conflicting writes" message after the loop.
            }
            catch (Exception ex)
            {
                // Never let the raw exception reach the model, and never fail the report over this
                // - see class doc comment.
                return Error($"Memory write failed: {ex.Message}");
            }
        }

        return Error($"Gave up after {MaxWriteRetries} conflicting concurrent writes - the report itself is unaffected.");
    }

    /// <summary>
    /// [MOVED 2026-09-28] In the tenant's own folder of the REPORTS container, beside its reports
    /// (<c>{tenantId}/{reportType}/{yyyy}/{MM}/...</c>) - was <c>{tenantId}/history.md.enc</c> in a
    /// separate insights-tenant-memory container. At the tenant root, outside the dated report
    /// folders, so a month-prefix clean-up of old reports never deletes it.
    /// </summary>
    public const string BlobFileName = "tenant-memory.md.enc";

    private string BlobPath => $"{tenantId}/{BlobFileName}";

    private async Task<(string Content, ETag ETag, bool Exists)> FetchCurrentAsync(CancellationToken cancellationToken)
    {
        var service = new BlobServiceClient(blobConnectionString, ClientOptions);
        var blob = service.GetBlobContainerClient(containerName).GetBlobClient(BlobPath);

        BlobDownloadResult downloaded;
        try
        {
            downloaded = await blob.DownloadContentAsync(cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return ("", default, false);
        }

        var properties = downloaded.Details;
        var metadata = properties.Metadata;
        var keyVaultVersion = metadata.TryGetValue("keyvault_object_version", out var v) ? v : "";
        var encryptedAesKey = metadata.TryGetValue("encrypted_aes_key", out var keyB64)
            ? Convert.FromBase64String(keyB64)
            : [];

        var plaintext = await decryptor.DecryptAsync(
            downloaded.Content.ToArray(), encryptedAesKey, keyVaultVersion, cancellationToken);

        return (plaintext, properties.ETag, true);
    }

    private static string Error(string reason) => JsonSerializer.Serialize(new { error = reason });
}
