using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BunnySubSync.Api;
using Lumina.Excel.Sheets;

namespace BunnySubSync.Game;

/// <summary>
/// G5: turns SubmarineTracker's SQLite history into a CSV the platform's
/// existing web import understands. Read-only with respect to the source —
/// the DB (plus -wal/-shm) is copied to temp before opening, because
/// SubmarineTracker may hold it open under WAL mode.
/// </summary>
public sealed class BackfillService(Configuration configuration, SyncService sync)
{
    /// <summary>Highest SubmarineTracker PRAGMA user_version this exporter was
    /// written against (their Database.cs Migrate()). Anything newer →
    /// refuse; mis-parsing silently is the failure mode to fear.</summary>
    private const int MaxSupportedSchemaVersion = 1;

    /// <summary>Speed fallback when the sub's build row is gone — a mid-game
    /// build; times are estimates and stamped as such either way.</summary>
    private const float FallbackSpeed = 100f;

    public sealed record VoyageRow(
        string SubmarineName,
        string? FreeCompanyName,
        string Route,
        DateTime DeployedAtUtc,
        DateTime CollectedAtUtc,
        int DurationMinutes,
        int Tanks,
        List<(string ItemName, int Quantity)> Items);

    public sealed record ScanResult(
        List<VoyageRow> Mappable,
        int TotalVoyages,
        int SkippedUnmapped,
        DateTime? OldestUtc,
        DateTime? NewestUtc);

    public static string DefaultDbPath()
    {
        var configDir = Plugin.PluginInterface.ConfigDirectory;
        return Path.Join(configDir.Parent!.FullName, "SubmarineTracker", "submarine-sqlite.db");
    }

    /// <summary>Scan the DB and build the mappable voyage list. Throws with a
    /// user-readable message on schema/file problems.</summary>
    public ScanResult Scan(string dbPath, DateTime? fromUtc, DateTime? toUtc)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"No SubmarineTracker database at {dbPath}");
        if (sync.Last == null)
            throw new InvalidOperationException("Refresh the server inventory first (Mapping tab) — platform names come from it.");

        var tempDb = CopyToTemp(dbPath);
        try
        {
            using var conn = new SQLiteConnection(new SQLiteConnectionStringBuilder
            {
                DataSource = tempDb,
                ReadOnly = true,
                Pooling = false,
            }.ToString());
            conn.Open();

            var schemaVersion = ReadUserVersion(conn);
            if (schemaVersion > MaxSupportedSchemaVersion)
                throw new InvalidDataException(
                    $"SubmarineTracker DB schema v{schemaVersion} not supported (max v{MaxSupportedSchemaVersion}) — update BunnySubSync.");

            var builds = ReadBuilds(conn);
            var voyages = ReadVoyages(conn, fromUtc, toUtc);
            return Resolve(voyages, builds);
        }
        finally
        {
            TryDeleteTempCopies(tempDb);
        }
    }

    public static string BuildCsv(List<VoyageRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("Submarine,Free Company,Route,Deployed At,Collected At,Route Duration (min),Ceruleum Tanks,Notes");
        for (var i = 1; i <= 10; i++)
            sb.Append($",Item {i},Qty {i}");
        sb.Append('\n');

        foreach (var row in rows.OrderBy(r => r.CollectedAtUtc))
        {
            sb.Append(Csv(row.SubmarineName)).Append(',');
            sb.Append(Csv(row.FreeCompanyName ?? "")).Append(',');
            sb.Append(Csv(row.Route)).Append(',');
            sb.Append(row.DeployedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(row.CollectedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(row.DurationMinutes).Append(',');
            sb.Append(row.Tanks).Append(',');
            sb.Append(Csv("Backfilled from SubmarineTracker (times estimated)"));

            for (var i = 0; i < 10; i++)
            {
                if (i < row.Items.Count)
                    sb.Append(',').Append(Csv(row.Items[i].ItemName)).Append(',').Append(row.Items[i].Quantity);
                else
                    sb.Append(",,");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    // -------------------------------------------------------------------------

    private sealed record RawSector(uint Sector, uint PrimaryItem, int PrimaryCount, uint AdditionalItem, int AdditionalCount);

    private sealed record RawVoyage(ulong FcId, uint SubRegister, long ReturnUnix, List<RawSector> Sectors);

    private sealed record SubBuild(string Name, int Rank, ushort Hull, ushort Stern, ushort Bow, ushort Bridge);

    private static string CopyToTemp(string dbPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "BunnySubSync");
        Directory.CreateDirectory(tempDir);
        var tempDb = Path.Combine(tempDir, "st-backfill.db");

        File.Copy(dbPath, tempDb, overwrite: true);
        // Under WAL mode the newest transactions live in the sidecar files.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = dbPath + suffix;
            var target = tempDb + suffix;
            if (File.Exists(sidecar))
                File.Copy(sidecar, target, overwrite: true);
            else if (File.Exists(target))
                File.Delete(target);
        }

        return tempDb;
    }

    private static void TryDeleteTempCopies(string tempDb)
    {
        try
        {
            foreach (var f in new[] { tempDb, tempDb + "-wal", tempDb + "-shm" })
            {
                if (File.Exists(f))
                    File.Delete(f);
            }
        }
        catch
        {
            // temp files; best effort
        }
    }

    private static int ReadUserVersion(SQLiteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static Dictionary<(ulong FcId, uint Register), SubBuild> ReadBuilds(SQLiteConnection conn)
    {
        var builds = new Dictionary<(ulong, uint), SubBuild>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FreeCompanyId, SubmarineId, Name, Rank, Hull, Stern, Bow, Bridge FROM submarine;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var fcId = DecodeMsgPackUInt((byte[])reader[0]);
            builds[(fcId, (uint)reader.GetInt64(1))] = new SubBuild(
                reader.GetString(2),
                reader.GetInt32(3),
                (ushort)reader.GetInt64(4),
                (ushort)reader.GetInt64(5),
                (ushort)reader.GetInt64(6),
                (ushort)reader.GetInt64(7));
        }

        return builds;
    }

    private static List<RawVoyage> ReadVoyages(SQLiteConnection conn, DateTime? fromUtc, DateTime? toUtc)
    {
        // One loot row per sector; (FreeCompanyId, SubmarineId, Return)
        // identifies a voyage; rowid preserves sector visit order.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FreeCompanyId, SubmarineId, Return, Sector,
                   PrimaryItem, PrimaryCount, AdditionalItem, AdditionalCount
            FROM loot
            WHERE Valid = 1 AND Sector > 0 AND Return > 0
            ORDER BY Return, rowid;
            """;

        var fromUnix = fromUtc is { } f ? new DateTimeOffset(f, TimeSpan.Zero).ToUnixTimeSeconds() : long.MinValue;
        var toUnix = toUtc is { } t ? new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds() : long.MaxValue;

        var voyages = new Dictionary<(ulong, uint, long), RawVoyage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var returnUnix = reader.GetInt64(2);
            if (returnUnix < fromUnix || returnUnix > toUnix)
                continue;

            var fcId = DecodeMsgPackUInt((byte[])reader[0]);
            var register = (uint)reader.GetInt64(1);
            var key = (fcId, register, returnUnix);
            if (!voyages.TryGetValue(key, out var voyage))
                voyages[key] = voyage = new RawVoyage(fcId, register, returnUnix, []);

            voyage.Sectors.Add(new RawSector(
                (uint)reader.GetInt64(3),
                (uint)reader.GetInt64(4),
                reader.GetInt32(5),
                (uint)reader.GetInt64(6),
                reader.GetInt32(7)));
        }

        return [.. voyages.Values];
    }

    private ScanResult Resolve(List<RawVoyage> voyages, Dictionary<(ulong, uint), SubBuild> builds)
    {
        var inventory = sync.Last!;
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var mappable = new List<VoyageRow>();
        var skipped = 0;

        foreach (var voyage in voyages)
        {
            // Mapped = FC chosen + sub linked (G3 mapping). The Enabled
            // toggles gate live pushes, not backfill exports.
            if (!configuration.FcMappings.TryGetValue(voyage.FcId, out var mapping)
                || mapping.PlatformFcId == null
                || !mapping.Subs.TryGetValue(voyage.SubRegister, out var link)
                || link.PlatformSubmarineId == null)
            {
                skipped++;
                continue;
            }

            var subName = inventory.Submarines.FirstOrDefault(s => s.Id == link.PlatformSubmarineId)?.Name;
            if (subName == null)
            {
                skipped++; // link points at a sub the server no longer has
                continue;
            }

            var fcName = mapping.PlatformFcId == 0
                ? null
                : inventory.FreeCompanies.FirstOrDefault(f => f.Id == mapping.PlatformFcId)?.Name;

            var sectors = voyage.Sectors.Select(s => s.Sector).ToArray();
            var durationSeconds = builds.TryGetValue((voyage.FcId, voyage.SubRegister), out var build)
                ? SectorMath.EstimateDurationSeconds(sectors, build.Rank, build.Hull, build.Stern, build.Bow, build.Bridge)
                : SectorMath.EstimateDurationSecondsAtSpeed(sectors, FallbackSpeed);
            if (durationSeconds == 0)
            {
                skipped++; // unparseable route (>5 sectors would be corrupt data)
                continue;
            }

            var collected = DateTimeOffset.FromUnixTimeSeconds(voyage.ReturnUnix).UtcDateTime;

            var items = voyage.Sectors
                              .SelectMany(s => new[] { (s.PrimaryItem, s.PrimaryCount), (s.AdditionalItem, s.AdditionalCount) })
                              .Where(p => p.Item1 > 0 && p.Item2 > 0)
                              .GroupBy(p => p.Item1)
                              .Select(g => (
                                  ItemName: itemSheet.TryGetRow(g.Key, out var item)
                                      ? item.Name.ExtractText()
                                      : $"unknown item {g.Key}",
                                  Quantity: g.Sum(p => p.Item2)))
                              .ToList();

            mappable.Add(new VoyageRow(
                subName,
                fcName,
                SectorMath.SectorsToRoute(sectors),
                collected.AddSeconds(-durationSeconds),
                collected,
                (int)(durationSeconds / 60),
                SectorMath.TankCost(sectors),
                items));
        }

        return new ScanResult(
            mappable,
            voyages.Count,
            skipped,
            voyages.Count > 0 ? DateTimeOffset.FromUnixTimeSeconds(voyages.Min(v => v.ReturnUnix)).UtcDateTime : null,
            voyages.Count > 0 ? DateTimeOffset.FromUnixTimeSeconds(voyages.Max(v => v.ReturnUnix)).UtcDateTime : null);
    }

    /// <summary>SubmarineTracker stores FreeCompanyId as a MessagePack-encoded
    /// u64 blob; decoding the unsigned family is 10 lines — no dependency.</summary>
    private static ulong DecodeMsgPackUInt(byte[] bytes)
    {
        return bytes[0] switch
        {
            <= 0x7f => bytes[0],
            0xcc => bytes[1],
            0xcd => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(1)),
            0xce => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1)),
            0xcf => BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(1)),
            _ => throw new InvalidDataException($"Unexpected MessagePack lead byte 0x{bytes[0]:x2} in FreeCompanyId"),
        };
    }

    private static string Csv(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            return '"' + field.Replace("\"", "\"\"") + '"';
        return field;
    }
}
