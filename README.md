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

## Getting started (website side, one time)

The plugin pushes voyages into your existing account — it can't create
submarines for you, so set the website up first:

1. Create an account at [subs.bnuuy.gg](https://subs.bnuuy.gg) and log in.
2. **Create your submarines**: Submarines → *Add Submarine*, one per in-game
   sub, named **exactly as in game** — same-named subs pair automatically in
   the plugin's Mapping tab. (You can also do this during mapping: the *copy
   name* button gives you the exact in-game name to paste into the website.)
3. **Running subs in more than one FC?** Enable Multi-FC Mode (Profile →
   Multi-FC Mode), add each FC under Free Companies, and assign each
   submarine to its FC. With a single FC you can skip all of this — the
   plugin maps your subs to the account directly.

Once paired (next section), voyages land under Deployments exactly like
manual entries — dispatches as "at sea", collections with loot and timings.

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

## Shared Free Companies

If someone shares their FC with you on the website (Free Companies → members),
it appears in the **Mapping** tab under a **"Shared with me"** divider, labeled
with the owner's name — two identically-named FCs are otherwise
indistinguishable in the dropdown. Pick it as your Platform FC to contribute
your voyages to the shared record: pushes land in the owner's FC and dedupe
against other members' pushes automatically (voyages carry a game-derived id,
so the same voyage from two members lands once).

- **View-only** memberships are listed but greyed out as *view only* — you can
  see their numbers in the Stats tab but never push into them.
- **Already built your own FC?** If an in-game sub is already linked to a sub in
  one of *your own* FCs while a shared FC offers a same-named sub, the row shows
  a `(shared?)` hint. Switch the Platform FC dropdown to the shared FC to
  re-point the mapping; your own FC keeps its history (it just stops receiving
  new pushes). Nothing is moved automatically. To consolidate old history, the
  FC owner can import your CSV export on the website.

## Stats

The **Stats** tab shows your website aggregates in game — totals, per-submarine,
and per-route gil (completed voyages only, all computed server-side). Pick a
scope (your default data, or a specific FC, including shared ones) and **Load
stats**. It's read-only and never changes anything.

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
[bunny_sub_tracker](https://github.com/Juusohel/bunny_sub_tracker) repo (private).

## Releasing

Tag `v*` and push — CI builds Release and attaches `latest.zip` to a GitHub
Release. Then bump `AssemblyVersion` in `BunnySubSync.csproj`'s `<Version>`
**and** in `repo.json` on `main`; Dalamud offers users the update from the
raw `repo.json` URL.

MIT licensed. Game-interop techniques adapted from
[SubmarineTracker](https://github.com/Infiziert90/SubmarineTracker) (MIT).
