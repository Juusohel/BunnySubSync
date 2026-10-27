using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace BunnySubSync;

[Serializable]
public class SubLink
{
    public int? PlatformSubmarineId { get; set; }
    public string LastKnownName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    // The build last sent to the site, with the site sub it went to; any
    // other current build or link is sent again.
    public string? PushedBuild { get; set; }
}

[Serializable]
public class FcMapping
{
    public bool Enabled { get; set; }
    public int? PlatformFcId { get; set; }
    public Dictionary<uint, SubLink> Subs { get; set; } = new();

    // The sector unlocks last sent to the site, with the site FC they went to.
    public string? PushedUnlocks { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public string ServerUrl { get; set; } = "https://subs.bnuuy.gg";

    // Plugin configs are plaintext JSON on disk — standard for Dalamud; the
    // token only grants access to this user's sub data and is revocable from
    // the website's Profile page.
    public string ApiToken { get; set; } = string.Empty;

    public bool AutoPush { get; set; } = true;
    public bool ChatNotifications { get; set; } = true;

    // Push an incomplete row at dispatch time so the website shows the sub as
    // at-sea. The collection push later completes the same row.
    public bool PushOnDispatch { get; set; } = true;

    // Dev-only: shows the Simulator tab. Deliberately has no UI toggle — set
    // it by hand in the config JSON so end users never wander in.
    public bool DevMode { get; set; }

    // Key: in-game FC content id.
    public Dictionary<ulong, FcMapping> FcMappings { get; set; } = new();

    // The game version whose submarine sheets were last sent to the server
    // (or refused there — non-admin accounts don't upload). On load, a
    // different running version triggers one upload.
    public string? LastGameDataVersion { get; set; }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
