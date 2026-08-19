using System.Data;
using Dapper;
using Insights.Domain;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IDictionaryRepository"/>
public sealed class SqlDictionaryRepository(string connectionString) : IDictionaryRepository
{
    /// <summary>usp_Insights_AssertStatusCoverage: a ComplianceStatus exists that the dictionary does not map.</summary>
    private const int UnmappedStatusErrorNumber = 51001;

    /// <summary>usp_Insights_StatusDataQuality: past-due schedules reference statuses absent from the dictionary.</summary>
    private const int PastDueUnmappedStatusErrorNumber = 51003;

    /// <summary>usp_Insights_StatusDataQuality: NULL-status volume exceeds the proportionate threshold.</summary>
    private const int UnknownStatusVolumeErrorNumber = 51004;

    public async Task<bool> AssertStatusCoverageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        try
        {
            // This proc THROWs BEFORE its SELECT, so there is nothing to lose on the failure
            // path and a plain Dapper call is safe - unlike GetStatusDataQualityAsync below.
            return await connection.QuerySingleAsync<bool>(
                new CommandDefinition(
                    "dbo.usp_Insights_AssertStatusCoverage",
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (ex.Number == UnmappedStatusErrorNumber)
        {
            throw new DictionaryCoverageException(
                "one or more ComplianceStatus values are absent from InsightsStatusClassification.", null, ex);
        }
    }

    public async Task<StatusDataQualityProbe> GetStatusDataQualityAsync(
        int customerId,
        int maxAbsoluteGap = 100,
        decimal maxProportionPct = 0.10m,
        CancellationToken cancellationToken = default)
    {
        StatusDataQualityProbe? probe = null;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "dbo.usp_Insights_StatusDataQuality";
        command.Parameters.Add(new SqlParameter("@CustomerID", SqlDbType.Int) { Value = customerId });
        command.Parameters.Add(new SqlParameter("@MaxAbsoluteGap", SqlDbType.Int) { Value = maxAbsoluteGap });
        command.Parameters.Add(new SqlParameter("@MaxProportionPct", SqlDbType.Decimal)
        {
            Precision = 5, Scale = 2, Value = maxProportionPct,
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // [TRAP] SAME SHAPE AS usp_Insights_GoldenInvariants: this proc SELECTs the probe row
        // FIRST and only THEN THROWs (51003 / 51004). The THROW surfaces here on the read that
        // would otherwise report "no more rows" - i.e. after the row is already in hand.
        // Dapper's buffered QueryAsync would discard it, because the exception propagates out of
        // Dapper's own loop before it returns what it built. Reading with a raw SqlDataReader
        // keeps the counts, so the exception can carry them and the failure is diagnosable
        // without a second round trip. That detail is the entire value of this proc.
        try
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                probe = ReadProbe(reader);
            }
        }
        catch (SqlException ex) when (ex.Number == PastDueUnmappedStatusErrorNumber)
        {
            throw new DictionaryCoverageException(
                "past-due schedules reference status values absent from the dictionary.", probe, ex);
        }
        catch (SqlException ex) when (ex.Number == UnknownStatusVolumeErrorNumber)
        {
            // probe is non-null here: the proc always selects before this THROW.
            throw new StatusDataQualityThresholdException(probe!, ex);
        }

        return probe ?? throw new InvalidOperationException(
            $"usp_Insights_StatusDataQuality returned no row for tenant {customerId}. It always selects exactly one.");
    }

    private static StatusDataQualityProbe ReadProbe(SqlDataReader reader) =>
        new(
            CustomerId: reader.GetInt32(0),
            PastDueSchedules: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            NullStatusRows: reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            UnmappedStatusRows: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            TotalUnknown: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            UnknownPct: reader.IsDBNull(5) ? 0m : reader.GetDecimal(5),
            Verdict: ParseVerdict(reader.GetString(6)),
            DataQualityNote: reader.IsDBNull(7) ? null : reader.GetString(7));

    /// <summary>
    /// Fails closed on an unrecognised verdict. A value this wrapper does not know must never be
    /// treated as the permissive one - defaulting an unknown verdict to "clean" would be exactly
    /// the silent pass the probe exists to prevent.
    /// </summary>
    private static StatusDataQualityVerdict ParseVerdict(string value) => value switch
    {
        "clean" => StatusDataQualityVerdict.Clean,
        "declare_in_data_quality" => StatusDataQualityVerdict.DeclareInDataQuality,
        "RAISE" => StatusDataQualityVerdict.Raise,
        _ => throw new InvalidOperationException($"Unknown Verdict '{value}' from usp_Insights_StatusDataQuality."),
    };
}
