BetterTurrets
=============

Quality-of-life and balance tweaks for deployable turrets in 7 Days To Die.

What it does
------------
- Shows a lightweight on-screen turret HUD with ammo counts; icons tint to your dye color and get themed nicknames (TMNT palette; undyed turrets get unique fallback names).
- Adds a hold-E reload radial option so you can refill deployed turrets without picking them up.
- Scales turret damage (and optionally penalizes player damage) at perkTurrets 5 based on how many owned turrets are deployed; values are configurable.
- Doubles (or customizes) turret magazines via config. Code-based multiplier is disabled by default; XML overrides are provided for junk turrets.
- Attempts to read dye color from cosmetic mods, other mod slots, metadata tints, and item class tints to keep names/colors stable.

Installation
------------
1) Copy the folder `Mod/BetterTurrets` into your game `Mods` directory (e.g. `D:/SteamLibrary/steamapps/common/7 Days To Die/Mods/BetterTurrets`).
2) Restart the game/server. Both server and clients should have the mod for consistent behavior.

Configuration
-------------
- Runtime tunables live in `Mod/BetterTurrets/config.xml`.
	- `magazineMultiplier`: Code-based magazine scaling. Set to `1.0` (default here) to disable code scaling when using XML overrides. Raise this only if you remove the XML overrides or want global scaling.
	- `baseTurretDamageMultiplier`: Flat multiplier to all turret-origin damage.
	- `perTurretDamageBonus`: Additional turret damage per deployed owned turret at perkTurrets level 5.

- XML ammo overrides live in `Mod/BetterTurrets/Config/items.xml`.
	- Junk turret: base magazine 62 → 124, tier add 6..30 (Q2..Q6) → 12..60.
	- Adjust these XPath `set` values if you want different magazine sizes; add more `set` entries for other turret items as needed.

Building (optional)
-------------------
- Requires .NET Framework 4.8 targeting; build with:
	- `dotnet build BetterTurrets.csproj -c Release`
- The built DLL outputs to `bin/Release/net48/BetterTurrets.dll`. Copy it to `Mod/BetterTurrets/BetterTurrets.dll` (overwriting) to deploy.

Notes
-----
- Black dye mapping: the mod prefers the dye item name (`dyeblack`) and a near-black value threshold to force the Shredder nickname.
- Memory safety: turret name/reload caches clear when turrets are removed from the world, avoiding growth on long-running servers.
