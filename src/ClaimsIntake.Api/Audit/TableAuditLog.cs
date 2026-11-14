using System.Globalization;
using System.Text.Json;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using ClaimsIntake.Domain.Assessment;

namespace ClaimsIntake.Api.Audit;

/// <summary>The few table operations the audit log needs. The seam that keeps the Azure SDK out of the unit tests.</summary>
public interface IAuditTable
{
    Task EnsureExistsAsync(CancellationToken cancellationToken);

    Task AddAsync(TableEntity entity, CancellationToken cancellationToken);

    /// <summary>The entities in a partition, in row-key order.</summary>
    Task<IReadOnlyList<TableEntity>> QueryPartitionAsync(string partitionKey, CancellationToken cancellationToken);
}

/// <summary>
/// The audit log on Azure Table Storage, used when <c>Audit:Table:Endpoint</c> is set. One partition per claim, one row
/// per tool call, row keys that sort by time. Authenticates with <c>DefaultAzureCredential</c> (the managed identity
/// holds Storage Table Data Contributor), with no keys. Compiled and tested against a fake table only (ADR 0006).
/// </summary>
public sealed class TableStorageAuditLog(IAuditTable table) : IAuditLog
{
    private readonly SemaphoreSlim _created = new(1, 1);
    private bool _tableExists;

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await EnsureTableAsync(cancellationToken);
        await table.AddAsync(ToEntity(entry), cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> ForClaimAsync(Guid claimId, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);
        var rows = await table.QueryPartitionAsync(PartitionKey(claimId), cancellationToken);
        return [.. rows.Select(r => FromEntity(claimId, r))];
    }

    internal static string PartitionKey(Guid claimId) => claimId.ToString("D", CultureInfo.InvariantCulture);

    internal static TableEntity ToEntity(AuditEntry entry) =>
        new(PartitionKey(entry.ClaimId),
            // Ticks first, so rows sort by time; a random suffix keeps two calls in the same tick apart.
            $"{entry.Timestamp.UtcTicks:D19}-{Guid.NewGuid().ToString("N")[..8]}")
        {
            ["Tool"] = entry.Tool,
            ["Arguments"] = JsonSerializer.Serialize(entry.Arguments),
            ["ResultSummary"] = entry.ResultSummary,
            ["DurationMs"] = entry.DurationMs,
            ["StartedAt"] = entry.Timestamp,
            ["Succeeded"] = entry.Succeeded,
        };

    internal static AuditEntry FromEntity(Guid claimId, TableEntity row) =>
        new(claimId,
            row.GetString("Tool"),
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.GetString("Arguments") ?? "{}") ?? [],
            row.GetString("ResultSummary") ?? "",
            row.GetDouble("DurationMs") ?? 0,
            row.GetDateTimeOffset("StartedAt") ?? DateTimeOffset.MinValue,
            row.GetBoolean("Succeeded") ?? true);

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (_tableExists)
        {
            return;
        }

        await _created.WaitAsync(cancellationToken);
        try
        {
            if (!_tableExists)
            {
                await table.EnsureExistsAsync(cancellationToken);
                _tableExists = true;
            }
        }
        finally
        {
            _created.Release();
        }
    }
}

/// <summary>The real <see cref="IAuditTable"/>, over a <see cref="TableClient"/>. Never run against a live account.</summary>
public sealed class TableClientAuditTable(TableClient client) : IAuditTable
{
    public async Task EnsureExistsAsync(CancellationToken cancellationToken) =>
        await client.CreateIfNotExistsAsync(cancellationToken);

    public async Task AddAsync(TableEntity entity, CancellationToken cancellationToken) =>
        await client.AddEntityAsync(entity, cancellationToken);

    public async Task<IReadOnlyList<TableEntity>> QueryPartitionAsync(string partitionKey, CancellationToken cancellationToken)
    {
        // The partition key is a Guid formatted by this class, never free text, so the filter cannot be injected into.
        if (!Guid.TryParse(partitionKey, out var id))
        {
            throw new ArgumentException("The partition key must be a claim id.", nameof(partitionKey));
        }

        var rows = new List<TableEntity>();
        await foreach (var row in client.QueryAsync<TableEntity>($"PartitionKey eq '{id:D}'", cancellationToken: cancellationToken))
        {
            rows.Add(row);
        }

        return [.. rows.OrderBy(r => r.RowKey, StringComparer.Ordinal)];
    }
}

public static class AuditLogServiceCollectionExtensions
{
    public const string TableEndpointSetting = "Audit:Table:Endpoint";

    /// <summary>
    /// Registers the audit log: in memory by default, Azure Table Storage when <c>Audit:Table:Endpoint</c> is set
    /// (an https URL such as https://&lt;account&gt;.table.core.windows.net). <c>Audit:Table:Name</c> defaults to <c>auditlog</c>.
    /// </summary>
    public static IServiceCollection AddAuditLog(this IServiceCollection services, IConfiguration configuration)
    {
        var endpoint = configuration[TableEndpointSetting];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            services.AddSingleton<IAuditLog, InMemoryAuditLog>();
            return services;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{TableEndpointSetting} must be an https URL.");
        }

        var tableName = configuration["Audit:Table:Name"] is { Length: > 0 } name ? name : "auditlog";
        var clientId = configuration["Intake:ManagedIdentityClientId"] is { Length: > 0 } id ? id : null;
        services.AddSingleton<IAuditLog>(_ => new TableStorageAuditLog(new TableClientAuditTable(
            new TableClient(uri, tableName, new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId })))));
        return services;
    }
}
