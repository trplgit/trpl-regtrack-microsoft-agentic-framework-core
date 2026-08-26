namespace Insights.Data;

/// <summary>
/// Reverses IReportEncryptor's envelope (design doc Sec.9.2) - unwraps the AES key via Key Vault,
/// decrypts the blob content back to plaintext HTML. Kept as its own interface, not a second method
/// bolted onto IReportEncryptor, so a caller that only ever writes (PersistActivity) never takes a
/// dependency on the ability to read.
/// </summary>
public interface IReportDecryptor
{
    /// <summary>
    /// <paramref name="encryptedContent"/> is the raw blob bytes as written by IReportBlobWriter -
    /// the IV prepended to the ciphertext, one stream, matching AdalKeyVaultReportEncryptor's own
    /// convention exactly.
    /// </summary>
    Task<string> DecryptAsync(
        byte[] encryptedContent, byte[] encryptedAesKey, string keyVaultObjectVersion, CancellationToken cancellationToken = default);
}
