## v0.4.5

- Getting stuck at random with nothing in the way (the main thing players reported): MW2's player
  box could end up less than a centimetre inside the ground. From there MW2 movement couldn't move it
  any way, and the automatic hop couldn't either; F6 was the only way out. The box is now checked
  after every movement step and moved back out, keeping your speed.
- If Risk of Rain 2 puts you somewhere MW2's box doesn't fit (it is bigger than the survivor), such
  as under a rock lip or against a boulder, the way out now looks in every direction up to 2.5 m
  instead of only straight up, and the survivor's body follows MW2's box out instead of pulling it
  back in.
- F6 still turns MW2 mode off and on if you do get stuck, and the spot is still saved to
  `mw2_stuck.log`.
- Kill popups read MW2's numbers again (+100, elites +200, bosses +1000). The XP you earn is still
  the reduced RoR2 rate.
- The minimap is solid: the level behind the HUD no longer shows through it.
- Copycat now works in Risk of Rain 2: if an elite killed you, you come back with that elite's power
  (Blazing, Glacial, Overloading...) until the stage ends. A normal monster leaves nothing to copy.
  Its Create-a-Class description says so.
- Tactical Insertion as your class's lethal now does something: a second plant each stage, on top of
  the one every class gets on X. Its Create-a-Class description says so.
- The first-person gun bobs as you walk, as in MW2 (less when aiming, none at full zoom on a scope).
- Effects around your character (Topaz Brooch's barrier bubble, slows, banners) no longer cover the
  first-person view, and in third person they're sized to the MW2 soldier.
- The Attack Helicopter, Pave Low and Harrier guns are heard firing. In MW2 their gunfire is a looping
  sound for each burst, which wasn't played; it is now, and from farther away.

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
