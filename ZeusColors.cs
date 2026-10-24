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
            RegisterListener<Listeners.OnTick>(() =>
            {
                if (colorZaps.Count > 0)
                    UpdateColorZaps();
            });
            RegisterListener<Listeners.OnMapStart>(_ => colorZaps.Clear());
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
        // The stock zap still plays as normal; players who picked a zap color
        // also get a colored copy of it laid over the top (ColorZap below).
        // weapon_fire marks the shot; the game's bullet_impact for it gives
        // the exact end point. Players on Default see only the stock zap.

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

            // Same two wires the stock zap draws (weapon_tracers_taser_wire1a/b),
            // each falling under its own gravity
            colorZaps.Add(new ColorZap(this, muzzle, end, color, 350f));
            colorZaps.Add(new ColorZap(this, muzzle, end, color, 500f));

            // Stock zap glows where it lands (weapon_taser_glow_impact)
            var flash = SpawnZapFlash(end, ResolveColor(color));
            if (flash != null)
                RemoveLater(HandleOf(flash), ZapCrackleSeconds);
        }

        // --- Colored copy of the stock zap ----------------------------------
        //
        // The stock zap (weapon_tracers_taser) is two wire ropes strung from
        // the muzzle to the impact point: they bulge slightly, sag under
        // gravity pinned at both ends, and fade out over 1.8s, while a bright
        // crackle rope (wire2) flashes along them for the first instant. Its
        // colors are baked into the particle files, so this rebuilds the same
        // shapes and timing out of beams in the player's color and lays them
        // over the stock zap.

        private const int ZapWireSegments = 8;
        private const float ZapLifetime = 1.8f;
        private const float ZapCrackleSeconds = 0.2f;
        private const float ZapFadeStart = 0.4f;
        private const float ZapMaxSag = 120f;

        private readonly List<ColorZap> colorZaps = new();

        private sealed class ColorZap
        {
            private readonly ZeusOnlyPlugin plugin;
            private readonly Vector3[] basePoints;
            private readonly Vector3[] bulge;
            private readonly List<CHandle<CBeam>> crackle = new();
            private readonly List<CHandle<CBeam>> wire = new();
            private readonly ZeusColorOption color;
            private readonly float gravity;
            private readonly float startTime;

            public ColorZap(ZeusOnlyPlugin plugin, Vector3 start, Vector3 end, ZeusColorOption color, float gravity)
            {
                this.plugin = plugin;
                this.color = color;
                this.gravity = gravity;
                startTime = Server.CurrentTime;

                basePoints = new Vector3[ZapWireSegments + 1];
                bulge = new Vector3[ZapWireSegments + 1];
                for (int i = 0; i <= ZapWireSegments; i++)
                {
                    float t = i / (float)ZapWireSegments;
                    basePoints[i] = Vector3.Lerp(start, end, t);
                    bulge[i] = plugin.RandomUnit() * 6f * MathF.Sin(t * MathF.PI);
                }

                var points = PointsAt(0f, false);
                for (int i = 0; i < ZapWireSegments; i++)
                {
                    var c = CreateBeam(points[i], points[i + 1], CrackleColor(i, 1f), 2f);
                    if (c != null) crackle.Add(HandleOf(c));
                    var w = CreateBeam(points[i], points[i + 1], WireColor(i, 1f), 0.6f);
                    if (w != null) wire.Add(HandleOf(w));
                }
            }

            // Returns false once the zap has fully faded
            public bool Update()
            {
                float age = Server.CurrentTime - startTime;
                if (age >= ZapLifetime)
                {
                    Destroy();
                    return false;
                }

                bool crackling = age < ZapCrackleSeconds;
                var points = PointsAt(age, crackling);

                float crackleFade = crackling ? 1f - age / ZapCrackleSeconds : 0f;
                float wireFade = age < ZapFadeStart ? 1f : 1f - (age - ZapFadeStart) / (ZapLifetime - ZapFadeStart);

                for (int i = 0; i < ZapWireSegments; i++)
                {
                    if (i < crackle.Count)
                    {
                        var c = crackle[i].Value;
                        if (c != null && c.IsValid)
                        {
                            if (crackling)
                                MoveBeam(c, points[i], points[i + 1], CrackleColor(i, crackleFade));
                            else
                                c.Remove();
                        }
                    }

                    if (i < wire.Count)
                    {
                        var w = wire[i].Value;
                        if (w != null && w.IsValid)
                            MoveBeam(w, points[i], points[i + 1], WireColor(i, wireFade));
                    }
                }

                return true;
            }

            public void Destroy()
            {
                foreach (var beam in crackle) RemoveEntity(beam);
                foreach (var beam in wire) RemoveEntity(beam);
            }

            // Sag grows like a falling rope, strongest mid-span and pinned at
            // both ends; the crackle jitters the interior points while it lasts
            private Vector3[] PointsAt(float age, bool jitter)
            {
                float sag = Math.Min(0.5f * gravity * age * age, ZapMaxSag);
                var points = new Vector3[ZapWireSegments + 1];
                for (int i = 0; i <= ZapWireSegments; i++)
                {
                    float weight = MathF.Sin(i / (float)ZapWireSegments * MathF.PI);
                    points[i] = basePoints[i] + bulge[i] - new Vector3(0f, 0f, sag * weight);
                    if (jitter && i > 0 && i < ZapWireSegments)
                        points[i] += plugin.RandomUnit() * 1.5f;
                }
                return points;
            }

            // Additive beams fade by scaling toward black
            private Color CrackleColor(int segment, float fade) =>
                Scale(Whiten(ResolveColor(color, segment / (float)ZapWireSegments), 0.35f), fade);

            private Color WireColor(int segment, float fade) =>
                Scale(ResolveColor(color, segment / (float)ZapWireSegments), 0.45f * fade);

            private static Color Scale(Color c, float f)
            {
                f = Math.Clamp(f, 0f, 1f);
                return Color.FromArgb(255, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
            }

            private static void MoveBeam(CBeam beam, Vector3 start, Vector3 end, Color color)
            {
                beam.Teleport(start, Vector3.Zero, Vector3.Zero);
                SetBeamEnd(beam, end, true);
                beam.Render = color;
                Utilities.SetStateChanged(beam, "CBaseModelEntity", "m_clrRender");
            }
        }

        private void UpdateColorZaps()
        {
            for (int i = colorZaps.Count - 1; i >= 0; i--)
                if (!colorZaps[i].Update())
                    colorZaps.RemoveAt(i);
        }

        private void ClearColorZaps()
        {
            foreach (var zap in colorZaps)
                zap.Destroy();
            colorZaps.Clear();
        }

        private static COmniLight? SpawnZapFlash(Vector3 pos, Color color)
        {
            var light = Utilities.CreateEntityByName<COmniLight>("light_omni2");
            if (light == null)
                return null;

            light.Enabled = true;
            light.DirectLight = 3; // dynamic
            light.CastShadows = 0;
            light.Color = color;
            light.Brightness = 2f;
            light.Range = 140f;
            light.InnerAngle = 180f;
            light.OuterAngle = 180f;
            light.Teleport(pos, Vector3.Zero, Vector3.Zero);
            light.DispatchSpawn();
            return light;
        }

        private void UnloadZeusColors()
        {
            ClearColorZaps();
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
