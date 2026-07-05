using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace BunnySubSync.Game;

/// <summary>
/// Sector→route-letter conversion, fuel cost, and voyage-duration estimation,
/// ported from SubmarineTracker (MIT): Utils.cs NumToLetter/SectorsToPath,
/// Voyage.cs FindVoyageStart/CalculateDuration, SubExplPretty.cs CalcTime,
/// Build.cs SubmarineBuild.Speed.
/// </summary>
public static class SectorMath
{
    /// <summary>Fixed per-voyage overhead the game adds, in seconds (12h).</summary>
    public const uint FixedVoyageTimeSeconds = 43200;

    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private static ExcelSheet<SubmarineExploration> ExplorationSheet =>
        Plugin.DataManager.GetExcelSheet<SubmarineExploration>();

    // Map start sectors (StartingPoint rows), descending, so the start of any
    // sector's map is the first start row-id <= the sector.
    private static readonly Lazy<uint[]> ReversedStarts = new(() =>
        ExplorationSheet.Where(r => r.StartingPoint)
                        .Select(r => r.RowId)
                        .OrderByDescending(id => id)
                        .ToArray());

    public static uint FindVoyageStart(uint sector) =>
        ReversedStarts.Value.FirstOrDefault(start => sector >= start);

    public static string NumToLetter(uint num)
    {
        var index = (int)(num - 1); // 0-indexed

        var value = "";
        if (index >= Letters.Length)
            value += Letters[(index / Letters.Length) - 1];

        value += Letters[index % Letters.Length];
        return value;
    }

    /// <summary>Sectors → concatenated route letters, e.g. [32,22] → "OJ" (decision D8: no separator).</summary>
    public static string SectorsToRoute(IReadOnlyList<uint> sectors)
    {
        if (sectors.Count == 0)
            return "";

        var start = FindVoyageStart(sectors[0]);
        return string.Concat(sectors.Select(s => NumToLetter(s - start)));
    }

    /// <summary>
    /// Inverse of <see cref="SectorsToRoute"/> for the simulator: route
    /// letters + a map's start sector → sector row ids. Single letters A–Z
    /// only (covers every real route). Throws on unknown letters.
    /// </summary>
    public static uint[] RouteToSectors(uint mapStartSector, string route)
    {
        return route.Trim().ToUpperInvariant()
                    .Select(c => Letters.IndexOf(c) is var idx && idx >= 0
                                     ? mapStartSector + (uint)idx + 1
                                     : throw new ArgumentException($"'{c}' is not a route letter"))
                    .ToArray();
    }

    /// <summary>Start sectors of every map, for the simulator's map picker: (startSectorRowId, mapName).</summary>
    public static List<(uint StartSector, string MapName)> MapStarts()
    {
        return ExplorationSheet
               .Where(r => r.StartingPoint)
               .Select(r => (r.RowId, r.Map.IsValid ? r.Map.Value.Name.ExtractText() : $"Map {r.Map.RowId}"))
               .ToList();
    }

    /// <summary>Total ceruleum tanks charged for a route (Σ CeruleumTankReq, see Build.cs FuelCost).</summary>
    public static int TankCost(IEnumerable<uint> sectors) =>
        sectors.Sum(s => ExplorationSheet.TryGetRow(s, out var row) ? (int)row.CeruleumTankReq : 0);

    /// <summary>
    /// Estimated voyage duration for a route and build, in seconds — used only
    /// by the explicit "Estimate &amp; queue" path for journal-missed voyages,
    /// never silently. Port of Voyage.CalculateDuration + SubmarineBuild.Speed.
    /// </summary>
    public static uint EstimateDurationSeconds(
        uint[] sectors, int rank, ushort hull, ushort stern, ushort bow, ushort bridge)
    {
        var rankSheet = Plugin.DataManager.GetExcelSheet<SubmarineRank>();
        var partSheet = Plugin.DataManager.GetExcelSheet<SubmarinePart>();

        float speed = (rankSheet.TryGetRow((uint)rank, out var r) ? r.SpeedBonus : 0)
                      + PartSpeed(partSheet, hull) + PartSpeed(partSheet, stern)
                      + PartSpeed(partSheet, bow) + PartSpeed(partSheet, bridge);

        return EstimateDurationSecondsAtSpeed(sectors, speed);
    }

    /// <summary>Duration for a route at a known speed — the backfill's
    /// fallback path when the sub's build row is gone from the source DB.</summary>
    public static uint EstimateDurationSecondsAtSpeed(uint[] sectors, float speed)
    {
        if (sectors.Length is 0 or > 5)
            return 0;

        var start = ExplorationSheet.GetRow(FindVoyageStart(sectors[0]));
        var rows = sectors.Select(s => ExplorationSheet.GetRow(s)).ToArray();

        var duration = CalcTime(start, rows[0], speed);
        for (var i = 1; i < rows.Length; i++)
            duration += CalcTime(rows[i - 1], rows[i], speed);

        return duration + FixedVoyageTimeSeconds;
    }

    private static int PartSpeed(ExcelSheet<SubmarinePart> sheet, ushort partId) =>
        sheet.TryGetRow(partId, out var part) ? part.Speed : 0;

    // SubExplPretty.cs: travel time between two sectors + survey time at the destination.
    private static uint CalcTime(SubmarineExploration from, SubmarineExploration to, float speed)
    {
        if (speed < 1)
            speed = 1;

        var distance = Vector3.Distance(new Vector3(from.X, from.Y, from.Z), new Vector3(to.X, to.Y, to.Z));
        var voyage = (uint)Math.Floor(distance * 3990 / (speed * 100) * 60);
        var survey = (uint)Math.Floor(to.SurveyDurationmin * 7000 / (speed * 100) * 60);
        return voyage + survey;
    }
}
