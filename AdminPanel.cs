using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ZeusOnlyMode
{
    public partial class ZeusOnlyPlugin
    {
        // ------------------------------------------------------------------
        // Admin panel: an admin (@css/generic) who holds Inspect (F) and
        // Reload (R) together for two seconds gets a center panel of
        // checkboxes, one per feature, that turn it on or off for every
        // player. Holding the two keys again closes it.
        //
        // A center panel cannot be clicked, so while it is open: Reload (R)
        // moves down, Inspect (F) ticks or unticks the highlighted row. Keys
        // act when released, and never when they were part of the two-key
        // hold, so opening and closing it changes nothing. The panel does
        // not freeze the admin, and closes itself if they die or leave.
        // The fire mode and color rows are saved in
        // configs/plugins/ZeusOnlyMode/admin_features.json.
        // ------------------------------------------------------------------

        private const float AdminPanelHoldSeconds = 2.0f;
        private const float AdminPanelTapSeconds = 0.6f;
        private const int AdminPanelRows = 6;   // rows visible at once

        private sealed record PanelItem(string Name, Func<bool> IsOn, Action<string> Toggle);

        private sealed class AdminHold
        {
            public float ComboStart = -1;   // when both keys went down together
            public bool Fired;              // this hold already opened/closed the panel
            public bool ComboSeen;          // both keys were down since neither was last held
            public float ReloadDown;
            public float InspectDown;
        }

        private sealed class AdminPanelState
        {
            public int Cursor;
        }

        private readonly Dictionary<int, AdminHold> adminHolds = new();
        private readonly Dictionary<int, AdminPanelState> adminPanels = new();

        private bool zeusColorsEnabled = true;
        private readonly HashSet<FireMode> disabledModes = new();
        private string adminFeaturesPath = "";
        private PanelItem[] panelItems = Array.Empty<PanelItem>();

        private bool ModeAllowed(FireMode mode) => !disabledModes.Contains(mode);

        private void LoadAdminPanel()
        {
            adminFeaturesPath = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "ZeusOnlyMode", "admin_features.json"));
            try
            {
                if (File.Exists(adminFeaturesPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(adminFeaturesPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("colors", out var colors) && (colors.ValueKind == JsonValueKind.True || colors.ValueKind == JsonValueKind.False))
                        zeusColorsEnabled = colors.GetBoolean();
                    if (root.TryGetProperty("disabledModes", out var modes) && modes.ValueKind == JsonValueKind.Array)
                        foreach (var m in modes.EnumerateArray())
                            if (Enum.TryParse<FireMode>(m.GetString(), true, out var parsed) && parsed != FireMode.Super)
                                disabledModes.Add(parsed);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not read {Path}: {Message}", adminFeaturesPath, ex.Message);
            }

            var items = new List<PanelItem>
            {
                new("Zeus Only Mode", () => zeusOnlyEnabled, who => SetZeusOnlyMode(!zeusOnlyEnabled)),
                new("Super Zeus", () => superZeusEnabled, who => SetSuperZeusLevel(superZeusEnabled ? 0 : 1, who)),
                new("All Fire Modes", () => AllFireModes, who => SetSuperZeusLevel(AllFireModes ? 1 : 2, who)),
            };
            foreach (var info in FireModes.Where(f => f.Mode != FireMode.Super))
            {
                var mode = info.Mode;
                var name = info.Name;
                items.Add(new(name, () => ModeAllowed(mode), who => SetModeAllowed(mode, name, !ModeAllowed(mode))));
            }
            items.Add(new("Zeus Colors", () => zeusColorsEnabled, who => SetColorsEnabled(!zeusColorsEnabled)));
            panelItems = items.ToArray();

            RegisterListener<Listeners.OnTick>(OnAdminPanelTick);
            RegisterListener<Listeners.OnMapStart>(_ =>
            {
                adminPanels.Clear();
                adminHolds.Clear();
            });
        }

        private void SaveAdminFeatures()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(adminFeaturesPath)!);
                File.WriteAllText(adminFeaturesPath, JsonSerializer.Serialize(new
                {
                    colors = zeusColorsEnabled,
                    disabledModes = disabledModes.Select(m => m.ToString()).ToArray()
                }));
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not write {Path}: {Message}", adminFeaturesPath, ex.Message);
            }
        }

        private void SetModeAllowed(FireMode mode, string name, bool allowed)
        {
            if (allowed)
                disabledModes.Remove(mode);
            else
            {
                disabledModes.Add(mode);
                switch (mode)
                {
                    case FireMode.Grapple: ClearGrapples(); break;
                    case FireMode.Camera: ClearCameras(); break;
                    case FireMode.Sensor: ClearSensors(); break;
                }
            }

            SaveAdminFeatures();
            Server.PrintToChatAll($" {ChatColors.LightBlue}{name}{ChatColors.Default} was {(allowed ? "enabled" : "disabled")} by an admin.");
        }

        private void SetColorsEnabled(bool enabled)
        {
            zeusColorsEnabled = enabled;
            if (!enabled)
                colorMenus.Clear();

            SaveAdminFeatures();
            Server.PrintToChatAll($" {ChatColors.LightBlue}Zeus Colors{ChatColors.Default} were {(enabled ? "enabled" : "disabled")} by an admin.");
        }

        // Called every tick for each living player with their button state.
        // Returns true while this player's panel is open.
        private bool HandleAdminPanelButtons(CCSPlayerController player, PlayerButtons buttons, PlayerButtons previous)
        {
            float now = Server.CurrentTime;
            bool reload = (buttons & PlayerButtons.Reload) != 0;
            bool inspect = (buttons & PlayerButtons.Inspect) != 0;
            bool wasReload = (previous & PlayerButtons.Reload) != 0;
            bool wasInspect = (previous & PlayerButtons.Inspect) != 0;

            adminPanels.TryGetValue(player.Slot, out var panel);

            if (!reload && !inspect && !wasReload && !wasInspect)
            {
                // Idle: forget any finished hold without allocating anything
                if (adminHolds.TryGetValue(player.Slot, out var idle))
                {
                    idle.ComboStart = -1;
                    idle.Fired = false;
                    idle.ComboSeen = false;
                }
                return panel != null;
            }

            if (!adminHolds.TryGetValue(player.Slot, out var hold))
                adminHolds[player.Slot] = hold = new AdminHold();

            if (reload && !wasReload) hold.ReloadDown = now;
            if (inspect && !wasInspect) hold.InspectDown = now;

            if (reload && inspect)
            {
                hold.ComboSeen = true;
                if (hold.ComboStart < 0)
                    hold.ComboStart = now;
                else if (!hold.Fired && now - hold.ComboStart >= AdminPanelHoldSeconds)
                {
                    hold.Fired = true;
                    ToggleAdminPanel(player);
                    adminPanels.TryGetValue(player.Slot, out panel);
                }
            }
            else
            {
                hold.ComboStart = -1;
                if (!reload && !inspect)
                {
                    hold.Fired = false;
                    hold.ComboSeen = false;
                }
            }

            if (panel == null)
                return false;

            // A quick tap released on its own is a menu action
            if (!hold.ComboSeen)
            {
                if (wasReload && !reload && now - hold.ReloadDown < AdminPanelTapSeconds)
                    panel.Cursor = (panel.Cursor + 1) % panelItems.Length;
                else if (wasInspect && !inspect && now - hold.InspectDown < AdminPanelTapSeconds)
                    ToggleSelected(player, panel);
            }
            return true;
        }

        private void ToggleAdminPanel(CCSPlayerController player)
        {
            if (adminPanels.Remove(player.Slot))
                return;

            if (!AdminManager.PlayerHasPermissions(player, SuperZeusPermission))
                return;

            colorMenus.Remove(player.Slot);   // one panel at a time
            modePanels.Remove(player.Slot);
            adminPanels[player.Slot] = new AdminPanelState();
        }

        private void ToggleSelected(CCSPlayerController player, AdminPanelState panel)
        {
            if (!AdminManager.PlayerHasPermissions(player, SuperZeusPermission))
            {
                adminPanels.Remove(player.Slot);
                return;
            }

            try { panelItems[panel.Cursor].Toggle(player.PlayerName); }
            catch (Exception ex) { Logger.LogError("Admin panel toggle failed: {Message}", ex.Message); }
        }

        private string BuildAdminPanelHtml(AdminPanelState panel)
        {
            int first = Math.Clamp(panel.Cursor - AdminPanelRows / 2, 0, Math.Max(0, panelItems.Length - AdminPanelRows));
            int last = Math.Min(panelItems.Length, first + AdminPanelRows);

            var lines = new List<string> { "<font color='#FFD700'><b>Zeus Admin</b></font>" };
            for (int i = first; i < last; i++)
            {
                bool on = panelItems[i].IsOn();
                string box = on ? "<font color='#55FF55'>[X]</font>" : "<font color='#999999'>[&nbsp;&nbsp;]</font>";
                string name = panelItems[i].Name;
                lines.Add(i == panel.Cursor
                    ? $"<font color='#FFFFFF'><b>&gt;&nbsp;{box}&nbsp;{name}</b></font>"
                    : $"&nbsp;&nbsp;{box}&nbsp;<font color='#CCCCCC'>{name}</font>");
            }
            lines.Add("<font color='#888888'>R: next &nbsp; F: tick &nbsp; hold F+R: close</font>");
            return string.Join("<br>", lines);
        }

        private void OnAdminPanelTick()
        {
            if (adminPanels.Count == 0)
                return;

            foreach (var slot in adminPanels.Keys.ToList())
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player == null || !player.IsValid || !player.PawnIsAlive)
                {
                    adminPanels.Remove(slot);
                    continue;
                }

                try { player.PrintToCenterHtml(BuildAdminPanelHtml(adminPanels[slot]), 1); }
                catch { /* never let a render error hit the tick */ }
            }
        }
    }
}
