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
}

[Serializable]
public class FcMapping
{
    public bool Enabled { get; set; }
    public int? PlatformFcId { get; set; }
    public Dictionary<uint, SubLink> Subs { get; set; } = new();
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

    // D4 stage one: push an incomplete row at dispatch time so the website
    // shows the sub as at-sea. The collection push completes the same row.
    public bool PushOnDispatch { get; set; } = true;

    // Dev-only: shows the Simulator tab. Deliberately has no UI toggle — set
    // it by hand in the config JSON so end users never wander in (plan §9 G2).
    public bool DevMode { get; set; }

    // Key: in-game FC content id.
    public Dictionary<ulong, FcMapping> FcMappings { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
