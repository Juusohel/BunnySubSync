using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BunnySubSync.Api;

// DTOs mirror the server's wire contract exactly — field names/shapes are
// not ours to "improve".

public sealed class SyncResponse
{
    [JsonPropertyName("server_time")] public DateTimeOffset ServerTime { get; set; }
    [JsonPropertyName("user")] public SyncUser User { get; set; } = new();
    [JsonPropertyName("multi_fc_mode")] public bool MultiFcMode { get; set; }
    [JsonPropertyName("free_companies")] public List<SyncFreeCompany> FreeCompanies { get; set; } = new();
    [JsonPropertyName("submarines")] public List<SyncSubmarine> Submarines { get; set; } = new();
    [JsonPropertyName("salvage_items")] public List<SyncSalvageItem> SalvageItems { get; set; } = new();
}

public sealed class SyncUser
{
    [JsonPropertyName("pid")] public string Pid { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

public sealed class SyncFreeCompany
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("world")] public string? World { get; set; }
    [JsonPropertyName("is_primary")] public bool IsPrimary { get; set; }

    // True when the key-holder is an accepted member of this FC (owned by
    // another user); own FCs are false.
    [JsonPropertyName("shared")] public bool Shared { get; set; }

    // True when the membership is view-only: listed for the stats tab but
    // never a mapping/push target (the server rejects such pushes anyway).
    [JsonPropertyName("read_only")] public bool ReadOnly { get; set; }

    // The FC owner's name on shared FCs, so two identically-named shared FCs
    // are distinguishable in the mapping dropdown; null for own FCs.
    [JsonPropertyName("owner_name")] public string? OwnerName { get; set; }
}

public sealed class SyncSubmarine
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("free_company_id")] public int? FreeCompanyId { get; set; }
    [JsonPropertyName("rank")] public int? Rank { get; set; }
    [JsonPropertyName("hull_type")] public string? HullType { get; set; }

    // Mirror the sub's FC flags: a shared sub belongs to the FC owner but a
    // writer member may push into it; read_only mirrors a viewer membership.
    [JsonPropertyName("shared")] public bool Shared { get; set; }
    [JsonPropertyName("read_only")] public bool ReadOnly { get; set; }
}

public sealed class SyncSalvageItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("ffxiv_item_id")] public uint? FfxivItemId { get; set; }
    [JsonPropertyName("item_name")] public string ItemName { get; set; } = string.Empty;
    [JsonPropertyName("vendor_gil_value")] public int VendorGilValue { get; set; }
}

public sealed class PushRow
{
    [JsonPropertyName("external_voyage_id")] public string ExternalVoyageId { get; set; } = string.Empty;
    [JsonPropertyName("submarine_id")] public int SubmarineId { get; set; }
    [JsonPropertyName("client_submarine_name")] public string? ClientSubmarineName { get; set; }
    [JsonPropertyName("route")] public string Route { get; set; } = string.Empty;
    [JsonPropertyName("route_duration_minutes")] public int RouteDurationMinutes { get; set; }
    [JsonPropertyName("deployed_at")] public DateTimeOffset DeployedAt { get; set; }
    // Set on Estimate & queue rows: deployed_at is back-computed from the
    // collection, so the server keeps an existing row's stored dispatch time
    // (real dispatch push from another computer, or a manual entry) over it.
    [JsonPropertyName("deployed_at_estimated")] public bool DeployedAtEstimated { get; set; }
    [JsonPropertyName("collected_at")] public DateTimeOffset? CollectedAt { get; set; }
    [JsonPropertyName("ceruleum_tanks")] public int CeruleumTanks { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("loot")] public List<PushLoot> Loot { get; set; } = new();
}

public sealed class PushLoot
{
    [JsonPropertyName("ffxiv_item_id")] public uint? FfxivItemId { get; set; }
    [JsonPropertyName("item_name")] public string? ItemName { get; set; }
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("hq")] public bool Hq { get; set; }
}

internal sealed class PushRequest
{
    [JsonPropertyName("deployments")] public List<PushRow> Deployments { get; set; } = new();
}

public sealed class PushResponse
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("created")] public int Created { get; set; }
    [JsonPropertyName("completed")] public int Completed { get; set; }
    [JsonPropertyName("enriched")] public int Enriched { get; set; }
    [JsonPropertyName("duplicates")] public int Duplicates { get; set; }
    [JsonPropertyName("errors")] public int Errors { get; set; }
    [JsonPropertyName("results")] public List<PushRowResult> Results { get; set; } = new();
}

public sealed class PushRowResult
{
    [JsonPropertyName("external_voyage_id")] public string ExternalVoyageId { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("deployment_id")] public int? DeploymentId { get; set; }
    [JsonPropertyName("unmatched_items")] public List<string> UnmatchedItems { get; set; } = new();
    [JsonPropertyName("message")] public string? Message { get; set; }
}

// Response of GET /api/plugin/v1/stats — the in-game overlay aggregate.
// Completed voyages only; every gil value is computed server-side. All rates
// use the planned route duration.
public sealed class StatsResponse
{
    [JsonPropertyName("generated_at")] public DateTimeOffset GeneratedAt { get; set; }
    [JsonPropertyName("scope")] public StatsScope Scope { get; set; } = new();
    [JsonPropertyName("totals")] public StatsTotals Totals { get; set; } = new();
    [JsonPropertyName("per_submarine")] public List<StatsSubmarine> PerSubmarine { get; set; } = new();
    [JsonPropertyName("per_route")] public List<StatsRoute> PerRoute { get; set; } = new();
}

public sealed class StatsScope
{
    // Echoes the request; null = the key-holder's own default scope.
    [JsonPropertyName("free_company_id")] public int? FreeCompanyId { get; set; }
}

public sealed class StatsTotals
{
    [JsonPropertyName("voyages")] public long Voyages { get; set; }
    [JsonPropertyName("gil")] public long Gil { get; set; }
    [JsonPropertyName("voyages_7d")] public long Voyages7d { get; set; }
    [JsonPropertyName("gil_7d")] public long Gil7d { get; set; }
}

public sealed class StatsSubmarine
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("voyages")] public long Voyages { get; set; }
    [JsonPropertyName("total_gil")] public long TotalGil { get; set; }
    [JsonPropertyName("gil_per_route_hour")] public double GilPerRouteHour { get; set; }
}

public sealed class StatsRoute
{
    [JsonPropertyName("route")] public string Route { get; set; } = string.Empty;
    [JsonPropertyName("voyages")] public long Voyages { get; set; }
    [JsonPropertyName("avg_gil")] public double AvgGil { get; set; }
    [JsonPropertyName("gil_per_route_hour")] public double GilPerRouteHour { get; set; }
}

// Body of POST /api/plugin/v1/gamedata — the three submarine sheets,
// dumped once per game version and read in English whatever the client
// language. The field set is everything SubmarineTracker's formulas read; the
// server's route planner ports those formulas, so a field dropped here breaks
// it silently. Integer widths are the sheets' own.
public sealed class GameDataUpload
{
    [JsonPropertyName("game_version")] public string GameVersion { get; set; } = string.Empty;
    [JsonPropertyName("exploration")] public List<GameDataSector> Exploration { get; set; } = new();
    [JsonPropertyName("parts")] public List<GameDataPart> Parts { get; set; } = new();
    [JsonPropertyName("ranks")] public List<GameDataRank> Ranks { get; set; } = new();
}

public sealed class GameDataSector
{
    [JsonPropertyName("sector_id")] public ushort SectorId { get; set; }
    [JsonPropertyName("map_id")] public ushort MapId { get; set; }
    [JsonPropertyName("map_name")] public string MapName { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    // Route letter relative to the map's start (as in pushed route strings);
    // null on the map's starting point, which has no letter.
    [JsonPropertyName("letter")] public string? Letter { get; set; }
    [JsonPropertyName("starting_point")] public bool StartingPoint { get; set; }
    [JsonPropertyName("rank_required")] public byte RankRequired { get; set; }
    [JsonPropertyName("exp_reward")] public uint ExpReward { get; set; }
    [JsonPropertyName("x")] public short X { get; set; }
    [JsonPropertyName("y")] public short Y { get; set; }
    [JsonPropertyName("z")] public short Z { get; set; }
    [JsonPropertyName("survey_distance")] public byte SurveyDistance { get; set; }
    [JsonPropertyName("survey_duration_min")] public ushort SurveyDurationMin { get; set; }
    [JsonPropertyName("ceruleum_tank_req")] public byte CeruleumTankReq { get; set; }
}

public sealed class GameDataPart
{
    [JsonPropertyName("part_id")] public ushort PartId { get; set; }
    // The part's item name; null for a part id the exporter has no item for.
    [JsonPropertyName("name")] public string? Name { get; set; }
    // Raw sheet values. Slot: 0 hull, 1 stern, 2 bow, 3 bridge.
    [JsonPropertyName("slot")] public byte Slot { get; set; }
    [JsonPropertyName("class")] public ushort Class { get; set; }
    [JsonPropertyName("rank")] public byte Rank { get; set; }
    [JsonPropertyName("components")] public byte Components { get; set; }
    [JsonPropertyName("repair_materials")] public byte RepairMaterials { get; set; }
    [JsonPropertyName("surveillance")] public short Surveillance { get; set; }
    [JsonPropertyName("retrieval")] public short Retrieval { get; set; }
    [JsonPropertyName("speed")] public short Speed { get; set; }
    [JsonPropertyName("range")] public short Range { get; set; }
    [JsonPropertyName("favor")] public short Favor { get; set; }
}

public sealed class GameDataRank
{
    [JsonPropertyName("rank")] public ushort Rank { get; set; }
    [JsonPropertyName("exp_to_next")] public uint ExpToNext { get; set; }
    [JsonPropertyName("capacity")] public ushort Capacity { get; set; }
    [JsonPropertyName("surveillance_bonus")] public byte SurveillanceBonus { get; set; }
    [JsonPropertyName("retrieval_bonus")] public byte RetrievalBonus { get; set; }
    [JsonPropertyName("speed_bonus")] public byte SpeedBonus { get; set; }
    [JsonPropertyName("range_bonus")] public byte RangeBonus { get; set; }
    [JsonPropertyName("favor_bonus")] public byte FavorBonus { get; set; }
}

// 200 body: "created" (a new version, now current) or "unchanged" (already
// stored with the same numbers). 403/409 bodies carry error + message instead.
public sealed class GameDataUploadResponse
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("game_version")] public string? GameVersion { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}
