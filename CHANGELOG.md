## v0.4.4

- Co-op between a mod-manager install and a manual install desynced: players ended up on boss
  bodies and the game logged errors nonstop. RoR2BepInExPack, which every mod manager installs,
  changes how Risk of Rain 2 numbers its content, and the manual zip didn't have it. The manual zip
  now includes RoR2BepInExPack (by Risk of Thunder, MIT).
- The lobby now also checks that everyone's Risk of Rain 2 numbers its content the same way, and
  says so in chat if not. The log notes the numbering at the title screen.

## v0.4.3

- The download no longer includes the developer test tools: the automated playtest runner, the clip
  recorder used for the showcase reel (it ran a locally installed ffmpeg), the test admin panel and
  the F9/F10 debug keys. None of them could be turned on in normal play, and nothing changes in play.
- MW2 mode no longer keeps switching god mode off, so god mode from other mods works alongside it.

## v0.4.2

First public release, for Risk of Rain 2's October 8, 2026 update.

- The MW2 Soldier survivor: MW2's movement, viewmodel, weapons with attachments and camos, sounds,
  HUD, minimap and menus, read from your own MW2 install
- Create-a-Class, perks and Pro perks (as RoR2 items), deathstreaks, Last Stand, One Man Army
- All 15 killstreaks, earned from kill streaks or found as items in chests
- MW2's XP, rank, challenges and unlocks, saved across runs
- Choose Class (F4) follows MW2's rule: the class changes on the spot only right after you spawn;
  once you've fought, it comes with your next spawn
- Your own MW2 key binds are used; Tactical Insertion is on X; F6 gets you out of a stuck spot
- Co-op through RoR2 lobbies, with a version check on join
- Source and downloads on GitHub; the Thunderstore listing is in review
