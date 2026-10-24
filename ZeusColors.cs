using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.UserMessages;
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
        // a center-screen panel fed every tick plus a chat list, picked by
        // chatting a number (hidden), 8/9 to page up/down, 0 or "cancel" to
        // close. Every option is shown in its own color. Choices persist per
        // SteamID in configs/plugins/ZeusOnlyMode/zeus_colors.json.
        // ------------------------------------------------------------------

        // Chat is the closest CS2 chat color (chat has fewer colors than the
        // panel: Orange and Gold share one code)
        private sealed record ZeusColorOption(string Key, string Name, Color Color, char Chat);

        private static readonly ZeusColorOption[] ZeusColorOptions =
        {
            new("red", "Red", Color.FromArgb(255, 40, 40), ChatColors.Red),
            new("orange", "Orange", Color.FromArgb(255, 135, 25), ChatColors.Orange),
            new("yellow", "Yellow", Color.FromArgb(255, 240, 60), ChatColors.Yellow),
            new("green", "Green", Color.FromArgb(50, 220, 70), ChatColors.Green),
            new("default", "Blue (Default)", Color.FromArgb(90, 160, 255), ChatColors.Blue),
            new("purple", "Purple", Color.FromArgb(160, 60, 235), ChatColors.Purple),
            new("pink", "Pink", Color.FromArgb(255, 120, 200), ChatColors.LightRed),
            new("gold", "Gold", Color.FromArgb(255, 200, 40), ChatColors.Gold),
            new("silver", "Silver", Color.FromArgb(195, 205, 220), ChatColors.Silver),
        };

        private const int ColorsPerPage = 5;
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

            int fireBulletsId = UserMessage.FindIdByName("CMsgTEFireBullets");
            if (fireBulletsId > 0)
                HookUserMessage(fireBulletsId, OnFireBullets, HookMode.Pre);
            else
                Logger.LogWarning("CMsgTEFireBullets not found; the stock zap can't be hidden for colored zaps.");

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
        // Players who picked a zap color get a colored copy of the stock zap
        // (ColorZap below), and the stock one is hidden from everyone else.
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

        // Hide the stock zap for colored shooters. Other players draw a shot's
        // tracer, muzzle flash and sound from the server's FireBullets message,
        // so withholding it for a colored zap leaves only the colored copy;
        // the shot sound is replayed to the same players so it isn't silent.
        // The shooter's own client predicts its zap locally, which no server
        // can suppress, so the shooter still sees the stock zap themselves.
        private const uint TaserItemDefIndex = 31;

        private HookResult OnFireBullets(UserMessage message)
        {
            if (!message.HasField("item_def_index") || message.ReadUInt("item_def_index") != TaserItemDefIndex)
                return HookResult.Continue;

            var shooter = message.HasField("player") ? FindPlayerByHandle(message.ReadUInt("player")) : null;
            if (shooter == null || GetZeusColor(shooter, alt: false) == null)
                return HookResult.Continue;

            var pawn = shooter.PlayerPawn.Value;
            if (pawn != null && pawn.IsValid)
            {
                var listeners = new RecipientFilter();
                foreach (var p in message.Recipients)
                    if (p != null && p.IsValid && p.Slot != shooter.Slot)
                        listeners.Add(p);

                if (listeners.Count > 0)
                    pawn.EmitSound("Weapon_Taser.Single", listeners);
            }

            return HookResult.Stop;
        }

        // The message names the shooter by entity handle; accept either the
        // pawn's or the controller's, by full handle or by entity index
        private static CCSPlayerController? FindPlayerByHandle(uint handle)
        {
            uint index = handle & 0x7FFF;
            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid)
                    continue;

                var pawn = player.PlayerPawn.Value;
                if (player.EntityHandle.Raw == handle || player.PlayerPawn.Raw == handle
                    || player.Index == index || (pawn != null && pawn.Index == index))
                    return player;
            }
            return null;
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

        private const int ZapWireSegments = 16;
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

                // One smooth bow per wire (like the rope's path bulge), never a
                // per-point kink, so the wire reads as a soft curve
                var span = end - start;
                var side = Vector3.Cross(span, Vector3.UnitZ);
                if (side.LengthSquared() < 0.01f) side = Vector3.UnitX;
                side = Vector3.Normalize(side) * (plugin.rng.NextSingle() * 2f - 1f) + Vector3.UnitZ * (plugin.rng.NextSingle() * 0.6f);
                side = Vector3.Normalize(side) * 6f;

                basePoints = new Vector3[ZapWireSegments + 1];
                bulge = new Vector3[ZapWireSegments + 1];
                for (int i = 0; i <= ZapWireSegments; i++)
                {
                    float t = i / (float)ZapWireSegments;
                    basePoints[i] = Vector3.Lerp(start, end, t);
                    bulge[i] = side * MathF.Sin(t * MathF.PI);
                }

                var points = PointsAt(0f);
                for (int i = 0; i < ZapWireSegments; i++)
                {
                    var c = CreateBeam(points[i], points[i + 1], CrackleColor(i, 1f), 1f);
                    if (c != null) crackle.Add(HandleOf(c));
                    var w = CreateBeam(points[i], points[i + 1], WireColor(i, 1f), 0.3f);
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
                var points = PointsAt(age);

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
            // both ends, so the wire stays a smooth curve as it drops
            private Vector3[] PointsAt(float age)
            {
                float sag = Math.Min(0.5f * gravity * age * age, ZapMaxSag);
                var points = new Vector3[ZapWireSegments + 1];
                for (int i = 0; i <= ZapWireSegments; i++)
                {
                    float weight = MathF.Sin(i / (float)ZapWireSegments * MathF.PI);
                    points[i] = basePoints[i] + bulge[i] - new Vector3(0f, 0f, sag * weight);
                }
                return points;
            }

            // Additive beams fade by scaling toward black
            private Color CrackleColor(int segment, float fade) => Scale(Whiten(color.Color, 0.35f), fade);

            private Color WireColor(int segment, float fade) => Scale(color.Color, 0.45f * fade);

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

        private static Color Whiten(Color c, float amount) => Color.FromArgb(255,
            (int)(c.R + (255 - c.R) * amount),
            (int)(c.G + (255 - c.G) * amount),
            (int)(c.B + (255 - c.B) * amount));

        // Additive beams fade (and darken) by scaling toward black
        private static Color Scale(Color c, float f)
        {
            f = Math.Clamp(f, 0f, 1f);
            return Color.FromArgb(255, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }

        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        // --- Menu ----------------------------------------------------------

        private void OpenColorMenu(CCSPlayerController player)
        {
            colorMenus[player.Slot] = new ColorMenuState { ExpireAt = Server.CurrentTime + ColorMenuIdleSeconds };
            RefreshColorMenu(player);
            player.PrintToChat($" {ChatColors.Gold}Zeus Color{ChatColors.Default}: chat {ChatColors.Green}1{ChatColors.Default} Zap, {ChatColors.Green}2{ChatColors.Default} Lightning Ball, {ChatColors.Green}3{ChatColors.Default} Both, or {ChatColors.Green}0{ChatColors.Default} to close.");
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
                PrintColorPageToChat(player, menu);
                return true;
            }

            switch (choice)
            {
                case 0:
                    CloseColorMenu(player);
                    return true;
                case 8: // page up
                    menu.Page = Math.Max(0, menu.Page - 1);
                    RefreshColorMenu(player);
                    PrintColorPageToChat(player, menu);
                    return true;
                case 9: // page down
                    menu.Page = Math.Min(ColorPageCount - 1, menu.Page + 1);
                    RefreshColorMenu(player);
                    PrintColorPageToChat(player, menu);
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
            player.PrintToChat($" {ChatColors.Default}Your {what} color is now {option.Chat}{option.Name}{ChatColors.Default}.");
        }

        private static int ColorPageCount => (ZeusColorOptions.Length + ColorsPerPage - 1) / ColorsPerPage;

        // The chat side of the menu, like SimpleVote's nomination list
        private static void PrintColorPageToChat(CCSPlayerController player, ColorMenuState menu)
        {
            player.PrintToChat($" {ChatColors.Default}Page {menu.Page + 1}/{ColorPageCount}. Type a number to choose:");

            int start = menu.Page * ColorsPerPage;
            for (int i = start; i < Math.Min(start + ColorsPerPage, ZeusColorOptions.Length); i++)
            {
                var option = ZeusColorOptions[i];
                player.PrintToChat($" {option.Chat}[{i - start + 1}] {option.Name}");
            }

            player.PrintToChat($" {ChatColors.Grey}[8] Page Up  [9] Page Down  [0] Close");
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

            string title = menu.Target switch
            {
                ColorMenuTarget.Primary => "Zap Color",
                ColorMenuTarget.Alt => "Lightning Ball Color",
                _ => "Zap + Ball Color"
            };
            sb.Append($"<font color='#FFD700'><b>{title} - page {menu.Page + 1}/{ColorPageCount}</b></font>");

            // Each whole row, number included, is drawn in its own color
            int start = menu.Page * ColorsPerPage;
            for (int i = start; i < Math.Min(start + ColorsPerPage, ZeusColorOptions.Length); i++)
            {
                var option = ZeusColorOptions[i];
                sb.Append($"<br><font color='{Hex(option.Color)}'>{i - start + 1}: {option.Name}</font>");
            }

            sb.Append("<br><font color='#9E9E9E'>8: Page Up   9: Page Down   0: Close</font>");
            return sb.ToString();
        }

        private static string ColoredName(ZeusColorOption option) =>
            $"<font color='{Hex(option.Color)}'>{option.Name}</font>";

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
