using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BunnySubSync.Api;
using Dalamud.Plugin.Services;

namespace BunnySubSync.Game;

/// <summary>
/// Drains the journal to the server (plan §9 G3). Below the seam: consumes
/// journal entries + config mapping only. Cadence: every 60s and immediately
/// on new work; transport failure → exponential backoff (1, 2, 4 … cap 30
/// min); 401 → pause until the token changes. Idempotency (D5) makes every
/// retry safe.
///
/// Mapping gating per §3.4.4, adapted to the journal model: voyages whose
/// mapping is *missing* stay Queued with a visible "waiting" reason (mapping
/// usually arrives after the first voyages); voyages whose FC/sub the user
/// explicitly *disabled* go to Skipped quietly.
/// </summary>
public sealed class Outbox : IDisposable
{
    private const int MaxBatch = 50; // server-side MAX_BATCH_SIZE
    private const int NormalCadenceSeconds = 60;
    private const int BackoffCapMinutes = 30;

    private readonly VoyageEvents events;
    private readonly VoyageJournal journal;
    private readonly Configuration configuration;
    private readonly ConcurrentQueue<string> pendingChat = new();

    private int draining; // 0/1 via Interlocked
    private DateTime nextAttemptUtc = DateTime.MinValue;
    private int consecutiveFailures;

    public bool Paused { get; private set; }
    public string? PausedReason { get; private set; }
    public DateTime? LastDrainUtc { get; private set; }
    public string? LastDrainSummary { get; private set; }

    public Outbox(VoyageEvents events, VoyageJournal journal, Configuration configuration)
    {
        this.events = events;
        this.journal = journal;
        this.configuration = configuration;

        // The assembler is subscribed before us (Plugin ctor order), so by the
        // time these fire the journal already has the new entry; and even if
        // ordering ever changed, Kick only moves a timestamp — the drain
        // happens on a later framework tick regardless.
        events.Dispatched += OnNewWork;
        events.Completed += OnNewWork;
        Plugin.Framework.Update += OnTick;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnTick;
        events.Dispatched -= OnNewWork;
        events.Completed -= OnNewWork;
    }

    private void OnNewWork(SubDispatched _) => Kick();
    private void OnNewWork(VoyageCompleted _) => Kick();

    /// <summary>Ask for a drain soon. Does not bypass failure backoff — new
    /// work must not hammer a server that's already down.</summary>
    public void Kick()
    {
        if (consecutiveFailures == 0)
            nextAttemptUtc = DateTime.MinValue;
    }

    /// <summary>Status-tab path: the user changed the token — unpause and retry.</summary>
    public void OnTokenChanged()
    {
        Paused = false;
        PausedReason = null;
        consecutiveFailures = 0;
        nextAttemptUtc = DateTime.MinValue;
    }

    /// <summary>Log-tab Retry on a Failed/Skipped entry: re-queue and drain.</summary>
    public void Retry(JournalEntry entry)
    {
        journal.Update(entry, e =>
        {
            if (e.BuiltRow != null)
            {
                e.State = OutboxState.Queued;
                e.FailureMessage = null;
                e.WaitReason = null;
            }
        });
        consecutiveFailures = 0;
        nextAttemptUtc = DateTime.MinValue;
    }

    private void OnTick(IFramework framework)
    {
        // Chat must go out on the framework thread; drains run on the pool.
        while (pendingChat.TryDequeue(out var msg))
            Plugin.ChatGui.Print(msg);

        if (Paused || DateTime.UtcNow < nextAttemptUtc)
            return;
        if (Interlocked.CompareExchange(ref draining, 1, 0) != 0)
            return;

        nextAttemptUtc = DateTime.UtcNow.AddSeconds(NormalCadenceSeconds);
        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        try
        {
            var work = CollectWork();
            if (work.Count == 0)
                return;

            List<PushRow> rows = [.. work.Select(w => w.Row)];
            PushResponse response;
            try
            {
                using var client = new ApiClient(configuration.ServerUrl, configuration.ApiToken);
                response = await client.PushAsync(rows).ConfigureAwait(false);
            }
            catch (UnauthorizedException)
            {
                Paused = true;
                PausedReason = "token rejected — re-pair from the Status tab";
                pendingChat.Enqueue("[BunnySubSync] Server rejected the plugin token — pushes paused. Re-pair from the Status tab.");
                Plugin.Log.Warning("Outbox paused: 401 from server");
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var delayMinutes = Math.Min(1 << Math.Min(consecutiveFailures - 1, 10), BackoffCapMinutes);
                nextAttemptUtc = DateTime.UtcNow.AddMinutes(delayMinutes);
                Plugin.Log.Warning(ex, $"Outbox push failed (attempt {consecutiveFailures}) — retrying in {delayMinutes} min");
                return;
            }

            consecutiveFailures = 0;
            ApplyResults(work, response);
            LastDrainUtc = DateTime.UtcNow;
            LastDrainSummary =
                $"{response.Created} created, {response.Completed} completed, {response.Enriched} enriched, "
                + $"{response.Duplicates} duplicate, {response.Errors} error(s)";
            Plugin.Log.Information($"Outbox drained {rows.Count} row(s): {LastDrainSummary}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Unexpected outbox error");
        }
        finally
        {
            Interlocked.Exchange(ref draining, 0);
        }
    }

    private sealed record WorkItem(JournalEntry Entry, PushRow Row, bool IsDispatchRow);

    private List<WorkItem> CollectWork()
    {
        var work = new List<WorkItem>();

        foreach (var entry in journal.Snapshot())
        {
            if (work.Count >= MaxBatch)
                break;

            var isDispatchRow =
                entry.State == OutboxState.PendingCollection
                && entry.DispatchRow != null
                && !entry.DispatchPushed;
            var isCollectionRow = entry.State == OutboxState.Queued && entry.BuiltRow != null;
            if (!isDispatchRow && !isCollectionRow)
                continue;

            var row = isDispatchRow ? entry.DispatchRow! : entry.BuiltRow!;

            switch (Resolve(entry, out var submarineId, out var reason))
            {
                case Resolution.Ready:
                    journal.Update(entry, e => e.WaitReason = null);
                    row.SubmarineId = submarineId;
                    work.Add(new WorkItem(entry, row, isDispatchRow));
                    break;

                case Resolution.Waiting:
                    // Not mapped *yet* — mapping usually happens after the
                    // first voyages. Stays queued, visibly.
                    journal.Update(entry, e => e.WaitReason = reason);
                    break;

                case Resolution.Disabled:
                    // Explicitly disabled by the user — quiet per §3.4.4.
                    if (isCollectionRow)
                    {
                        journal.Update(entry, e =>
                        {
                            e.State = OutboxState.Skipped;
                            e.FailureMessage = reason;
                        });
                    }
                    break;
            }
        }

        return work;
    }

    private enum Resolution { Ready, Waiting, Disabled }

    private Resolution Resolve(JournalEntry entry, out int submarineId, out string? reason)
    {
        submarineId = 0;

        if (!configuration.FcMappings.TryGetValue(entry.FcId, out var mapping)
            || mapping.PlatformFcId == null)
        {
            reason = "waiting: FC not mapped (Mapping tab)";
            return Resolution.Waiting;
        }

        if (!mapping.Enabled)
        {
            reason = "FC disabled in mapping — not pushed";
            return Resolution.Disabled;
        }

        if (!mapping.Subs.TryGetValue(entry.SubRegisterTime, out var link)
            || link.PlatformSubmarineId == null)
        {
            reason = "waiting: submarine not linked (Mapping tab)";
            return Resolution.Waiting;
        }

        if (!link.Enabled)
        {
            reason = "submarine disabled in mapping — not pushed";
            return Resolution.Disabled;
        }

        submarineId = link.PlatformSubmarineId.Value;
        reason = null;
        return Resolution.Ready;
    }

    private void ApplyResults(List<WorkItem> work, PushResponse response)
    {
        var byId = work.ToDictionary(w => w.Row.ExternalVoyageId);

        foreach (var result in response.Results)
        {
            if (!byId.TryGetValue(result.ExternalVoyageId, out var item))
            {
                Plugin.Log.Warning($"Push result for unknown external id {result.ExternalVoyageId}");
                continue;
            }

            var (entry, isDispatchRow) = (item.Entry, item.IsDispatchRow);
            var isError = result.Status == "error";

            if (isError && result.Message is { } m && m.Contains("not found", StringComparison.OrdinalIgnoreCase))
                MarkLinkStale(entry);

            if (isDispatchRow)
            {
                journal.Update(entry, e =>
                {
                    // 'duplicate' = the server already has this voyage — as
                    // pushed as it needs to be.
                    e.DispatchPushed = !isError;
                    e.WaitReason = isError ? $"dispatch push error: {result.Message}" : null;
                });
                if (!isError)
                    Plugin.Log.Information($"Dispatch push OK for '{entry.SubName}' ({result.Status})");
                continue;
            }

            if (isError)
            {
                journal.Update(entry, e =>
                {
                    e.State = OutboxState.Failed;
                    e.FailureMessage = $"server error: {result.Message}";
                });
                pendingChat.Enqueue($"[BunnySubSync] Push failed for '{entry.SubName}': {result.Message}");
                continue;
            }

            journal.Update(entry, e =>
            {
                e.State = OutboxState.Sent;
                e.PushStatus = result.Status;
                e.FailureMessage = null;
                e.WaitReason = null;
                e.UnmatchedItems = result.UnmatchedItems.Count > 0 ? result.UnmatchedItems : null;
            });

            // Success chatter respects the toggle; warnings and failures
            // always print (they need action).
            if (configuration.ChatNotifications)
            {
                // 'enriched' is worth calling out — it means a manual web
                // entry was completed in place by this push.
                var suffix = result.Status == "enriched" ? " — enriched an existing manual entry" : "";
                pendingChat.Enqueue($"[BunnySubSync] Voyage pushed: '{entry.SubName}' ({result.Status}){suffix}");
            }
            if (entry.UnmatchedItems is { Count: > 0 } unmatched)
                pendingChat.Enqueue($"[BunnySubSync] ⚠ Unmatched loot (not counted by the site): {string.Join(", ", unmatched)}");
        }
    }

    private void MarkLinkStale(JournalEntry entry)
    {
        if (!configuration.FcMappings.TryGetValue(entry.FcId, out var mapping)
            || !mapping.Subs.TryGetValue(entry.SubRegisterTime, out var link))
            return;

        // §3.4 staleness recovery: unlink, surface on the Mapping tab —
        // nothing is retried blindly.
        link.PlatformSubmarineId = null;
        configuration.Save();
        Plugin.Log.Warning(
            $"Platform submarine for '{entry.SubName}' no longer exists — link cleared, re-link on the Mapping tab");
    }
}
