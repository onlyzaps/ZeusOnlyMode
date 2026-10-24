using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using static CounterStrikeSharp.API.Core.Listeners;

namespace ZeusOnlyMode
{
    public partial class ZeusOnlyPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Only Mode";
        public override string ModuleVersion => "3.0";

        private bool zeusOnlyEnabled = false;

        private CounterStrikeSharp.API.Modules.Timers.Timer? zeusReminderTimer;

        // Whitelist of allowed weapons (lowercase, no "weapon_" prefix)
        private readonly HashSet<string> allowedWeapons = new HashSet<string>
        {
            "taser",
            "knife",
            // Grenades and utilities
            "flashbang",
            "hegrenade", "grenade", // HE grenade
            "smokegrenade", "smoke",
            "molotov", "incgrenade", "firebomb", // T and CT fire nades
            "decoy", "decoygrenade", "c4"
        };

        // Weapon prices in CS2
        private readonly Dictionary<string, int> weaponPrices = new()
        {
            { "ak47", 2700 },
            { "m4a1", 3100 }, { "m4a1_silencer", 2900 },
            { "awp", 4750 },
            { "ssg08", 1700 },
            { "aug", 3300 },
            { "sg556", 3000 },
            { "galilar", 1800 },
            { "famas", 2050 },
            { "p90", 2350 },
            { "ump45", 1200 },
            { "mp7", 1500 },
            { "mp9", 1250 },
            { "mac10", 1050 },
            { "nova", 1050 },
            { "xm1014", 2000 },
            { "mag7", 1300 },
            { "m249", 5200 },
            { "negev", 1700 },
            { "glock", 200 },
            { "hkp2000", 200 }, { "usp_silencer", 200 },
            { "p250", 300 },
            { "fiveseven", 500 }, { "cz75a", 500 },
            { "deagle", 700 }, { "revolver", 600 },
            { "tec9", 500 }
            // Grenades are omitted because they’re allowed
};

        // Dictionary to hold saved weapons per player
        
        private readonly Dictionary<ulong, List<string>> savedWeapons = new();
        public override void Load(bool hotReload)
        {
            AddCommand("css_zeus_mode", "Enable or disable Zeus-only mode", Command_ToggleZeusMode);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventItemPickup>(OnItemPickup);

            RegisterListener<Listeners.OnClientPutInServer>(OnPlayerJoin);
            RegisterEventHandler<EventGameEnd>(OnMapEnd);
            // Hook buy command
            AddCommandListener("buy", OnBuyCommand);

            LoadSuperZeus();
            LoadZeusColors();
        }
        private HookResult OnMapEnd(EventGameEnd ev, GameEventInfo info)
        {
            if (!zeusOnlyEnabled)
                return HookResult.Continue;

            zeusOnlyEnabled = false;

            Server.PrintToChatAll("Zeus Mode Was Disabled — Thanks For Playing!");

            foreach (var p in Utilities.GetPlayers())
            {
                if (p == null || !p.IsValid || p.TeamNum < 2 || !p.PawnIsAlive) continue;

                RestorePlayerLoadout(p);
            }

            // Stop reminder timer
            zeusReminderTimer?.Kill();
            zeusReminderTimer = null;

            return HookResult.Continue;
        }

        private void Command_ToggleZeusMode(CCSPlayerController? player, CommandInfo info)
        {
            if (info.ArgCount < 2)
            {
                player?.PrintToChat("Usage: css_zeus_mode <0/1>");
                player?.PrintToChat($"Zeus Mode Is Currently {(zeusOnlyEnabled ? "Enabled" : "Disabled")}.");
                return;
            }

            bool newState = info.GetArg(1) == "1";

            if (newState == zeusOnlyEnabled)
            {
                player?.PrintToChat($"Zeus Mode Is Already {(zeusOnlyEnabled ? "Enabled" : "Disabled")}.");
                return;
            }

            zeusOnlyEnabled = newState;

            if (zeusOnlyEnabled) // after you set zeusOnlyEnabled = newState and it's true
            {
                Server.PrintToChatAll("Zeus Mode Enabled — Loadouts Have Been Snapshot.");

                foreach (var p in Utilities.GetPlayers())
                {
                    if (p == null || !p.IsValid || p.TeamNum < 2 || !p.PawnIsAlive)
                        continue;

                    // safe to manipulate loadout
                    SavePlayerLoadout(p);
                    StripIllegalWeapons(p);

                    // ensure Zeus
                    p.GiveNamedItem("weapon_taser");
                    p.ExecuteClientCommand("slot11");
                }

                // Start reminder timer
                zeusReminderTimer = AddTimer(5.0f, () =>
                {
                    if (zeusOnlyEnabled)
                    {
                        Server.PrintToChatAll("Zeus, Utility, and Knife only!");
                    }
                }, TimerFlags.REPEAT);
            }
            else
            {
                Server.PrintToChatAll("Zeus Mode Disabled — Loadouts Have Been Restored.");

                // Restore immediately
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p == null || !p.IsValid || p.TeamNum < 2) continue;
                    RestorePlayerLoadout(p);
                }

                // Stop reminder timer
                zeusReminderTimer?.Kill();
                zeusReminderTimer = null;
            }
        }

        public void SavePlayerLoadout(CCSPlayerController player)
        {
            if (player == null || !player.IsValid || !player.PawnIsAlive)
                return;

            ulong steamId = player.SteamID;
            savedWeapons[steamId] = new List<string>();

            var pawn = player.Pawn.Value;
            if (pawn == null) return;

            var weaponServices = pawn.WeaponServices;
            if (weaponServices == null) return;

            foreach (var handle in weaponServices.MyWeapons)
            {
                var weapon = handle.Value;
                if (weapon == null) continue;

                string weaponName = weapon.DesignerName; // e.g. "weapon_ak47"
                savedWeapons[steamId].Add(weaponName);
            }
        }

        public void RestorePlayerLoadout(CCSPlayerController player)
        {
            if (player == null || !player.IsValid || !player.PawnIsAlive)
                return;

            ulong steamId = player.SteamID;
            if (!savedWeapons.TryGetValue(steamId, out var weapons))
                return;

            // Remove everything the player currently has
            foreach (var handle in player.PlayerPawn.Value?.WeaponServices?.MyWeapons
                     ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
            {
                var weapon = handle.Value;
                if (weapon != null && weapon.IsValid)
                {
                    weapon.AcceptInput("Kill");
                }
            }

            // Give back saved weapons
            foreach (var weaponName in weapons)
            {
                player.GiveNamedItem(weaponName);
            }
        }

        private void SavePlayerLoadoutOld(CCSPlayerController? player)
        {
            if (player == null || !player.IsValid || player.TeamNum < 2 || player.SteamID == 0) return;

            var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>();
            savedWeapons[player.SteamID] = weapons
                .Select(w => w.Value?.DesignerName ?? "")
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();
        }

        

        private void RestorePlayerLoadoutOld(CCSPlayerController? player)
        {
            if (player == null || !player.IsValid || player.TeamNum < 2 || player.SteamID == 0) return;

            foreach (var weapon in player.PlayerPawn.Value?.WeaponServices?.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
                weapon.Value?.Remove();

            if (savedWeapons.TryGetValue(player.SteamID, out var weaponList))
            {
                foreach (var weapon in weaponList)
                    player.GiveNamedItem(weapon);
            }
        }

        private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
        {
            // Clear all loadouts when a new round starts
            savedWeapons.Clear();

            if (!zeusOnlyEnabled)
                return HookResult.Continue;

            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid || player.TeamNum < 2) continue;

                // Save fresh round loadout before stripping
                SavePlayerLoadout(player);

                StripIllegalWeapons(player);

                // Ensure Zeus is in slot
                bool hasTaser = player.PlayerPawn.Value?.WeaponServices?.MyWeapons
                    .Any(w => w.Value != null
                           && w.Value.IsValid
                           && w.Value.DesignerName.Equals("weapon_taser", StringComparison.OrdinalIgnoreCase))
                    ?? false;

                if (!hasTaser)
                {
                    player.GiveNamedItem("weapon_taser");
                }
                player.ExecuteClientCommand("slot11");
            }

            return HookResult.Continue;
        }

        // Removes weapons not in whitelist
        private void StripIllegalWeapons(CCSPlayerController player)
        {
            foreach (var weapon in player.PlayerPawn.Value?.WeaponServices?.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
            {
                var weapEnt = weapon.Value;

                if (weapEnt != null && weapEnt.IsValid)
                {
                    string className = weapEnt.DesignerName.Replace("weapon_", "").ToLowerInvariant();
                    if (!allowedWeapons.Contains(className))
                    {
                        weapEnt.Remove(); // detaches from player
                    }
                }
            }
        }

        // Block buying before it happens
        private HookResult OnBuyCommand(CCSPlayerController? player, CommandInfo cmd)
        {
            if (!zeusOnlyEnabled || player == null || !player.IsValid || player.TeamNum < 2)
                return HookResult.Continue;

            if (cmd.ArgCount < 2) return HookResult.Continue;

            string weaponName = cmd.GetArg(1)
                .ToLowerInvariant()
                .Replace("weapon_", "")
                .Replace("item_", "")
                .Trim();

            // If name is "unused", let it through. Check in pickup
            if (weaponName == "unused")
                return HookResult.Continue;

            if (!allowedWeapons.Contains(weaponName))
            {
                player.PrintToChat("Only Zeus and Utility Are Allowed.");
                return HookResult.Stop;
            }

            return HookResult.Continue;
        }

        // Remove illegal pickups from ground
        private HookResult OnItemPickup(EventItemPickup ev, GameEventInfo info)
        {
            if (!zeusOnlyEnabled) return HookResult.Continue;

            string weaponName = ev.Item
                .ToLowerInvariant()
                .Replace("weapon_", "")
                .Replace("item_", "")
                .Trim();

            if (!allowedWeapons.Contains(weaponName))
            {
                var player = ev.Userid;
                if (player != null && player.IsValid)
                {
                    var pawn = player.PlayerPawn.Value;
                    var weaponServices = pawn?.WeaponServices;

                    // Find the illegal weapon entity just picked up
                    foreach (var handle in weaponServices?.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
                    {
                        var weapEnt = handle.Value;
                        if (weapEnt != null && weapEnt.IsValid)
                        {
                            string className = weapEnt.DesignerName.Replace("weapon_", "").ToLowerInvariant();
                            if (className == weaponName)
                            {
                                // Remove from player and kill entity so it disappears completely
                                weapEnt.AcceptInput("Kill");

                                //player.PrintToChat("[Zeus Mode] Illegal weapon removed!");
                                break;
                            }
                        }
                    }
                }
            }

            return HookResult.Continue;
        }

        private void OnPlayerJoin(int playerSlot)
        {
            var player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player == null || !player.IsValid || player.SteamID == 0) return;

            if (zeusOnlyEnabled)
            {
                // Fresh snapshot of whatever they spawned with
                SavePlayerLoadout(player);

                StripIllegalWeapons(player);
                player.GiveNamedItem("weapon_taser");
                player.ExecuteClientCommand("slot11");
            }
        }
        public override void Unload(bool hotReload)
        {
            zeusReminderTimer?.Kill();
            zeusReminderTimer = null;

            UnloadSuperZeus();
        }
    }
}
