using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using HarmonyLib;
using UnityEngine;

// Entry point that wires up Harmony patches for this mod.
public class BetterTurretsMod : IModApi
{
	public void InitMod(Mod _)
	{
		BetterTurretsConfig.Load();
		var harmony = new Harmony("com.betterturrets.overlay");
		harmony.PatchAll();
	}
}

internal static class BetterTurretsConfig
{
	public static float MagazineMultiplier = 2f;
	public static float BaseTurretDamageMultiplier = 1f;
	public static float TurretDamagePerDeployed = 0.25f;

	public static void Load()
	{
		try
		{
			var dllPath = Assembly.GetExecutingAssembly().Location;
			var dir = Path.GetDirectoryName(dllPath) ?? string.Empty;
			var configPath = Path.Combine(dir, "config.xml");
			if (!File.Exists(configPath))
			{
				return;
			}

			var doc = XDocument.Load(configPath);
			var settings = doc.Root?.Element("btSettings");
			if (settings == null)
			{
				return;
			}

			TryReadFloat(settings, "magazineMultiplier", ref MagazineMultiplier);
			TryReadFloat(settings, "baseTurretDamageMultiplier", ref BaseTurretDamageMultiplier);
			TryReadFloat(settings, "perTurretDamageBonus", ref TurretDamagePerDeployed);
		}
		catch
		{
			// keep defaults on parse failures
		}
	}

	private static void TryReadFloat(XElement settings, string name, ref float target)
	{
		var el = settings.Element(name);
		if (el == null)
		{
			return;
		}

		var text = el.Value;
		if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0f)
		{
			target = parsed;
		}
	}
}

// Renders a lightweight HUD panel listing the player's active turrets and their ammo counts.
// We hook into EntityPlayerLocal.OnGUI to piggyback on existing UI lifecycle without touching XUi.
[HarmonyPatch(typeof(EntityPlayerLocal))]
[HarmonyPatch("OnGUI")]
internal static class TurretAmmoHudPatch
{
	// Reused list to avoid per-frame allocations.
	private static readonly List<Entity> ScratchEntities = new List<Entity>(16);

	// Simple 1x1 texture we tint via GUI.color for the icon placeholder.
	private static Texture2D _iconTex;

	private const float PanelWidth = 170f;
	private const float RowHeight = 34f;
	private const float BoundsSize = 200f; // Half-extent for turret search cube around player.

	private static void Postfix(EntityPlayerLocal __instance)
	{
		// Only show for the local, living player with an attached world.
		if (__instance == null || __instance.isEntityRemote || __instance.IsDead())
			return;

		var world = __instance.world;
		if (world == null)
			return;

		// Lazily create a white pixel icon.
		if (_iconTex == null)
		{
			_iconTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
			_iconTex.SetPixel(0, 0, Color.white);
			_iconTex.Apply();
		}

		// Gather nearby turrets that belong to this player.
		ScratchEntities.Clear();
		var bounds = new Bounds(__instance.position, new Vector3(BoundsSize, BoundsSize, BoundsSize));
		world.GetEntitiesInBounds(typeof(EntityTurret), bounds, ScratchEntities);

		// Filter and project into a small view model.
		var ownedTurrets = 0;
		foreach (var entity in ScratchEntities)
		{
			if (entity is not EntityTurret turret)
				continue;

			if (turret.IsDead() || turret.isEntityRemote)
				continue;

			if (turret.belongsPlayerId != __instance.entityId)
				continue;

			DrawRow(ownedTurrets, turret);
			ownedTurrets++;
		}
	}
	private static readonly FieldInfo HudOriginalItemField = AccessTools.Field(typeof(EntityTurret), "OriginalItemValue") ?? AccessTools.Field(typeof(EntityTurret), "originalItemValue");
	private static readonly FieldInfo HudLastOriginalItemField = AccessTools.Field(typeof(EntityTurret), "lastOriginalItemValue") ?? AccessTools.Field(typeof(EntityTurret), "m_lastOriginalItemValue");

	private static bool TryGetTurretDyeColor(EntityTurret turret, out Color color, out string dyeItemName)
	{
		color = Color.white;
		dyeItemName = null;
		if (turret == null || turret.IsDead())
		{
			return false;
		}

		if (!TryGetTurretItemValueForHud(turret, out var itemValue))
		{
			return false;
		}

		return BetterTurretsUtil.TryGetDyeColor(itemValue, out color, out dyeItemName);
	}

	private static bool TryGetTurretItemValueForHud(EntityTurret turret, out ItemValue itemValue)
	{
		itemValue = default;
		if (turret == null)
		{
			return false;
		}

		if (HudOriginalItemField != null)
		{
			var value = (ItemValue)HudOriginalItemField.GetValue(turret);
			if (BetterTurretsUtil.IsValidItemValue(value))
			{
				itemValue = value;
				return true;
			}
		}

		if (HudLastOriginalItemField != null)
		{
			var value = (ItemValue)HudLastOriginalItemField.GetValue(turret);
			if (BetterTurretsUtil.IsValidItemValue(value))
			{
				itemValue = value;
				return true;
			}
		}

		return false;
	}

	private static void DrawRow(int index, EntityTurret turret)
	{
		var x = Screen.width - PanelWidth - 12f;
		var y = Screen.height * 0.35f + index * RowHeight;

		var rowRect = new Rect(x, y, PanelWidth, RowHeight - 4f);
		GUI.color = new Color(0f, 0f, 0f, 0.35f);
		GUI.Box(rowRect, GUIContent.none);

		// Icon tinted by turret dye if available.
		var iconRect = new Rect(x + 6f, y + 6f, 22f, 22f);
		var hasDye = TryGetTurretDyeColor(turret, out var dyeColor, out var dyeItemName);
		GUI.color = hasDye ? dyeColor : Color.white;
		GUI.DrawTexture(iconRect, _iconTex);

		// Text
		var labelRect = new Rect(x + 34f, y + 6f, PanelWidth - 40f, 22f);
		var nickname = GetOrAssignTurretName(turret, hasDye ? dyeColor : (Color?)null, dyeItemName);
		var label = $"{nickname}: {turret.AmmoCount}";
		GUI.color = Color.white;
		GUI.Label(labelRect, label);
	}

	private static readonly Dictionary<int, string> TurretNames = new();
	private static readonly HashSet<string> UsedNames = new(StringComparer.OrdinalIgnoreCase);
	private static readonly System.Random NameRng = new System.Random();
	private static readonly (Color color, string name)[] ColorNames =
	{
		(new Color(0.85f, 0.10f, 0.10f), "Raphael"),      // red
		(new Color(0.10f, 0.40f, 0.90f), "Leonardo"),     // blue
		(new Color(0.60f, 0.20f, 0.80f), "Donatello"),    // purple
		(new Color(0.95f, 0.50f, 0.10f), "Michelangelo"), // orange
		(new Color(0.20f, 0.70f, 0.20f), "Mutagen"),      // green fallback
		(new Color(0.05f, 0.05f, 0.05f), "Shredder"),     // black
		(new Color(0.55f, 0.35f, 0.18f), "Splinter"),     // brown
		(new Color(0.95f, 0.85f, 0.15f), "April"),        // yellow
		(new Color(0.95f, 0.60f, 0.80f), "Pinky")         // pink
	};

	private static readonly string[] FallbackNames = new[]
	{
		// Cartoon Network (early 2000s)
		"Ben", "Gwen", "Kevin", "SamuraiJack", "Aku", "Numbuh1", "Numbuh2", "Numbuh3", "Numbuh4", "Numbuh5",
		"Bloo", "Mac", "Eduardo", "Wilt", "Coco", "Dexter", "DeeDee", "Blossom", "Bubbles", "Buttercup",
		// Nickelodeon (early/mid 2000s)
		"Aang", "Katara", "Sokka", "Toph", "Zuko", "Iroh", "Appa", "Momo",
		"Danny", "Sam", "Tucker", "Timmy", "Cosmo", "Wanda", "Jorgen",
		"Jimmy", "Sheen", "Carl", "Cindy", "Libby", "Goddard",
		"Zim", "GIR", "Dib", "Gaz",
		// One Piece (Straw Hats)
		"Luffy", "Zoro", "Nami", "Usopp", "Sanji", "Chopper", "Robin", "Franky", "Brook", "Jinbe",
		// Hazbin Hotel
		"Alastor"
	};

	private static readonly Dictionary<string, string> DyeNameMap = new(StringComparer.OrdinalIgnoreCase)
	{
		{"dyered", "Raphael"},
		{"dyeblue", "Leonardo"},
		{"dyepurple", "Donatello"},
		{"dyeorange", "Michelangelo"},
		{"dyegreen", "Mutagen"},
		{"dyeblack", "Shredder"},
		{"dyebrown", "Splinter"},
		{"dyeyellow", "April"},
		{"dyepink", "Pinky"}
	};

	private static string GetOrAssignTurretName(EntityTurret turret, Color? dyeColor, string dyeItemName)
	{
		if (TurretNames.TryGetValue(turret.entityId, out var existing))
		{
			return existing;
		}

		string chosen;
		var fromFallback = false;

		// If we have a dye, prefer explicit item-name mapping, then color match.
		if (dyeColor.HasValue && TryGetTurretNickname(dyeColor.Value, dyeItemName, out var nick))
		{
			chosen = nick;
		}
		else
		{
			chosen = DrawUniqueFallbackName();
			fromFallback = true;
		}

		TurretNames[turret.entityId] = chosen;
		if (fromFallback)
		{
			UsedNames.Add(chosen);
		}
		return chosen;
	}

	private static bool TryGetTurretNickname(Color dyeColor, string dyeItemName, out string nickname)
	{
		nickname = null;

		// First try explicit dye item name mapping for reliability.
		if (!string.IsNullOrEmpty(dyeItemName) && DyeNameMap.TryGetValue(dyeItemName, out var mapped))
		{
			nickname = mapped;
			return true;
		}
		Color.RGBToHSV(dyeColor, out var h, out var s, out var v);

		// Treat near-black as Shredder directly.
		if (v < 0.20f)
		{
			nickname = "Shredder";
			return true;
		}

		// Very low saturation means effectively uncolored; fall back to random pool.
		if (s < 0.08f)
		{
			return false;
		}

		var bestScore = float.MaxValue;
		string best = null;
		foreach (var (color, name) in ColorNames)
		{
			if (name == "Shredder")
			{
				continue; // handled by low-value check
			}

			Color.RGBToHSV(color, out var th, out var ts, out var tv);
			var hueDiff = Mathf.Min(Mathf.Abs(h - th), 1f - Mathf.Abs(h - th)); // circular hue distance
			var satDiff = Mathf.Abs(s - ts);
			var valDiff = Mathf.Abs(v - tv);
			var score = (hueDiff * 1.1f) + (satDiff * 0.15f) + (valDiff * 0.2f);
			if (score < bestScore)
			{
				bestScore = score;
				best = name;
			}
		}

		if (best != null && bestScore <= 0.22f)
		{
			nickname = best;
			return true;
		}

		nickname = null;
		return false;
	}

	internal static void ReleaseTurret(EntityTurret turret)
	{
		if (turret == null)
		{
			return;
		}

		if (TurretNames.TryGetValue(turret.entityId, out var name))
		{
			TurretNames.Remove(turret.entityId);
			UsedNames.Remove(name);
		}
	}

	private static string DrawUniqueFallbackName()
	{
		// Try unused fallback names first.
		var available = new List<string>();
		for (var i = 0; i < FallbackNames.Length; i++)
		{
			if (!UsedNames.Contains(FallbackNames[i]))
			{
				available.Add(FallbackNames[i]);
			}
		}

		if (available.Count > 0)
		{
			return available[NameRng.Next(available.Count)];
		}

		// If exhausted, generate a numbered variant.
		var idx = UsedNames.Count + 1;
		return $"Turbo-{idx}";
	}

	private static float ColorDistanceSquared(Color a, Color b)
	{
		var dr = a.r - b.r;
		var dg = a.g - b.g;
		var db = a.b - b.b;
		return dr * dr + dg * dg + db * db;
	}
}

// Scales turret and player damage at perkTurrets level 5 based on deployed owned turrets.
[HarmonyPatch(typeof(EntityAlive))]
internal static class TurretDamageScalingPatch
{
	private const string TurretPerkName = "perkTurrets";
	private const float PlayerDamagePenaltyPerDeployed = 0.25f; // -25% per turret

	[HarmonyPrefix]
	[HarmonyPatch("damageEntityLocal", typeof(DamageSource), typeof(int), typeof(bool), typeof(float))]
	private static void PrefixDamageEntityLocal(EntityAlive __instance, DamageSource _damageSource, ref int _strength)
	{
		ApplyScaledDamage(__instance, _damageSource, ref _strength);
	}

	[HarmonyPrefix]
	[HarmonyPatch("DamageEntity", typeof(DamageSource), typeof(int), typeof(bool), typeof(float))]
	private static void PrefixDamageEntity(EntityAlive __instance, DamageSource _damageSource, ref int _strength)
	{
		ApplyScaledDamage(__instance, _damageSource, ref _strength);
	}

	private static void ApplyScaledDamage(EntityAlive victim, DamageSource source, ref int strength)
	{
		if (strength <= 0 || victim == null || source == null)
		{
			return;
		}

		var world = victim.world;
		if (world == null)
		{
			return;
		}

		var attacker = world.GetEntity(source.getEntityId()) as EntityAlive;
		if (attacker == null)
		{
			return;
		}

		// Determine if this hit originated from a turret (entity or turret item) or the player directly.
		var isTurretEntity = attacker is EntityTurret turretEntity;
		var isTurretItem = false;
		try
		{
			var ic = source.AttackingItem.ItemClass;
			isTurretItem = ic != null && ic.Name != null && ic.Name.IndexOf("turret", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		catch
		{
			// best-effort; ignore lookup failures
		}

		// Determine the player whose perk governs scaling.
		EntityAlive perkOwner = attacker;
		if (isTurretEntity)
		{
			perkOwner = world.GetEntity(((EntityTurret)attacker).belongsPlayerId) as EntityAlive;
		}

		var perkLevel = perkOwner?.Progression?.GetProgressionValue(TurretPerkName)?.Level ?? 0;
		if (perkLevel < 5)
		{
			return;
		}

		var ownedTurretCount = CountOwnedActiveTurrets(perkOwner, world);
		if (ownedTurretCount <= 0)
		{
			return;
		}

		float scale;
		if (isTurretEntity || isTurretItem)
		{
			// Buff turret-origin damage; never penalize it.
			scale = BetterTurretsConfig.BaseTurretDamageMultiplier * (1f + (BetterTurretsConfig.TurretDamagePerDeployed * ownedTurretCount));
		}
		else if (attacker == perkOwner)
		{
			// Penalize the owning player's outgoing damage.
			scale = Math.Max(0f, 1f - (PlayerDamagePenaltyPerDeployed * ownedTurretCount));
		}
		else
		{
			return;
		}

		var scaled = (int)Math.Floor(strength * scale);
		strength = Math.Max(0, scaled);
	}

	private static int CountOwnedActiveTurrets(EntityAlive owner, World world)
	{
		if (owner == null || world == null)
		{
			return 0;
		}

		var count = 0;
		var entities = world.Entities?.list;
		if (entities == null)
		{
			return 0;
		}

		for (var i = 0; i < entities.Count; i++)
		{
			if (entities[i] is not EntityTurret turret)
			{
				continue;
			}

			if (turret.IsDead() || turret.isEntityRemote)
			{
				continue;
			}

			if (turret.belongsPlayerId != owner.entityId)
			{
				continue;
			}

			count++;
		}

		return count;
	}
}

// Doubles base ammo capacity for turret items.
[HarmonyPatch(typeof(ItemActionRanged))]
internal static class TurretAmmoCapacityPatch
{
	[HarmonyTargetMethods]
	private static IEnumerable<MethodBase> Targets()
	{
		var methods = AccessTools.GetDeclaredMethods(typeof(ItemActionRanged));
		var list = new List<MethodBase>();
		for (var i = 0; i < methods.Count; i++)
		{
			if (methods[i].Name == "GetMaxAmmoCount")
			{
				list.Add(methods[i]);
			}
		}
		return list;
	}

	private static void Postfix(object __instance, ref int __result, ItemActionData _actionData)
	{
		if (__result <= 0)
		{
			return;
		}

		if (BetterTurretsConfig.MagazineMultiplier <= 1.001f)
		{
			return; // multiplier disabled
		}

		var itemValue = TryExtractItemValue(_actionData);
		var name = itemValue?.ItemClass?.Name;

		var isTurret = false;
		if (!string.IsNullOrEmpty(name))
		{
			isTurret = name.IndexOf("turret", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		else
		{
			var actionName = __instance?.GetType().Name ?? string.Empty;
			isTurret = actionName.IndexOf("turret", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		if (!isTurret)
		{
			return;
		}

		__result = Math.Max(1, (int)Math.Ceiling(__result * BetterTurretsConfig.MagazineMultiplier));
	}

	private static ItemValue TryExtractItemValue(ItemActionData actionData)
	{
		try
		{
			if (actionData == null)
			{
				return null;
			}

			var invDataField = AccessTools.Field(actionData.GetType(), "invData");
			var invData = invDataField?.GetValue(actionData);
			if (invData == null)
			{
				return null;
			}

			var itemValueField = AccessTools.Field(invData.GetType(), "itemValue");
			if (itemValueField?.GetValue(invData) is ItemValue value && value.ItemClass != null)
			{
				return value;
			}
		}
		catch
		{
			return null;
		}

		return null;
	}
}

[HarmonyPatch]
internal static class TurretEntityAmmoCapacityPatch
{
	private static MethodBase _target;

	[HarmonyPrepare]
	private static bool Prepare()
	{
		_target = AccessTools.Method(typeof(EntityTurret), "GetMaxAmmoCount")
			?? AccessTools.PropertyGetter(typeof(EntityTurret), "MaxAmmoCount")
			?? AccessTools.Method(typeof(EntityTurret), "GetAmmoMax")
			?? AccessTools.Method(typeof(EntityTurret), "get_MaxAmmoCount");
		return _target != null;
	}

	private static MethodBase TargetMethod()
	{
		return _target;
	}

	[HarmonyPostfix]
	private static void Postfix(ref int __result)
	{
		if (__result <= 0)
		{
			return;
		}

		if (BetterTurretsConfig.MagazineMultiplier <= 1.001f)
		{
			return; // multiplier disabled
		}

		__result = Math.Max(1, (int)Math.Ceiling(__result * BetterTurretsConfig.MagazineMultiplier));
	}
}

internal static class BetterTurretsUtil
{
	public static bool TryGetDyeColor(ItemValue itemValue, out Color color, out string dyeItemName)
	{
		color = Color.white;
		dyeItemName = null;
		string firstModName = null;
		if (itemValue == null)
		{
			return false;
		}

		try
		{
			// Prefer cosmetic mods (dyes) attached to this item.
			if (itemValue.CosmeticMods != null)
			{
				foreach (var mod in itemValue.CosmeticMods)
				{
					if (!IsValidItemValue(mod))
					{
						continue;
					}

					if (firstModName == null && mod?.ItemClass != null)
					{
						firstModName = mod.ItemClass.Name;
					}

					if (TryGetItemClassTint(mod.ItemClass, out color))
					{
						dyeItemName = mod.ItemClass?.Name;
						return true;
					}
				}
			}

			// Mods can also carry dyes (non-cosmetic slots in some cases).
			if (itemValue.Modifications != null)
			{
				foreach (var mod in itemValue.Modifications)
				{
					if (!IsValidItemValue(mod))
					{
						continue;
					}

					if (firstModName == null && mod?.ItemClass != null)
					{
						firstModName = mod.ItemClass.Name;
					}

					if (TryGetItemClassTint(mod.ItemClass, out color))
					{
						dyeItemName = mod.ItemClass?.Name;
						return true;
					}
				}
			}

			// Metadata key sometimes holds tint.
			if (itemValue.Metadata != null && itemValue.Metadata.TryGetValue("colorTint", out var meta))
			{
				const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
				var valField = meta.GetType().GetField("Value", flags);
				if (valField != null)
				{
					var val = valField.GetValue(meta);
					if (val is Color mc)
					{
						color = mc;
						dyeItemName = dyeItemName ?? firstModName;
						return true;
					}
					if (val is Color32 m32)
					{
						color = (Color)m32;
						dyeItemName = dyeItemName ?? firstModName;
						return true;
					}
					if (val is string ms && TryParseHtmlColor(ms, out var parsed))
					{
						color = parsed;
						dyeItemName = dyeItemName ?? firstModName;
						return true;
					}
					if (val is int mi)
					{
						// Assume ARGB32
						var a = ((mi >> 24) & 0xFF) / 255f;
						var r = ((mi >> 16) & 0xFF) / 255f;
						var g = ((mi >> 8) & 0xFF) / 255f;
						var b = (mi & 0xFF) / 255f;
						color = new Color(r, g, b, a);
						dyeItemName = dyeItemName ?? firstModName;
						return true;
					}
				}
			}

			// Fallback to the base item class tint if present.
			if (TryGetItemClassTint(itemValue.ItemClass, out color))
			{
				dyeItemName = itemValue.ItemClass?.Name;
				return true;
			}
		}
		catch
		{
			// ignore and fall through
		}

		return false;
	}

	private static bool TryParseHtmlColor(string s, out Color c)
	{
		c = Color.white;
		if (string.IsNullOrEmpty(s)) return false;
		// Unity's ColorUtility supports #RRGGBB and #RRGGBBAA
		if (ColorUtility.TryParseHtmlString(s, out var parsed))
		{
			c = parsed;
			return true;
		}
		return false;
	}

	private static bool TryGetItemClassTint(ItemClass itemClass, out Color color)
	{
		color = Color.white;
		if (itemClass == null)
		{
			return false;
		}

		if (ItemClassCustomIconTintField != null && ItemClassCustomIconTintField.GetValue(itemClass) is Color c1)
		{
			color = c1;
			return true;
		}

		if (ItemClassAltIconTintField != null && ItemClassAltIconTintField.GetValue(itemClass) is Color c2)
		{
			color = c2;
			return true;
		}

		return false;
	}

	private static readonly FieldInfo ItemClassCustomIconTintField = typeof(ItemClass).GetField("CustomIconTint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
	private static readonly FieldInfo ItemClassAltIconTintField = typeof(ItemClass).GetField("AltItemTypeIconColor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	public static bool IsValidItemValue(in ItemValue value)
	{
		return value.ItemClass != null;
	}
}

// Adds a hold-E radial option to reload a deployed turret without picking it up.
[HarmonyPatch(typeof(Entity))]
internal static class TurretReloadActivationPatch
{
	private static readonly Dictionary<int, int> ReloadIndexByEntity = new();
	private static readonly FieldInfo OriginalItemField = AccessTools.Field(typeof(EntityTurret), "OriginalItemValue") ?? AccessTools.Field(typeof(EntityTurret), "originalItemValue");
	private static readonly FieldInfo LastOriginalItemField = AccessTools.Field(typeof(EntityTurret), "lastOriginalItemValue") ?? AccessTools.Field(typeof(EntityTurret), "m_lastOriginalItemValue");

	[HarmonyPostfix]
	[HarmonyPatch("GetActivationCommands", typeof(Vector3i), typeof(EntityAlive))]
	private static void AddReloadCommand(Entity __instance, ref EntityActivationCommand[] __result, Vector3i _tePos, EntityAlive _entityFocusing)
	{
		if (__instance is not EntityTurret turret || _entityFocusing == null)
		{
			return;
		}

		// Only owner can reload.
		if (turret.belongsPlayerId != _entityFocusing.entityId)
		{
			return;
		}

		var list = __result != null ? new List<EntityActivationCommand>(__result) : new List<EntityActivationCommand>();
		var reloadCmd = new EntityActivationCommand
		{
			text = "Reload",
			icon = "ui_game_symbol_ammo",
			iconColor = Color.white,
			enabled = true,
			eventName = "reloadTurret",
			activateTime = 1.0f // require hold
		};
		list.Add(reloadCmd);
		__result = list.ToArray();
		ReloadIndexByEntity[__instance.entityId] = list.Count - 1;
	}

	[HarmonyPrefix]
	[HarmonyPatch("OnEntityActivated", typeof(int), typeof(Vector3i), typeof(EntityAlive))]
	private static bool HandleReload(Entity __instance, int _indexInBlockActivationCommands, Vector3i _tePos, EntityAlive _entityFocusing)
	{
		if (__instance is not EntityTurret turret || _entityFocusing == null)
		{
			return true;
		}

		if (!ReloadIndexByEntity.TryGetValue(__instance.entityId, out var reloadIndex) || _indexInBlockActivationCommands != reloadIndex)
		{
			return true;
		}

		TryRefillTurret(turret);
		return false; // consume the activation so pickup is not triggered
	}

	private static void TryRefillTurret(EntityTurret turret)
	{
		if (turret == null || turret.IsDead())
		{
			return;
		}

		if (!TryGetTurretItemValue(turret, out var itemValue))
		{
			return;
		}

		var capacity = GetCapacity(itemValue);
		if (capacity <= 0)
		{
			return;
		}

		turret.AmmoCount = capacity;
	}

	private static int GetCapacity(ItemValue itemValue)
	{
		var actions = itemValue?.ItemClass?.Actions;
		if (actions == null)
		{
			return 0;
		}

		foreach (var action in actions)
		{
			if (action is ItemActionRanged ranged)
			{
				try { return ranged.GetMaxAmmoCount(null); }
				catch { return 0; }
			}
		}

		return 0;
	}

	private static bool TryGetTurretItemValue(EntityTurret turret, out ItemValue itemValue)
	{
		itemValue = default;
		if (turret == null)
		{
			return false;
		}

		if (OriginalItemField != null)
		{
			var value = (ItemValue)OriginalItemField.GetValue(turret);
			if (BetterTurretsUtil.IsValidItemValue(value))
			{
				itemValue = value;
				return true;
			}
		}

		if (LastOriginalItemField != null)
		{
			var value = (ItemValue)LastOriginalItemField.GetValue(turret);
			if (BetterTurretsUtil.IsValidItemValue(value))
			{
				itemValue = value;
				return true;
			}
		}

		return false;
	}

	internal static void ReleaseReloadState(Entity entity)
	{
		if (entity == null)
		{
			return;
		}

		ReloadIndexByEntity.Remove(entity.entityId);
	}

}

[HarmonyPatch]
internal static class TurretCleanupPatch
{
	private static MethodBase _target;

	[HarmonyPrepare]
	private static bool Prepare()
	{
		var type = typeof(Entity);
		_target = AccessTools.Method(type, "OnRemovedFromWorld")
			?? AccessTools.Method(type, "OnRemovedFromWorld", new[] { typeof(bool) })
			?? AccessTools.Method(type, "OnEntityUnload")
			?? AccessTools.Method(type, "OnEntityUnload", new[] { typeof(bool) });
		return _target != null;
	}

	private static MethodBase TargetMethod()
	{
		return _target;
	}

	[HarmonyPostfix]
	private static void Postfix(Entity __instance)
	{
		if (__instance is not EntityTurret turret)
		{
			return;
		}

		TurretAmmoHudPatch.ReleaseTurret(turret);
		TurretReloadActivationPatch.ReleaseReloadState(__instance);
	}
}

