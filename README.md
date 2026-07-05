# Bunny Sub Sync

Dalamud plugin that pushes your FFXIV submarine voyages to
[Bunny Sub Tracker](https://subs.bnuuy.gg) automatically — dispatches show up
as "at sea", collections land with full loot, and manual entries you made on
the website get completed in place.

## Install

1. Dalamud Settings (`/xlsettings`) → **Experimental** → *Custom Plugin
   Repositories* → add:
   `https://raw.githubusercontent.com/Juusohel/BunnySubSync/main/repo.json`
2. `/xlplugins` → search **Bunny Sub Sync** → install.

## Pairing (one time)

1. Log into [subs.bnuuy.gg](https://subs.bnuuy.gg) → **Profile** → *In-game
   plugin* → copy your plugin token.
2. In game: `/bunnysync` → **Status** tab → paste the token → *Test
   connection*. You should see "Linked as <you>".
3. Visit your workshop once so the plugin sees your submarines, then open the
   **Mapping** tab → *Refresh from server* → pick your platform FC, tick
   **Enabled**, and check each sub linked to its website counterpart
   (same-named subs link automatically; the *copy name* button helps you
   create missing ones on the website).

That's it. Voyages push automatically from then on — the **Log** tab shows
what went out. Tokens can be revoked/regenerated on the website at any time.

## Backfill (optional)

If you've been running **SubmarineTracker**, the **Backfill** tab can export
its voyage history as a CSV for the website's Import dialog — map your FC
first, scan, export, then upload on the website with the "this is a backfill"
option ticked. Dispatch times in the export are estimates (the history only
stores returns).

## Notes

- The plugin token is stored in plain text in the plugin config (standard for
  Dalamud plugins); it only grants access to your own submarine data and is
  revocable from the website.
- After major game patches the plugin may need a rebuild against updated
  Dalamud/ClientStructs — if voyages stop being captured after a patch, check
  for a plugin update first.

## Building from source

Requires a machine with XIVLauncher/Dalamud installed (or `DALAMUD_HOME`
pointing at the dev libs):

```
dotnet build
```

Dev-load the output directory via Dalamud Settings → Experimental → Dev
Plugin Locations. The server side lives in the
[bunny_sub_tracker](https://github.com/Juusohel/bunny-sub-tracker) repo.

## Releasing

Tag `v*` and push — CI builds Release and attaches `latest.zip` to a GitHub
Release. Then bump `AssemblyVersion` in `BunnySubSync.csproj`'s `<Version>`
**and** in `repo.json` on `main`; Dalamud offers users the update from the
raw `repo.json` URL.

MIT licensed. Game-interop techniques adapted from
[SubmarineTracker](https://github.com/Infiziert90/SubmarineTracker) (MIT).
