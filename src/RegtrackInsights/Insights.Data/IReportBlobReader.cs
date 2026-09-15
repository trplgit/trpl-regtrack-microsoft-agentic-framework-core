using Insights.Domain;

namespace Insights.Data;

/// <summary>Downloads a report blob written by IReportBlobWriter, still encrypted - decryption is a separate step (IReportDecryptor).</summary>
public interface IReportBlobReader
{
    Task<byte[]> ReadAsync(BlobLocation location, CancellationToken cancellationToken = default);
}
