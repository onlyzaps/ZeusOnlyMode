using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Text;
using System.Text.Json;
using Vector3 = System.Numerics.Vector3;

namespace ZeusOnlyMode
{
    public partial class ZeusOnlyPlugin
    {
        // ------------------------------------------------------------------
        // zeuscolor: a per-player menu for the color of their primary zap
        // and their alt-fire lightning ball. Same shape as SimpleVote's menus:
        // a center-screen panel fed every tick, picked by chatting a number
        // (hidden), 0 for the next page, "cancel" to close. Choices persist
        // per SteamID in configs/plugins/ZeusOnlyMode/zeus_colors.json.
        // ------------------------------------------------------------------

        private sealed record ZeusColorOption(string Key, string Name, Color Color, bool Rainbow = false);

        private static readonly ZeusColorOption[] ZeusColorOptions =
        {
            new("default", "Electric Blue (Default)", Color.FromArgb(120, 190, 255)),
            new("rainbow", "Rainbow", Color.White, Rainbow: true),
            new("white", "White", Color.FromArgb(255, 255, 255)),
            new("red", "Red", Color.FromArgb(255, 40, 40)),
            new("crimson", "Crimson", Color.FromArgb(200, 10, 50)),
            new("orange", "Orange", Color.FromArgb(255, 130, 20)),
            new("gold", "Gold", Color.FromArgb(255, 200, 40)),
            new("yellow", "Yellow", Color.FromArgb(255, 245, 60)),
            new("lime", "Lime", Color.FromArgb(170, 255, 40)),
            new("green", "Green", Color.FromArgb(40, 220, 60)),
            new("emerald", "Emerald", Color.FromArgb(20, 200, 120)),
            new("mint", "Mint", Color.FromArgb(120, 255, 190)),
            new("teal", "Teal", Color.FromArgb(20, 190, 180)),
            new("cyan", "Cyan", Color.FromArgb(40, 240, 255)),
            new("sky", "Sky Blue", Color.FromArgb(110, 200, 255)),
            new("blue", "Blue", Color.FromArgb(40, 80, 255)),
            new("royal", "Royal Blue", Color.FromArgb(70, 100, 230)),
            new("indigo", "Indigo", Color.FromArgb(90, 40, 220)),
            new("purple", "Purple", Color.FromArgb(150, 50, 230)),
            new("violet", "Violet", Color.FromArgb(200, 110, 255)),
            new("magenta", "Magenta", Color.FromArgb(255, 40, 220)),
            new("hotpink", "Hot Pink", Color.FromArgb(255, 70, 160)),
            new("pink", "Pink", Color.FromArgb(255, 150, 200)),
            new("rose", "Rose", Color.FromArgb(255, 90, 120)),
            new("silver", "Silver", Color.FromArgb(190, 200, 215)),
        };

        private const int ColorsPerPage = 7;
        private const float ColorMenuIdleSeconds = 30f;

        private sealed class ZeusColorPrefs
        {
            public string? Primary { get; set; }
            public string? Alt { get; set; }
        }

        private enum ColorMenuTarget { Primary, Alt, Both }

        private sealed class ColorMenuState
        {
            public ColorMenuTarget? Target; // null = still choosing what to color
            public int Page;
            public float ExpireAt;
            public string Html = "";
        }

        private readonly Dictionary<ulong, ZeusColorPrefs> zeusColorPrefs = new();
        private readonly Dictionary<int, ColorMenuState> colorMenus = new();
        private string zeusColorsPath = "";

        private void LoadZeusColors()
        {
            // plugins/ZeusOnlyMode -> configs/plugins/ZeusOnlyMode, next to the
            // plugin config, so updating the plugin never wipes anyone's colors
            zeusColorsPath = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "ZeusOnlyMode", "zeus_colors.json"));

            try
            {
                if (File.Exists(zeusColorsPath))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, ZeusColorPrefs>>(File.ReadAllText(zeusColorsPath));
                    foreach (var (id, prefs) in loaded ?? new())
                        if (ulong.TryParse(id, out var steamId))
                            zeusColorPrefs[steamId] = prefs;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not read {Path}: {Message}", zeusColorsPath, ex.Message);
            }

            // Bare name (no css_ prefix) so CSS's own !/ chat dispatch never
            // fires it a second time on top of our chat handler
            AddCommand("zeuscolor", "Choose the color of your Zeus zap and lightning ball", (player, info) =>
            {
                if (player == null || !player.IsValid || player.IsBot)
                {
                    info.ReplyToCommand("zeuscolor can only be used by a player.");
                    return;
                }
                OpenColorMenu(player);
            });

            AddCommandListener("say", OnZeusColorChat);
            AddCommandListener("say_team", OnZeusColorChat);

            RegisterEventHandler<EventWeaponFire>(OnZapFired);
            RegisterEventHandler<EventBulletImpact>(OnZapImpact);

            RegisterListener<Listeners.OnTick>(OnColorMenuTick);
            RegisterListener<Listeners.OnClientDisconnect>(slot =>
            {
                colorMenus.Remove(slot);
                pendingZaps.Remove(slot);
            });
        }

        // zeuscolor (with or without ! or /) and open-menu input are both
        // swallowed so other players never see them
        private HookResult OnZeusColorChat(CCSPlayerController? player, CommandInfo info)
        {
            if (player == null || !player.IsValid || player.IsBot)
                return HookResult.Continue;

            string message = info.GetArg(1).Trim();
            string clean = message.TrimStart('!', '/').Trim();

            if (TryHandleColorMenuInput(player, clean))
                return HookResult.Handled;

            if (!clean.Equals("zeuscolor", StringComparison.OrdinalIgnoreCase))
                return HookResult.Continue;

            OpenColorMenu(player);
            return HookResult.Handled;
        }

        // --- Colored primary zap -------------------------------------------
        //
        // The stock zap tracer is drawn by the client and can't be recolored,
        // so players who picked a zap color get a lightning bolt of that color
        // drawn along their shot. weapon_fire marks the shot; the game's
        // bullet_impact for it gives the exact end point. Players on Default
        // keep the stock look untouched.

        private const float StockZeusRange = 190f;
        private readonly HashSet<int> pendingZaps = new();

        private HookResult OnZapFired(EventWeaponFire ev, GameEventInfo info)
        {
            var player = ev.Userid;
            if (player == null || !player.IsValid || !ev.Weapon.Contains("taser"))
                return HookResult.Continue;

            if (GetZeusColor(player, alt: false) == null)
                return HookResult.Continue;

            int slot = player.Slot;
            pendingZaps.Add(slot);

            // No impact event (zapped thin air)? Draw the full reach next frame.
            Server.NextFrame(() =>
            {
                if (!pendingZaps.Remove(slot) || !player.IsValid)
                    return;

                var pawn = player.PlayerPawn.Value;
                if (pawn == null || pawn.AbsOrigin == null)
                    return;

                var eye = ToVec3(pawn.AbsOrigin) + new Vector3(0, 0, pawn.ViewOffset.Z);
                AngleVectors(new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0f), out var forward, out _, out _);
                DrawZap(player, eye + forward * StockZeusRange);
            });

            return HookResult.Continue;
        }

        private HookResult OnZapImpact(EventBulletImpact ev, GameEventInfo info)
        {
            var player = ev.Userid;
            if (player == null || !player.IsValid || !pendingZaps.Remove(player.Slot))
                return HookResult.Continue;

            DrawZap(player, new Vector3(ev.X, ev.Y, ev.Z));
            return HookResult.Continue;
        }

        private void DrawZap(CCSPlayerController player, Vector3 end)
        {
            var color = GetZeusColor(player, alt: false);
            var pawn = player.PlayerPawn.Value;
            if (color == null || pawn == null || pawn.AbsOrigin == null)
                return;

            var eye = ToVec3(pawn.AbsOrigin) + new Vector3(0, 0, pawn.ViewOffset.Z);
            AngleVectors(new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0f), out var forward, out var right, out var up);
            var muzzle = eye + forward * 16f + right * (pawn.LeftHanded ? -5f : 5f) - up * 6f;

            // Two quick strokes with different shapes, like the ball's kill bolt
            DrawLightningBolt(muzzle, end, 0.07f, color, 0.35f, false);
            AddTimer(0.07f, () => DrawLightningBolt(muzzle, end, 0.1f, color, 0.35f, false), TimerFlags.STOP_ON_MAPCHANGE);
        }

        private void SaveZeusColors()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(zeusColorsPath)!);
                var data = zeusColorPrefs.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
                File.WriteAllText(zeusColorsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not save {Path}: {Message}", zeusColorsPath, ex.Message);
            }
        }

        // --- Lookups used by the zap and the lightning ball ----------------

        // null = the player never picked one (or picked Default)
        private ZeusColorOption? GetZeusColor(CCSPlayerController? player, bool alt)
        {
            if (player == null || !player.IsValid || player.IsBot)
                return null;

            if (!zeusColorPrefs.TryGetValue(player.SteamID, out var prefs))
                return null;

            var option = FindColorOption(alt ? prefs.Alt : prefs.Primary);
            return option == null || option.Key == "default" ? null : option;
        }

        private static ZeusColorOption? FindColorOption(string? key) =>
            key == null ? null : ZeusColorOptions.FirstOrDefault(o => o.Key == key);

        // Rainbow cycles through the hues over time; offset spreads it along a bolt
        private static Color ResolveColor(ZeusColorOption option, float offset = 0f) =>
            option.Rainbow ? HueToColor(Server.CurrentTime * 0.5f + offset) : option.Color;

        private static Color HueToColor(float hue)
        {
            hue = (hue % 1f + 1f) % 1f * 6f;
            float x = 1f - Math.Abs(hue % 2f - 1f);
            var (r, g, b) = (int)hue switch
            {
                0 => (1f, x, 0f),
                1 => (x, 1f, 0f),
                2 => (0f, 1f, x),
                3 => (0f, x, 1f),
                4 => (x, 0f, 1f),
                _ => (1f, 0f, x)
            };
            return Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        private static Color Whiten(Color c, float amount) => Color.FromArgb(255,
            (int)(c.R + (255 - c.R) * amount),
            (int)(c.G + (255 - c.G) * amount),
            (int)(c.B + (255 - c.B) * amount));

        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        // --- Menu ----------------------------------------------------------

        private void OpenColorMenu(CCSPlayerController player)
        {
            colorMenus[player.Slot] = new ColorMenuState { ExpireAt = Server.CurrentTime + ColorMenuIdleSeconds };
            RefreshColorMenu(player);
            player.PrintToChat($" {ChatColors.Gold}Zeus Color{ChatColors.Default}: chat a {ChatColors.Green}number{ChatColors.Default} to choose, or {ChatColors.Green}cancel{ChatColors.Default} to close.");
        }

        private void CloseColorMenu(CCSPlayerController player)
        {
            if (colorMenus.Remove(player.Slot))
                player.PrintToCenterHtml(" ");
        }

        // Called from OnSay before anything else. Numbers and "cancel" belong
        // to the open menu (and are hidden); anything else is ordinary chat.
        private bool TryHandleColorMenuInput(CCSPlayerController player, string input)
        {
            if (!colorMenus.TryGetValue(player.Slot, out var menu))
                return false;

            if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                CloseColorMenu(player);
                player.PrintToChat($" {ChatColors.Default}Zeus color menu closed.");
                return true;
            }

            if (!int.TryParse(input, out int choice))
                return false;

            menu.ExpireAt = Server.CurrentTime + ColorMenuIdleSeconds;

            if (menu.Target == null)
            {
                switch (choice)
                {
                    case 0: CloseColorMenu(player); return true;
                    case 1: menu.Target = ColorMenuTarget.Primary; break;
                    case 2: menu.Target = ColorMenuTarget.Alt; break;
                    case 3: menu.Target = ColorMenuTarget.Both; break;
                    default: return true;
                }
                menu.Page = 0;
                RefreshColorMenu(player);
                return true;
            }

            int pages = (ZeusColorOptions.Length + ColorsPerPage - 1) / ColorsPerPage;
            if (choice == 0)
            {
                menu.Page = (menu.Page + 1) % pages;
                RefreshColorMenu(player);
                return true;
            }

            int index = menu.Page * ColorsPerPage + choice - 1;
            if (choice < 1 || choice > ColorsPerPage || index >= ZeusColorOptions.Length)
                return true;

            ApplyColorChoice(player, menu.Target.Value, ZeusColorOptions[index]);
            CloseColorMenu(player);
            return true;
        }

        private void ApplyColorChoice(CCSPlayerController player, ColorMenuTarget target, ZeusColorOption option)
        {
            if (!zeusColorPrefs.TryGetValue(player.SteamID, out var prefs))
                zeusColorPrefs[player.SteamID] = prefs = new ZeusColorPrefs();

            if (target != ColorMenuTarget.Alt) prefs.Primary = option.Key;
            if (target != ColorMenuTarget.Primary) prefs.Alt = option.Key;
            SaveZeusColors();

            string what = target switch
            {
                ColorMenuTarget.Primary => "Zeus zap",
                ColorMenuTarget.Alt => "lightning ball",
                _ => "Zeus zap and lightning ball"
            };
            player.PrintToChat($" {ChatColors.Default}Your {what} color is now {ChatColors.Green}{option.Name}{ChatColors.Default}.");
        }

        private void RefreshColorMenu(CCSPlayerController player)
        {
            if (colorMenus.TryGetValue(player.Slot, out var menu))
                menu.Html = BuildColorMenuHtml(player, menu);
        }

        private string BuildColorMenuHtml(CCSPlayerController player, ColorMenuState menu)
        {
            var sb = new StringBuilder();

            if (menu.Target == null)
            {
                zeusColorPrefs.TryGetValue(player.SteamID, out var prefs);
                var primary = FindColorOption(prefs?.Primary) ?? ZeusColorOptions[0];
                var alt = FindColorOption(prefs?.Alt) ?? ZeusColorOptions[0];

                sb.Append("<font color='#FFD700'><b>Zeus Color - chat a number</b></font>");
                sb.Append($"<br><font color='#FF5722'>1:</font> <font color='#EAD1AF'>Zeus Zap</font> <font color='#9E9E9E'>(</font>{ColoredName(primary)}<font color='#9E9E9E'>)</font>");
                sb.Append($"<br><font color='#FF5722'>2:</font> <font color='#EAD1AF'>Lightning Ball</font> <font color='#9E9E9E'>(</font>{ColoredName(alt)}<font color='#9E9E9E'>)</font>");
                sb.Append("<br><font color='#FF5722'>3:</font> <font color='#EAD1AF'>Both</font>");
                sb.Append("<br><font color='#FF5722'>0:</font> <font color='#9E9E9E'>Close</font>");
                return sb.ToString();
            }

            int pages = (ZeusColorOptions.Length + ColorsPerPage - 1) / ColorsPerPage;
            string title = menu.Target switch
            {
                ColorMenuTarget.Primary => "Zap Color",
                ColorMenuTarget.Alt => "Lightning Ball Color",
                _ => "Zap + Ball Color"
            };
            sb.Append($"<font color='#FFD700'><b>{title} - page {menu.Page + 1}/{pages}</b></font>");

            int start = menu.Page * ColorsPerPage;
            for (int i = start; i < Math.Min(start + ColorsPerPage, ZeusColorOptions.Length); i++)
                sb.Append($"<br><font color='#FF5722'>{i - start + 1}:</font> {ColoredName(ZeusColorOptions[i])}");

            sb.Append("<br><font color='#FF5722'>0:</font> <font color='#9E9E9E'>Next Page</font>");
            return sb.ToString();
        }

        // Each name is drawn in its own color; Rainbow gets one hue per letter
        private static string ColoredName(ZeusColorOption option)
        {
            if (!option.Rainbow)
                return $"<font color='{Hex(option.Color)}'>{option.Name}</font>";

            var sb = new StringBuilder();
            for (int i = 0; i < option.Name.Length; i++)
                sb.Append($"<font color='{Hex(HueToColor(i / (float)option.Name.Length))}'>{option.Name[i]}</font>");
            return sb.ToString();
        }

        // The center panel only stays up while it keeps being fed, so re-send
        // every tick (the same approach SimpleVote's vote panel uses)
        private void OnColorMenuTick()
        {
            if (colorMenus.Count == 0)
                return;

            float now = Server.CurrentTime;
            foreach (var slot in colorMenus.Keys.ToList())
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player == null || !player.IsValid)
                {
                    colorMenus.Remove(slot);
                    continue;
                }

                if (now >= colorMenus[slot].ExpireAt)
                {
                    CloseColorMenu(player);
                    continue;
                }

                try { player.PrintToCenterHtml(colorMenus[slot].Html); }
                catch { /* never let a render error hit the tick */ }
            }
        }
    }
}
