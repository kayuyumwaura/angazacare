using System.Data.Common;
using Npgsql;

namespace AngazaCare.Data;

/// <summary>
/// Thin ADO.NET helper over Npgsql: raw SQL, explicit parameters, explicit mapping.
/// Every query in the app goes through here so there is one place to log or trace.
/// </summary>
public sealed class Db(NpgsqlDataSource source)
{
    public NpgsqlDataSource Source { get; } = source;

    public async Task<List<T>> QueryAsync<T>(string sql, Func<DbDataReader, T> map, object? args = null, NpgsqlConnection? conn = null, NpgsqlTransaction? tx = null)
    {
        await using var owned = conn is null ? await Source.OpenConnectionAsync() : null;
        await using var cmd = Command(sql, args, conn ?? owned!, tx);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync()) rows.Add(map(reader));
        return rows;
    }

    public async Task<T?> SingleAsync<T>(string sql, Func<DbDataReader, T> map, object? args = null, NpgsqlConnection? conn = null, NpgsqlTransaction? tx = null)
        => (await QueryAsync(sql, map, args, conn, tx)).FirstOrDefault();

    public async Task<int> ExecuteAsync(string sql, object? args = null, NpgsqlConnection? conn = null, NpgsqlTransaction? tx = null)
    {
        await using var owned = conn is null ? await Source.OpenConnectionAsync() : null;
        await using var cmd = Command(sql, args, conn ?? owned!, tx);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql, object? args = null, NpgsqlConnection? conn = null, NpgsqlTransaction? tx = null)
    {
        await using var owned = conn is null ? await Source.OpenConnectionAsync() : null;
        await using var cmd = Command(sql, args, conn ?? owned!, tx);
        var value = await cmd.ExecuteScalarAsync();
        if (value is null || value is DBNull) return default;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target);
    }

    /// <summary>Runs work inside one transaction and commits only if it completes.</summary>
    public async Task<T> InTransactionAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work)
    {
        await using var conn = await Source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var result = await work(conn, tx);
        await tx.CommitAsync();
        return result;
    }

    private static NpgsqlCommand Command(string sql, object? args, NpgsqlConnection conn, NpgsqlTransaction? tx)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        if (args is null) return cmd;
        foreach (var prop in args.GetType().GetProperties())
        {
            var value = prop.GetValue(args);
            cmd.Parameters.Add(value switch
            {
                null => new NpgsqlParameter(prop.Name, DBNull.Value),
                DateOnly d => new NpgsqlParameter(prop.Name, d),
                _ => new NpgsqlParameter(prop.Name, value),
            });
        }
        return cmd;
    }
}

public static class ReaderExtensions
{
    public static string? Str(this DbDataReader r, string col) => r[col] is DBNull ? null : (string)r[col];
    public static int? Int(this DbDataReader r, string col) => r[col] is DBNull ? null : Convert.ToInt32(r[col]);
    public static long? Long(this DbDataReader r, string col) => r[col] is DBNull ? null : Convert.ToInt64(r[col]);
    public static decimal? Dec(this DbDataReader r, string col) => r[col] is DBNull ? null : Convert.ToDecimal(r[col]);
    public static bool Bool(this DbDataReader r, string col) => r[col] is not DBNull && (bool)r[col];
    public static DateTime? Time(this DbDataReader r, string col) => r[col] is DBNull ? null : ((DateTime)r[col]).ToUniversalTime();
    public static DateOnly? Date(this DbDataReader r, string col) => r[col] switch
    {
        DBNull => null,
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        var other => DateOnly.Parse(other.ToString()!),
    };
    public static string[] Arr(this DbDataReader r, string col) => r[col] is DBNull ? Array.Empty<string>() : (string[])r[col];
}
