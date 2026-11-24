# Bunny Sub Sync

Dalamud plugin that pushes your FFXIV submarine voyages to
[Bunny Sub Tracker](https://subs.bnuuy.gg) automatically — dispatches show up
as "at sea", collections land with full loot, and manual entries you made on
the website get completed in place.

## Install

1. Dalamud Settings (`/xlsettings`) → **Experimental** → *Custom Plugin
   Repositories* → add this address and save:
   `https://raw.githubusercontent.com/Juusohel/BunnySubSync/main/repo.json`
2. `/xlplugins` → search **Bunny Sub Sync** → install.

## Getting started (website side, one time)

The plugin pushes voyages into your existing account — it can't create
submarines for you, so set the website up first:

1. Create an account at [subs.bnuuy.gg](https://subs.bnuuy.gg), **click the
   link in the verification email**, then log in. (No email? Check spam, or
   try to log in and press *Resend verification email*.)
2. **Create your submarines**: Fleet → *Add Submarine*, one per in-game sub,
   named **exactly as in game** — same-named subs pair automatically in the
   plugin's Mapping tab. With your first submarine the site also asks what
   your Free Company is called. (You can also do this during mapping: the
   *copy name* button gives you the exact in-game name to paste into the
   website.)
3. **Running subs in more than one FC?** After your first submarine, turn on
   Multi-FC Mode under Settings → Free Companies, add each other FC there,
   pick it in the Free Company selector, then add its submarines. With a
   single FC, skip this step.

Once paired (next section), voyages land under Voyages exactly like
manual entries — dispatches as "at sea", collections with loot and timings.

## Pairing (one time)

1. Log into [subs.bnuuy.gg](https://subs.bnuuy.gg) → **Settings →
   Integrations** → *In-game plugin* → *Reveal* (the site may ask for your
   password, or for a code if you've turned on two-factor sign-in), then
   *Copy* your plugin token. Changing or resetting your
   password on the website replaces this token and unpairs the plugin; pair
   again with the new one.
2. In game: `/bunnysync` → **Status** tab → paste the token → *Test
   connection*. You should see "Linked as <you>".
3. Visit your workshop once so the plugin sees your submarines, then open the
   **Mapping** tab → *Refresh from server* → pick your Free Company in the
   *Platform FC* dropdown, tick
   **Enabled**, and check each sub linked to its website counterpart
   (same-named subs link automatically; the *copy name* button helps you
   create missing ones on the website).

That's it. Voyages push automatically from then on — the **Log** tab shows
what went out. Tokens can be revoked/regenerated on the website at any time.
Changing or resetting your website password replaces the token as well, so
the plugin stops pushing until you paste the new one on the Status tab.

The full guide, with the Discord bot's setup, is at
[subs.bnuuy.gg/help](https://subs.bnuuy.gg/help).

## Shared Free Companies

If someone shares their FC with you on the website (Settings → Free Companies
→ that FC's Members card),
it appears in the **Mapping** tab under a **"Shared with me"** divider, labeled
with the owner's name — two identically-named FCs are otherwise
indistinguishable in the dropdown. Pick it as your Platform FC to contribute
your voyages to the shared record: pushes land in the owner's FC and dedupe
against other members' pushes automatically (voyages carry a game-derived id,
so the same voyage from two members lands once).

- **View-only** memberships are listed but greyed out as *view only* — you can
  see their numbers in the Stats tab but never push into them.
- **Different people (or PCs) dispatching and collecting works.** If someone
  else dispatched a voyage — or you dispatched it on another computer — the
  collecting plugin won't have seen the dispatch; it back-computes the launch
  time from the voyage's actual in-game schedule and pushes automatically,
  and the server matches it to the dispatcher's "at sea" entry and completes
  it in place with the dispatcher's original times kept. Voyages collected
  this way are tagged `[EST]` in their notes.
- **Voyages sent out while you were away from the workshop** are only seen
  when you come back, so the plugin doesn't use the moment it saw them as the
  launch time. It works the launch time out from the voyage's schedule
  instead, the same way, and tags the voyage `[EST]`.
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
its voyage history as a CSV for the website's importer (Analytics → Import) —
map your FC
first, scan, export, then upload on the website with the "this is a backfill"
option ticked. Dispatch times in the export are estimates (the history only
stores returns).

## If something isn't working

- **"Server rejected the plugin token — pushes paused"**: the token was
  regenerated on the website, or your website password was changed or reset
  (that replaces the token too). On Settings → Integrations press *Reveal*,
  then *Copy*, and paste it on the Status tab.
- **A submarine shows "— not linked —"**: the website has no submarine with
  exactly that name in the Free Company you picked. Use *copy name*, add it on
  the Fleet page, then *Refresh from server*.
- **Voyages stop after a game patch**: check `/xlplugins` for an update.

There is more, including the Discord bot, at
[subs.bnuuy.gg/help](https://subs.bnuuy.gg/help#troubleshooting). Still stuck?
Write to support@bnuuy.gg.

## Notes

- The plugin token is stored in plain text in the plugin config (standard for
  Dalamud plugins); it only grants access to your own submarine data and is
  revocable from the website. Changing or resetting your website password
  replaces it, which unpairs the plugin until you paste the new token.
- On the website, revealing or regenerating the plugin token (*Copy* needs it
  revealed first) asks for your password, or for a code from your
  authenticator app if you've turned on two-factor sign-in, and so does
  getting a link code for the Discord bot. Once you've confirmed, that browser
  tab doesn't ask again for up to 10 minutes.
- After major game patches the plugin may need a rebuild against updated
  Dalamud/ClientStructs — if voyages stop being captured after a patch, check
  for a plugin update first.
- Once per game version the plugin offers the site's route planner the game's
  submarine sheets (sectors, parts, ranks — static game data, nothing about
  you or your subs). The server only keeps uploads from admin accounts, so
  for everyone else this is a single refused request per game patch.
- For linked submarines the plugin also sends each sub's rank and parts
  (whenever they change, and with each voyage) and the sector ids each voyage
  visits; for linked FCs, which sectors the workshop has unlocked and
  explored. The site shows the build, keeps rank and build in step with the
  game, and uses the sectors for the route planner.
- With each collected voyage it also sends the result window sector by
  sector: what each sector returned, the EXP it gave, any sector it unlocked,
  and the game's surveillance, retrieval and favor outcome lines, plus the
  rest of each sector's result entry as the game reports it (such as the
  sector's rating). The loot totals are unchanged; the per-sector detail is
  kept for future loot statistics.
- The plugin never sends your character's name, your chat or anything else
  about your character. What the website keeps, and how to download or delete
  it, is on its privacy page: https://subs.bnuuy.gg/privacy

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

MIT licensed. Game-interop techniques and the submarine part → item table
adapted from [SubmarineTracker](https://github.com/Infiziert90/SubmarineTracker)
(MIT).
