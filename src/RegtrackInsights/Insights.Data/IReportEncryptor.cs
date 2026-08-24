using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Envelope-encrypts a report before it ever reaches blob storage (design doc Sec.9.2). Kept as an
/// interface for the same reason as IRunStatusReader/IInsightsRunEnqueuer - so the concrete Key
/// Vault/ADAL wiring lives in one place (Insights.Worker.Orchestration) without every caller taking
/// a hard dependency on it.
/// </summary>
public interface IReportEncryptor
{
    Task<EncryptedReportEnvelope> EncryptAsync(string plaintextHtml, CancellationToken cancellationToken = default);
}
