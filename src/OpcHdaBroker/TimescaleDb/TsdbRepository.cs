// ═══════════════════════════════════════════════════════════════════════════
// TIMESACALEDB REPOSITORY
// ───────────────────────────────────────────────────────────────────────────
// Handles all TimescaleDB operations: schema creation, bulk inserts, and
// deduplication via INSERT ... ON CONFLICT DO NOTHING.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Serilog;

namespace OpcHdaBroker.TimescaleDb
{
    /// <summary>
    /// Data point ready for TimescaleDB insertion.
    /// </summary>
    public class TsdbDataPoint
    {
        public DateTime  Timestamp { get; set; }
        public string    Tag      { get; set; }
        public double?   Value    { get; set; }
        public string    ValueText { get; set; }
        public string    Quality  { get; set; }
        public string    BrokerId { get; set; }
        public string    ValueType { get; set; }
    }

    /// <summary>
    /// Manages TimescaleDB schema and data operations.
    /// </summary>
    public class TsdbRepository : IDisposable
    {
        private static readonly ILogger Log = Serilog.Log.ForContext<TsdbRepository>();
        private readonly string        _connectionString;
        private readonly string        _brokerId;

        public TsdbRepository(string connectionString, string brokerId)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _brokerId         = brokerId ?? "default";
        }

        public void EnsureSchema()
        {
            using var conn = CreateConnection();
            Log.Information("Ensuring TimescaleDB schema...");

            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS hda_data (
                    time        TIMESTAMPTZ NOT NULL,
                    tag         TEXT        NOT NULL,
                    value       DOUBLE PRECISION,
                    value_text  TEXT,
                    quality     TEXT,
                    broker_id   TEXT,
                    value_type  TEXT,
                    PRIMARY KEY (time, tag)
                );");

            ExecuteNonQuery(conn, @"
                SELECT create_hypertable('hda_data', 'time',
                    if_not_exists := TRUE,
                    chunk_interval := INTERVAL '1 day');");

            ExecuteNonQuery(conn, @"
                CREATE INDEX IF NOT EXISTS idx_hda_data_tag_time
                ON hda_data (tag, time DESC);");

            Log.Information("TimescaleDB schema ready.");
        }

        public int BulkUpsert(IEnumerable<TsdbDataPoint> points)
        {
            var pointList = points.ToList();
            if (pointList.Count == 0) return 0;

            using var conn = CreateConnection();
            var totalInserted = 0;

            using (var writer = conn.BeginBinaryImport(
                "COPY hda_data (time, tag, value, value_text, quality, broker_id, value_type) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var pt in pointList)
                {
                    try
                    {
                        writer.StartRow();
                        writer.Write(pt.Timestamp, NpgsqlDbType.TimestampTz);
                        writer.Write(pt.Tag ?? (object)DBNull.Value, NpgsqlDbType.Text);
                        writer.Write(pt.Value ?? (object)DBNull.Value, NpgsqlDbType.Double);
                        writer.Write(pt.ValueText ?? (object)DBNull.Value, NpgsqlDbType.Text);
                        writer.Write(pt.Quality ?? (object)DBNull.Value, NpgsqlDbType.Text);
                        writer.Write(pt.BrokerId ?? (object)DBNull.Value, NpgsqlDbType.Text);
                        writer.Write(pt.ValueType ?? (object)DBNull.Value, NpgsqlDbType.Text);
                        totalInserted++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to write point for tag {Tag}", pt.Tag);
                    }
                }

                writer.Complete();
            }

            Log.Debug("Bulk upserted {Count} points", totalInserted);
            return totalInserted;
        }

        public int BulkUpsertFallback(IEnumerable<TsdbDataPoint> points, int batchSize = 500)
        {
            var pointList = points.ToList();
            if (pointList.Count == 0) return 0;

            using var conn = CreateConnection();
            var totalInserted = 0;
            var batches = pointList
                .Select((p, i) => new { p, i })
                .GroupBy(x => x.i / batchSize)
                .Select(g => g.Select(x => x.p).ToList())
                .ToList();

            foreach (var batch in batches)
            {
                var sb = new StringBuilder();
                sb.Append(@"
                    INSERT INTO hda_data (time, tag, value, value_text, quality, broker_id, value_type)
                    VALUES ");

                var values = new List<string>();
                var paramsList = new List<NpgsqlParameter>();
                int idx = 0;

                foreach (var pt in batch)
                {
                    values.Add($"(@t{idx}, @tag{idx}, @val{idx}, @vtxt{idx}, @q{idx}, @bid{idx}, @vtype{idx})");
                    paramsList.Add(new NpgsqlParameter($"@t{idx}",   pt.Timestamp));
                    paramsList.Add(new NpgsqlParameter($"@tag{idx}",  pt.Tag));
                    paramsList.Add(new NpgsqlParameter($"@val{idx}",  pt.Value   ?? (object)DBNull.Value));
                    paramsList.Add(new NpgsqlParameter($"@vtxt{idx}", pt.ValueText ?? (object)DBNull.Value));
                    paramsList.Add(new NpgsqlParameter($"@q{idx}",    pt.Quality ?? (object)DBNull.Value));
                    paramsList.Add(new NpgsqlParameter($"@bid{idx}",  pt.BrokerId ?? (object)DBNull.Value));
                    paramsList.Add(new NpgsqlParameter($"@vtype{idx}",pt.ValueType ?? (object)DBNull.Value));
                    idx++;
                }

                sb.Append(string.Join(",", values));
                sb.Append(" ON CONFLICT (time, tag) DO NOTHING RETURNING time;");

                try
                {
                    using (var cmd = new NpgsqlCommand(sb.ToString(), conn))
                    {
                        cmd.Parameters.AddRange(paramsList.ToArray());
                        var count = cmd.ExecuteNonQuery();
                        totalInserted += count;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Batch insert failed, trying one-by-one");
                    foreach (var pt in batch)
                    {
                        try
                        {
                            InsertSingle(conn, pt);
                            totalInserted++;
                        }
                        catch { }
                    }
                }
            }

            Log.Debug("Fallback bulk upserted {Count} points", totalInserted);
            return totalInserted;
        }

        public bool TestConnection()
        {
            try
            {
                using var conn = CreateConnection();
                using (var cmd = new NpgsqlCommand("SELECT 1", conn))
                {
                    var result = cmd.ExecuteScalar();
                    return result != null && result.ToString() == "1";
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TimescaleDB connection test failed");
                return false;
            }
        }

        public long GetRowCount()
        {
            try
            {
                using var conn = CreateConnection();
                using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM hda_data", conn))
                {
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to get row count");
                return -1;
            }
        }

        public int GetTagCount()
        {
            try
            {
                using var conn = CreateConnection();
                using (var cmd = new NpgsqlCommand("SELECT COUNT(DISTINCT tag) FROM hda_data", conn))
                {
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to get tag count");
                return -1;
            }
        }

        public DateTime? GetOldestTimestamp()
        {
            try
            {
                using var conn = CreateConnection();
                using (var cmd = new NpgsqlCommand("SELECT MIN(time) FROM hda_data", conn))
                {
                    var result = cmd.ExecuteScalar();
                    if (result == null || result == DBNull.Value) return null;
                    return Convert.ToDateTime(result);
                }
            }
            catch { return null; }
        }

        public DateTime? GetNewestTimestamp()
        {
            try
            {
                using var conn = CreateConnection();
                using (var cmd = new NpgsqlCommand("SELECT MAX(time) FROM hda_data", conn))
                {
                    var result = cmd.ExecuteScalar();
                    if (result == null || result == DBNull.Value) return null;
                    return Convert.ToDateTime(result);
                }
            }
            catch { return null; }
        }

        public void TruncateData()
        {
            using var conn = CreateConnection();
            ExecuteNonQuery(conn, "TRUNCATE TABLE hda_data;");
            Log.Warning("hda_data truncated");
        }

        private NpgsqlConnection CreateConnection()
        {
            var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            return conn;
        }

        private void InsertSingle(NpgsqlConnection conn, TsdbDataPoint pt)
        {
            const string sql = @"
                INSERT INTO hda_data (time, tag, value, value_text, quality, broker_id, value_type)
                VALUES (@t, @tag, @val, @vtxt, @q, @bid, @vtype)
                ON CONFLICT (time, tag) DO NOTHING";

            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@t",    pt.Timestamp);
                cmd.Parameters.AddWithValue("@tag",  pt.Tag ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@val",  pt.Value   ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@vtxt", pt.ValueText ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@q",    pt.Quality ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@bid",  pt.BrokerId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@vtype",pt.ValueType ?? (object)DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        private void ExecuteNonQuery(NpgsqlConnection conn, string sql)
        {
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
        }
    }
}
