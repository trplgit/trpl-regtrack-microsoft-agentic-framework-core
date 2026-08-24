#pragma warning disable CS0618 // ADAL is deprecated - deliberate, see the class doc comment below.

using System.Security.Cryptography;
using Dapper;
using Insights.Data;
using Insights.Domain;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Azure.KeyVault.WebKey;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Trplclientsecret;

namespace Insights.Persistence;

/// <summary>
/// Envelope encryption via the real DocAI pattern, reused verbatim (design doc Sec.9.2), verified
/// live against real UAT Key Vault before this was written - not a reimplementation from the
/// design doc's field names alone.
///
/// KEY DECISION: reuses DocAI's existing CustomerID=0 default key (TRPLCryptoKey-001), not a
/// dedicated Insights key - confirmed with the team, zero new Key Vault provisioning needed.
///
/// [TRAP] ADAL (Microsoft.IdentityModel.Clients.ActiveDirectory) is deprecated by Microsoft - the
/// real DocAI source still uses it, and CLAUDE.md says reuse the scheme verbatim, so this does too.
/// It works (confirmed live, 2026-08-24), it just carries no further security patches. Migrating to
/// Azure.Identity/ClientSecretCredential (already referenced in this project for other purposes) is
/// a natural follow-up, not done here without the team's sign-off first.
/// </summary>
public sealed class AdalKeyVaultReportEncryptor : IReportEncryptor
{
    private readonly string _connectionString;
    private readonly Lazy<Task<(KeyVaultClient Client, KeyBundle KeyBundle)>> _keyLoader;

    public AdalKeyVaultReportEncryptor(string regTrackConnectionString)
    {
        _connectionString = regTrackConnectionString;

        /*  Lazy<Task<>> - loaded once, shared across every report this process encrypts. Matches
            KeyVaultManager.cs's own caching shape: the key bundle does not change per-call, and
            re-authenticating with Key Vault on every report would be pure waste.                */
        _keyLoader = new Lazy<Task<(KeyVaultClient, KeyBundle)>>(LoadKeyAsync);
    }

    public async Task<EncryptedReportEnvelope> EncryptAsync(string plaintextHtml, CancellationToken cancellationToken = default)
    {
        var (kvClient, keyBundle) = await _keyLoader.Value;
        var keyId = keyBundle.KeyIdentifier.Identifier;

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var wrapped = await kvClient.EncryptAsync(keyId, JsonWebKeyEncryptionAlgorithm.RSAOAEP, aes.Key, cancellationToken);

        using var plaintextStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(plaintextHtml));
        using var encryptor = aes.CreateEncryptor();
        using var cryptoStream = new CryptoStream(plaintextStream, encryptor, CryptoStreamMode.Read);

        // IV prepended to the content stream itself - same convention as ComplianceFileUploadService.cs,
        // so there is one blob byte stream to write and later read back, not a separate IV field to lose.
        using var output = new MemoryStream();
        await output.WriteAsync(aes.IV, cancellationToken);
        await cryptoStream.CopyToAsync(output, cancellationToken);

        return new EncryptedReportEnvelope(
            Content: output.ToArray(),
            EncryptedAesKey: wrapped.Result,
            KeyVaultObjectName: keyBundle.KeyIdentifier.Name,
            KeyVaultObjectVersion: keyId);
    }

    private async Task<(KeyVaultClient, KeyBundle)> LoadKeyAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        /*  [FIX - found live 2026-08-24] CustomerID = 0 has TWO rows in UAT (TRPLCryptoKey-001 and
            5-testBita01), confirmed by direct query. QuerySingleOrDefaultAsync throws "Sequence
            contains more than one element" on exactly this, every time - real DocAI code
            (KeyVaultManager.cs) uses FirstOrDefaultAsync here, not SingleOrDefaultAsync, tolerating
            multiple default rows and taking whichever comes first. Matched to that, not guessed.  */
        var config = (await connection.QueryAsync<KeyVaultConfigRow>(
            "SELECT VaultBaseUrl, BYOK_KeyName, ClientId FROM tbl_SecretKeyCredentialsCustomerwise WHERE CustomerID = 0;")).FirstOrDefault();

        if (config is null)
            throw new InvalidOperationException(
                "No default (CustomerID = 0) Key Vault configuration found in tbl_SecretKeyCredentialsCustomerwise.");

        var clientSecret = new BU().GetClientSecret();

        var kvClient = new KeyVaultClient(async (authority, resource, _) =>
        {
            var authContext = new AuthenticationContext(authority);
            var clientCred = new ClientCredential(config.ClientId, clientSecret);
            var result = await authContext.AcquireTokenAsync(resource, clientCred);
            return result.AccessToken;
        });

        var keyBundle = await kvClient.GetKeyAsync(config.VaultBaseUrl, config.BYOK_KeyName);
        return (kvClient, keyBundle);
    }

    private sealed record KeyVaultConfigRow(string VaultBaseUrl, string BYOK_KeyName, string ClientId);
}
