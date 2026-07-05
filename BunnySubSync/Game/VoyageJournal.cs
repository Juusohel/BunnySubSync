using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BunnySubSync.Api;

namespace BunnySubSync.Game;

public enum OutboxState
{
    /// <summary>Dispatch observed; waiting for the voyage result.</summary>
    PendingCollection,

    /// <summary>Voyage assembled into a PushRow; ready for the G3 outbox worker.</summary>
    Queued,

    /// <summary>Pushed to the server (G3).</summary>
    Sent,

    /// <summary>Needs attention — see FailureMessage (e.g. "missed dispatch").</summary>
    Failed,
}

/// <summary>
/// One voyage, from observed dispatch to pushed result. Serialized to
/// voyages.json — keep changes backward-compatible (unknown fields are
/// ignored on load, missing ones get defaults).
/// </summary>
public sealed class JournalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ulong FcId { get; set; }
    public uint SubRegisterTime { get; set; }
    public string SubName { get; set; } = string.Empty;
    public DateTime DispatchedUtc { get; set; }
    public uint ScheduledReturnUnix { get; set; }
    public uint[] Sectors { get; set; } = [];
    public OutboxState State { get; set; }
    public string? FailureMessage { get; set; }
    public PushRow? BuiltRow { get; set; }
    public DateTime? CollectedUtc { get; set; }

    /// <summary>Loot captured for a journal-missed voyage, kept so the explicit
    /// "Estimate &amp; queue" action can still build the full row later.</summary>
    public List<LootLine>? PendingLoot { get; set; }

    public bool Simulated { get; set; }
}

/// <summary>
/// Persisted voyage journal. Two invariants (plan §9 G2, "bugs waiting to
/// happen otherwise"):
/// 1. This is an append-style list of voyages, never a per-sub slot — a
///    collect-then-redispatch produces a second entry, it never overwrites
///    the first before the outbox drains it.
/// 2. The journal itself records only what it's told; the baseline-is-not-a-
///    dispatch rule lives in the producer (SubSnapshotService).
/// Thread-safe: the result hook fires on the network thread while ImGui reads
/// on the main thread.
/// </summary>
public sealed class VoyageJournal
{
    private const int MaxEntries = 300; // prune oldest Sent entries past this

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object sync = new();
    private readonly string path;
    private readonly List<JournalEntry> entries = [];

    public VoyageJournal(string configDirectory)
    {
        path = Path.Combine(configDirectory, "voyages.json");
        Load();
    }

    public JournalEntry AppendDispatch(SubDispatched ev)
    {
        var entry = new JournalEntry
        {
            FcId = ev.FcId,
            SubRegisterTime = ev.SubRegisterTime,
            SubName = ev.SubName,
            DispatchedUtc = ev.DispatchedUtc,
            ScheduledReturnUnix = ev.ScheduledReturnUnix,
            Sectors = ev.Sectors,
            State = OutboxState.PendingCollection,
            Simulated = ev.Simulated,
        };

        lock (sync)
        {
            entries.Add(entry);
            SaveLocked();
        }

        return entry;
    }

    public JournalEntry AppendMissedDispatch(VoyageCompleted ev, uint lastKnownReturnUnix, string subName)
    {
        var entry = new JournalEntry
        {
            FcId = ev.FcId,
            SubRegisterTime = ev.SubRegisterTime,
            SubName = subName,
            ScheduledReturnUnix = lastKnownReturnUnix,
            Sectors = ev.Sectors,
            State = OutboxState.Failed,
            FailureMessage = "missed dispatch — push manually",
            CollectedUtc = ev.CollectedUtc,
            PendingLoot = ev.Loot,
            Simulated = ev.Simulated,
        };

        lock (sync)
        {
            entries.Add(entry);
            SaveLocked();
        }

        return entry;
    }

    /// <summary>Newest entry still waiting for this sub's voyage result, if any.</summary>
    public JournalEntry? FindPendingCollection(ulong fcId, uint subRegisterTime)
    {
        lock (sync)
        {
            return entries.LastOrDefault(e =>
                e.State == OutboxState.PendingCollection
                && e.FcId == fcId
                && e.SubRegisterTime == subRegisterTime);
        }
    }

    /// <summary>Mutate an entry under the journal lock, then persist.</summary>
    public void Update(JournalEntry entry, Action<JournalEntry> mutate)
    {
        lock (sync)
        {
            mutate(entry);
            SaveLocked();
        }
    }

    /// <summary>Snapshot for UI display, newest first.</summary>
    public List<JournalEntry> SnapshotNewestFirst()
    {
        lock (sync)
        {
            return entries.AsEnumerable().Reverse().ToList();
        }
    }

    public int CountInState(OutboxState state)
    {
        lock (sync)
        {
            return entries.Count(e => e.State == state);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path))
                return;

            var loaded = JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(path), JsonOptions);
            if (loaded != null)
                entries.AddRange(loaded);

            Plugin.Log.Information($"Voyage journal loaded: {entries.Count} entries");
        }
        catch (Exception ex)
        {
            // A corrupt journal must not brick the plugin: keep the bad file
            // aside for inspection and start fresh.
            Plugin.Log.Error(ex, "Voyage journal is unreadable — backing it up and starting fresh");
            try
            {
                File.Move(path, path + ".corrupt", overwrite: true);
            }
            catch (Exception moveEx)
            {
                Plugin.Log.Error(moveEx, "Could not back up the corrupt journal");
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            if (entries.Count > MaxEntries)
            {
                var excess = entries.Count - MaxEntries;
                // Only prune entries that need no further action.
                entries.RemoveAll(e => e.State == OutboxState.Sent && excess-- > 0);
            }

            // Atomic-ish: never leave a half-written journal behind.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to save the voyage journal");
        }
    }
}
