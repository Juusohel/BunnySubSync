using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BunnySubSync.Api;

// DTOs mirror the wire contract in
// feature_plans/PLUGIN_INTEGRATION_IMPLEMENTATION_PLAN.md §3 exactly —
// field names/shapes are not ours to "improve".

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
}

public sealed class SyncSubmarine
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("free_company_id")] public int? FreeCompanyId { get; set; }
    [JsonPropertyName("rank")] public int? Rank { get; set; }
    [JsonPropertyName("hull_type")] public string? HullType { get; set; }
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
