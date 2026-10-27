using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BunnySubSync.Api;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace BunnySubSync.Game;

/// <summary>
/// Sends the submarine sheets to the platform once per game version: on
/// plugin load when this version hasn't been sent yet, or on demand from the
/// Status tab. Can also save the same payload to a file.
///
/// Never blocks or touches voyage sync: it runs on the thread pool, failures
/// only log and retry on the next load, and a rejected token here does not
/// pause the outbox. The server accepts uploads from admin accounts only; any
/// other account gets a 403, which counts as done for that version so it
/// isn't retried.
/// </summary>
public sealed class GameDataUploader
{
    private readonly Configuration configuration;
    private int running; // 0/1 via Interlocked

    public bool Busy => Volatile.Read(ref running) == 1;
    public string? LastMessage { get; private set; }
    public bool LastMessageIsError { get; private set; }

    public GameDataUploader(Configuration configuration)
    {
        this.configuration = configuration;
    }

    /// <summary>The client's game version (its ffxivgame.ver), e.g. 2026.10.06.0000.0000.</summary>
    public static string CurrentGameVersion() => Plugin.DataManager.GameData.Repositories["ffxiv"].Version;

    /// <summary>Reads the payload from the running client, always in English
    /// so names never depend on the client's language.</summary>
    public static GameDataUpload ReadFromGame() =>
        GameDataExport.Build(
            CurrentGameVersion(),
            Plugin.DataManager.GetExcelSheet<SubmarineExploration>(ClientLanguage.English),
            Plugin.DataManager.GetExcelSheet<SubmarinePart>(ClientLanguage.English),
            Plugin.DataManager.GetExcelSheet<SubmarineRank>(ClientLanguage.English),
            Plugin.DataManager.GetExcelSheet<Item>(ClientLanguage.English));

    /// <summary>Plugin load: upload if this game version hasn't been sent.</summary>
    public void UploadIfNewVersion()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configuration.ApiToken)
                || configuration.LastGameDataVersion == CurrentGameVersion())
                return;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Game data: couldn't read the game version");
            return;
        }

        Start(manual: false);
    }

    /// <summary>Status-tab "Re-upload game data".</summary>
    public void UploadNow() => Start(manual: true);

    /// <summary>Status-tab "Save game data to file…": the exact upload payload.</summary>
    public void SaveToFile(string path)
    {
        try
        {
            var upload = ReadFromGame();
            File.WriteAllText(path, GameDataExport.ToFileJson(upload));
            Report($"Saved game data {upload.GameVersion} to {path}.", isError: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Game data: save to file failed");
            Report($"Save failed: {ex.Message}", isError: true);
        }
    }

    private void Start(bool manual)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await UploadAsync(manual).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Game data upload failed; retrying on next load");
                Report($"Upload failed: {ex.Message}", isError: true);
            }
            finally
            {
                Volatile.Write(ref running, 0);
            }
        });
    }

    private async Task UploadAsync(bool manual)
    {
        if (string.IsNullOrWhiteSpace(configuration.ApiToken))
        {
            Report("Pair a plugin token first (above).", isError: true);
            return;
        }

        var upload = ReadFromGame();
        var version = upload.GameVersion;

        GameDataUploadResult result;
        try
        {
            using var client = new ApiClient(configuration.ServerUrl, configuration.ApiToken);
            result = await client.UploadGameDataAsync(upload).ConfigureAwait(false);
        }
        catch (UnauthorizedException)
        {
            // Voyage pushes surface and pause on this themselves; here it's
            // just a failed attempt.
            Report("The server rejected the plugin token — re-pair above.", isError: true);
            return;
        }

        switch (result.StatusCode)
        {
            // Only our own answer counts: a 200 without it (an older server
            // whose SPA fallback answers unknown routes, a proxy page) must not
            // mark the version sent, or it would never be retried.
            case HttpStatusCode.OK when result.Body?.Status is "created" or "unchanged":
                MarkSent(version);
                Plugin.Log.Information($"Game data {version}: {result.Body?.Status}");
                Report(result.Body?.Status == "created"
                           ? $"Uploaded game data {version}."
                           : $"Game data {version} is already up to date on the server.",
                       isError: false);
                break;
            case HttpStatusCode.Forbidden:
                // Only admin accounts may upload — nothing to do for this
                // account, so don't ask again until the next game version.
                MarkSent(version);
                Plugin.Log.Debug($"Game data {version}: not accepted from this account");
                if (manual)
                    Report("This account doesn't upload game data (admin accounts only) — nothing to do.", isError: false);
                break;
            case HttpStatusCode.Conflict:
                // The same version is stored with different numbers: this
                // build's export differs from the one that uploaded first.
                Plugin.Log.Error($"Game data {version}: server holds different numbers for this version — {result.Body?.Message}");
                Report($"Server already has different numbers for {version}; nothing changed. Report this.", isError: true);
                break;
            default:
                Plugin.Log.Warning($"Game data {version}: server answered {(int)result.StatusCode}; retrying on next load");
                Report($"Upload failed (HTTP {(int)result.StatusCode}).", isError: true);
                break;
        }
    }

    private void MarkSent(string version)
    {
        configuration.LastGameDataVersion = version;
        configuration.Save();
    }

    private void Report(string message, bool isError)
    {
        LastMessage = message;
        LastMessageIsError = isError;
    }
}
