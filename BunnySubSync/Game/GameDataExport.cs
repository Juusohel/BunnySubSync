using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using BunnySubSync.Api;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace BunnySubSync.Game;

/// <summary>
/// Builds the game-data upload (POST /api/plugin/v1/gamedata) from the three
/// submarine sheets. Pure: the caller passes the sheets (read in English) and
/// the game version, so nothing here touches Dalamud — see
/// <see cref="GameDataUploader"/> for the in-game side.
///
/// The field set mirrors what SubmarineTracker (MIT) reads in Data/Build.cs,
/// Data/Voyage.cs, Data/Sectors.cs and SubExplPretty.cs — the platform's
/// route planner ports those formulas.
/// </summary>
public static class GameDataExport
{
    // Part row id → its item id, for the part's display name (the
    // SubmarinePart sheet has no name column). Ported from SubmarineTracker
    // (MIT, Copyright (c) 2023 Infi): Data/Submarine.cs PartIdToItemId. Each
    // class is four rows in bow, bridge, hull, stern order; 21–40 are the
    // modified versions of 1–20. A part added by a later patch has no entry
    // and goes up with a null name until this map is extended.
    private static readonly Dictionary<uint, uint> PartIdToItemId = new()
    {
        // Shark
        { 1, 21792 }, { 2, 21793 }, { 3, 21794 }, { 4, 21795 },
        // Unkiu
        { 5, 21796 }, { 6, 21797 }, { 7, 21798 }, { 8, 21799 },
        // Whale
        { 9, 22526 }, { 10, 22527 }, { 11, 22528 }, { 12, 22529 },
        // Coelacanth
        { 13, 23903 }, { 14, 23904 }, { 15, 23905 }, { 16, 23906 },
        // Syldra
        { 17, 24344 }, { 18, 24345 }, { 19, 24346 }, { 20, 24347 },
        // Modified, same order
        { 21, 24348 }, { 22, 24349 }, { 23, 24350 }, { 24, 24351 },
        { 25, 24352 }, { 26, 24353 }, { 27, 24354 }, { 28, 24355 },
        { 29, 24356 }, { 30, 24357 }, { 31, 24358 }, { 32, 24359 },
        { 33, 24360 }, { 34, 24361 }, { 35, 24362 }, { 36, 24363 },
        { 37, 24364 }, { 38, 24365 }, { 39, 24366 }, { 40, 24367 },
    };

    // The saved file is the upload payload, indented and with readable
    // apostrophes so a committed fixture diffs cleanly between game versions.
    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static GameDataUpload Build(
        string gameVersion,
        ExcelSheet<SubmarineExploration> exploration,
        ExcelSheet<SubmarinePart> parts,
        ExcelSheet<SubmarineRank> ranks,
        ExcelSheet<Item> items)
    {
        // Rows past the last map are empty placeholders (map 0). Row 0 is NOT
        // one of them: it's the first map's starting point, which the first
        // leg's distance is measured from.
        var sectors = exploration.Where(r => r.Map.RowId != 0).ToList();
        var mapStarts = sectors.Where(r => r.StartingPoint)
                               .Select(r => r.RowId)
                               .OrderByDescending(id => id)
                               .ToArray();

        return new GameDataUpload
        {
            GameVersion = gameVersion,
            Exploration = sectors.Select(r => new GameDataSector
            {
                SectorId = checked((ushort)r.RowId),
                MapId = checked((ushort)r.Map.RowId),
                MapName = r.Map.IsValid ? r.Map.Value.Name.ExtractText() : string.Empty,
                Name = r.Destination.ExtractText(),
                Letter = r.StartingPoint ? null : Letter(r.RowId, mapStarts),
                StartingPoint = r.StartingPoint,
                RankRequired = r.RankReq,
                ExpReward = r.ExpReward,
                X = r.X,
                Y = r.Y,
                Z = r.Z,
                SurveyDistance = r.SurveyDistance,
                SurveyDurationMin = r.SurveyDurationmin,
                CeruleumTankReq = r.CeruleumTankReq,
            }).ToList(),
            // Row 0 of both sheets is the empty null row.
            Parts = parts.Where(p => p.RowId != 0).Select(p => new GameDataPart
            {
                PartId = checked((ushort)p.RowId),
                Name = PartIdToItemId.TryGetValue(p.RowId, out var itemId) && items.TryGetRow(itemId, out var item)
                           ? item.Name.ExtractText()
                           : null,
                Slot = p.Slot,
                Class = p.Class,
                Rank = p.Rank,
                Components = p.Components,
                RepairMaterials = p.RepairMaterials,
                Surveillance = p.Surveillance,
                Retrieval = p.Retrieval,
                Speed = p.Speed,
                Range = p.Range,
                Favor = p.Favor,
            }).ToList(),
            // Ranks past the last real one (capacity 0) stay in: the planner's
            // "last valid rank" rule reads them as SubmarineTracker does.
            Ranks = ranks.Where(r => r.RowId != 0).Select(r => new GameDataRank
            {
                Rank = checked((ushort)r.RowId),
                ExpToNext = r.ExpToNext,
                Capacity = r.Capacity,
                SurveillanceBonus = r.SurveillanceBonus,
                RetrievalBonus = r.RetrievalBonus,
                SpeedBonus = r.SpeedBonus,
                RangeBonus = r.RangeBonus,
                FavorBonus = r.FavorBonus,
            }).ToList(),
        };
    }

    public static string ToFileJson(GameDataUpload upload) =>
        JsonSerializer.Serialize(upload, FileJsonOptions);

    // Same letters as SectorMath.SectorsToRoute: the offset from the map's
    // start row (the first start at or below the sector), so they match the
    // route strings the plugin pushes.
    private static string? Letter(uint sector, uint[] mapStartsDescending)
    {
        var start = mapStartsDescending.FirstOrDefault(s => sector >= s);
        var offset = sector - start;
        return offset is >= 1 and <= 702 ? SectorMath.NumToLetter(offset) : null;
    }
}
