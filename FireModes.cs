using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ZeusOnlyMode
{
    public partial class ZeusOnlyPlugin
    {
        // ------------------------------------------------------------------
        // Fire modes: with "superzeus 2", each player picks what their
        // alt-fire does by pressing Inspect with the Zeus in hand; a center
        // panel names the mode. The pick is saved per SteamID in
        // configs/plugins/ZeusOnlyMode/fire_modes.json and kept forever. With
        // "superzeus 1" everyone fires the plain lightning ball, and saved
        // picks come back the next time all modes are on.
        // ------------------------------------------------------------------

        private enum FireMode { Super, Hyper, Reflect, Grapple, Camera, Sensor }

        private sealed record FireModeInfo(FireMode Mode, string Name, string Description);

        private static readonly FireModeInfo[] FireModes =
        {
            new(FireMode.Super, "Super Zeus", $"Lightning ball, {BallDamage} damage"),
            new(FireMode.Hyper, "Hyper Zeus", $"Rapid small balls, {HyperBallDamage} damage, {HyperBallsPerCharge} per charge"),
            new(FireMode.Reflect, "Reflect Zeus", $"Slower ball that bounces once, {BallDamage} damage"),
            new(FireMode.Grapple, "Grapple", "Pulls you to where it lands; kills on a direct hit"),
            new(FireMode.Camera, "Camera", "One wall camera per life; right-click to watch, W/A/S/D to pan"),
            new(FireMode.Sensor, "Sensor Probe", "One probe a round that warns your team when enemies pass"),
        };

        private const float ModePanelSeconds = 2.5f;

        private readonly Dictionary<ulong, FireMode> fireModes = new();
        private readonly Dictionary<int, (string Html, float ExpireAt)> modePanels = new();
        private string fireModesPath = "";

        private void LoadFireModes()
        {
            fireModesPath = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "ZeusOnlyMode", "fire_modes.json"));
            try
            {
                if (File.Exists(fireModesPath))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(fireModesPath));
                    foreach (var (id, mode) in loaded ?? new())
                        if (ulong.TryParse(id, out var steamId) && Enum.TryParse<FireMode>(mode, true, out var parsed))
                            fireModes[steamId] = parsed;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not read {Path}: {Message}", fireModesPath, ex.Message);
            }

            RegisterListener<Listeners.OnTick>(OnModePanelTick);
            RegisterListener<Listeners.OnClientDisconnect>(slot => modePanels.Remove(slot));
        }

        private void SaveFireModes()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fireModesPath)!);
                var data = fireModes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString());
                File.WriteAllText(fireModesPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not save {Path}: {Message}", fireModesPath, ex.Message);
            }
        }

        // What this player's alt-fire does right now
        private FireMode FireModeOf(CCSPlayerController player) =>
            AllFireModes && !player.IsBot && fireModes.TryGetValue(player.SteamID, out var mode) ? mode : FireMode.Super;

        private void OnFireModeButtons(CCSPlayerController player, PlayerButtons pressed)
        {
            if ((pressed & PlayerButtons.Inspect) == 0 || !AllFireModes || player == null || !player.IsValid || player.IsBot)
                return;

            var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
            if (weapon == null || !weapon.IsValid || weapon.DesignerName != "weapon_taser")
                return;

            var current = fireModes.TryGetValue(player.SteamID, out var mode) ? mode : FireMode.Super;
            int index = Array.FindIndex(FireModes, f => f.Mode == current);
            var next = FireModes[(index + 1) % FireModes.Length];

            StopCameraView(player.Slot);   // right-click wouldn't bring them back in another mode
            fireModes[player.SteamID] = next.Mode;
            SaveFireModes();
            ShowModePanel(player, next);
        }

        // Just the mode switched to, in the player's color
        private void ShowModePanel(CCSPlayerController player, FireModeInfo selected)
        {
            colorMenus.Remove(player.Slot);   // one panel at a time

            string accent = Hex(BoltGlowColor(GetZeusColor(player)));
            modePanels[player.Slot] = ($"<font color='{accent}'><b>{selected.Name}</b></font>", Server.CurrentTime + ModePanelSeconds);
        }

        // Fed every tick while shown, like the color menu
        private void OnModePanelTick()
        {
            if (modePanels.Count == 0)
                return;

            float now = Server.CurrentTime;
            foreach (var slot in modePanels.Keys.ToList())
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player == null || !player.IsValid || now >= modePanels[slot].ExpireAt)
                {
                    modePanels.Remove(slot);
                    continue;
                }

                try { player.PrintToCenterHtml(modePanels[slot].Html, 1); }
                catch { /* never let a render error hit the tick */ }
            }
        }
    }
}
