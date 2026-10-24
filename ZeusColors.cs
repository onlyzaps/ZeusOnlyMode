using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
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
        // zeuscolor: a per-player color for their Zeus zap and lightning
        // ball, picked from a center-screen panel with Reload / Inspect (see
        // Menu below). Every option is shown in its own color. Choices persist
        // per SteamID in configs/plugins/ZeusOnlyMode/zeus_colors.json.
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

        private const int ColorMenuWindow = 2;        // colors shown above / below the selection
        private const float ColorMenuIdleSeconds = 2.5f; // panel hides this long after the last press

        private sealed class ZeusColorPrefs
        {
            public string? Primary { get; set; }
            public string? Alt { get; set; }
        }

        private sealed class ColorMenuState
        {
            public int Index;
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
            AddCommand("zeuscolor", "Show the Zeus color menu (or press Reload with the Zeus)", (player, info) =>
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

        // zeuscolor (with or without ! or /) opens the menu and is swallowed
        // so other players never see it. Nothing else in chat is touched.
        private HookResult OnZeusColorChat(CCSPlayerController? player, CommandInfo info)
        {
            if (player == null || !player.IsValid || player.IsBot)
                return HookResult.Continue;

            string clean = info.GetArg(1).Trim().TrimStart('!', '/').Trim();
            if (!clean.Equals("zeuscolor", StringComparison.OrdinalIgnoreCase))
                return HookResult.Continue;

            OpenColorMenu(player);
            player.PrintToChat($" {ChatColors.Gold}Zeus Color{ChatColors.Default}: hold the Zeus and press {ChatColors.Green}Reload{ChatColors.Default} for the next color, {ChatColors.Green}Inspect{ChatColors.Default} for the previous.");
            return HookResult.Handled;
        }

        // --- Colored primary zap -------------------------------------------
        //
        // Players who picked a zap color get a colored copy of the stock zap
        // (ColorZap below) laid over the stock zap.
        // weapon_fire marks the shot; the game's bullet_impact for it gives
        // the exact end point. Players on Default see only the stock zap.

        private const float StockZeusRange = 190f;
        private readonly HashSet<int> pendingZaps = new();

        private HookResult OnZapFired(EventWeaponFire ev, GameEventInfo info)
        {
            var player = ev.Userid;
            if (player == null || !player.IsValid || !ev.Weapon.Contains("taser"))
                return HookResult.Continue;

            if (GetZeusColor(player) == null)
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
            var color = GetZeusColor(player);
            var pawn = player.PlayerPawn.Value;
            if (color == null || pawn == null || pawn.AbsOrigin == null)
                return;

            var eye = ToVec3(pawn.AbsOrigin) + new Vector3(0, 0, pawn.ViewOffset.Z);
            AngleVectors(new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0f), out var forward, out var right, out var up);
            var muzzle = eye + forward * 16f + right * (pawn.LeftHanded ? -5f : 5f) - up * 6f;

            // Same two wires the stock zap strings (weapon_tracers_taser_wire1a/b),
            // each falling under its own gravity until it reaches the floor
            float floorZ = Math.Min(pawn.AbsOrigin.Z, end.Z) + 1f;
            colorZaps.Add(new ColorZap(this, muzzle, end, color, 350f, floorZ));
            colorZaps.Add(new ColorZap(this, muzzle, end, color, 500f, floorZ));
        }

        // --- Colored copy of the stock zap ----------------------------------
        //
        // Rebuilt from the stock effect (weapon_tracers_taser). Its two probe
        // wires (wire1a/1b) are invisible carriers: 15 points strung from the
        // muzzle to the probe, each falling under its own gravity (350 / 500)
        // with light drag, held still near both ends for the first moments
        // (DampenToCP: 25 unit range, faded out between 0.25s and 0.5s),
        // pinned within 8 units of either end, and stopped by the world.
        // What you actually see is the crackle rope riding them (wire2):
        // it lives 0.4s, is thickest mid-span
        // and thin at both ends, thins to 35% over its life, fades over its
        // last 0.1s, and is smoothly tessellated. The colors are baked into
        // the particle files, so the same rope is drawn here out of beams in
        // the player's color: a smooth spline through the carrier points,
        // each beam tapering into the next, with a per-segment flicker in
        // place of the crackle texture.

        private const int ZapWirePoints = 15;
        private const int ZapSubdivisions = 2;          // spline samples between carrier points
        private const float ZapRopeDelay = 0f;
        private const float ZapRopeLife = 0.4f;
        private const float ZapRopeFade = 0.1f;
        private const float ZapRopeEndScale = 0.35f;
        private const float ZapRopePeakWidth = 1.2f;
        private const float ZapDrag = 0.05f;
        private const float ZapDampenRange = 25f;
        private const float ZapDampenScale = 0.025f;
        private const float ZapDampenFadeStart = 0.25f;
        private const float ZapDampenFadeEnd = 0.5f;
        private const float ZapLockRange = 8f;

        private readonly List<ColorZap> colorZaps = new();

        private sealed class ColorZap
        {
            private readonly ZeusOnlyPlugin plugin;
            private readonly Vector3 start;
            private readonly Vector3 end;
            private readonly Vector3[] points = new Vector3[ZapWirePoints];
            private readonly Vector3[] velocities = new Vector3[ZapWirePoints];
            private readonly bool[] locked = new bool[ZapWirePoints];
            private readonly List<CHandle<CBeam>> rope = new();
            private readonly ZeusColorOption color;
            private readonly float gravity;
            private readonly float floorZ;
            private readonly float startTime;
            private float lastUpdate;

            public ColorZap(ZeusOnlyPlugin plugin, Vector3 start, Vector3 end, ZeusColorOption color, float gravity, float floorZ)
            {
                this.plugin = plugin;
                this.start = start;
                this.end = end;
                this.color = color;
                this.gravity = gravity;
                this.floorZ = floorZ;
                startTime = lastUpdate = Server.CurrentTime;

                // Sequential path with a slight bulge to one side, like the
                // wire paying out of the cartridge
                var side = Vector3.Cross(end - start, Vector3.UnitZ);
                side = side.LengthSquared() < 0.01f ? Vector3.UnitX : Vector3.Normalize(side);
                side = Vector3.Normalize(side * (plugin.rng.NextSingle() * 2f - 1f) + Vector3.UnitZ * (plugin.rng.NextSingle() * 0.5f));
                float bulge = Math.Min(Vector3.Distance(start, end) * 0.02f, 6f);

                for (int i = 0; i < ZapWirePoints; i++)
                {
                    float t = i / (float)(ZapWirePoints - 1);
                    points[i] = Vector3.Lerp(start, end, t) + side * bulge * MathF.Sin(t * MathF.PI);
                    locked[i] = Vector3.Distance(points[i], start) < ZapLockRange || Vector3.Distance(points[i], end) < ZapLockRange;
                }

                // Draw the first frame right away rather than on the next tick
                Update();
            }

            // Returns false once the rope has faded
            public bool Update()
            {
                float now = Server.CurrentTime;
                float age = now - startTime;
                if (age >= ZapRopeDelay + ZapRopeLife)
                {
                    Destroy();
                    return false;
                }

                Simulate(age, now - lastUpdate);
                lastUpdate = now;

                if (age < ZapRopeDelay)
                    return true;

                float life = (age - ZapRopeDelay) / ZapRopeLife;
                float fade = Math.Clamp((ZapRopeDelay + ZapRopeLife - age) / ZapRopeFade, 0f, 1f);
                float widthScale = 1f + (ZapRopeEndScale - 1f) * life;
                var samples = SampleSpline();

                if (rope.Count == 0)
                {
                    for (int i = 0; i < samples.Length - 1; i++)
                    {
                        var beam = CreateBeam(samples[i], samples[i + 1], color.Color, 1f);
                        if (beam != null) rope.Add(HandleOf(beam));
                    }
                }

                for (int i = 0; i < rope.Count && i < samples.Length - 1; i++)
                {
                    var beam = rope[i].Value;
                    if (beam == null || !beam.IsValid) continue;

                    float u0 = i / (float)(samples.Length - 1);
                    float u1 = (i + 1) / (float)(samples.Length - 1);
                    float flicker = 0.7f + plugin.rng.NextSingle() * 0.3f;

                    beam.Teleport(samples[i], Vector3.Zero, Vector3.Zero);
                    SetBeamEnd(beam, samples[i + 1], true);
                    beam.Width = RopeWidth(u0) * widthScale;
                    beam.EndWidth = RopeWidth(u1) * widthScale;
                    beam.Render = Scale(Whiten(color.Color, 0.25f), fade * flicker);
                    Utilities.SetStateChanged(beam, "CBeam", "m_fWidth");
                    Utilities.SetStateChanged(beam, "CBeam", "m_fEndWidth");
                    Utilities.SetStateChanged(beam, "CBaseModelEntity", "m_clrRender");
                }

                return true;
            }

            public void Destroy()
            {
                foreach (var beam in rope) RemoveEntity(beam);
            }

            // Carrier wire physics, per the stock operators
            private void Simulate(float age, float dt)
            {
                if (dt <= 0f)
                    return;

                float dampen = age < ZapDampenFadeStart ? 1f
                    : age < ZapDampenFadeEnd ? 1f - (age - ZapDampenFadeStart) / (ZapDampenFadeEnd - ZapDampenFadeStart)
                    : 0f;

                for (int i = 0; i < ZapWirePoints; i++)
                {
                    if (locked[i])
                        continue;

                    velocities[i].Z -= gravity * dt;
                    velocities[i] *= Math.Max(0f, 1f - ZapDrag * dt * 30f);

                    float nearEnd = Math.Min(Vector3.Distance(points[i], start), Vector3.Distance(points[i], end));
                    float damped = ZapDampenScale + (1f - ZapDampenScale) * Math.Clamp(nearEnd / ZapDampenRange, 0f, 1f);
                    float factor = 1f + (damped - 1f) * dampen;

                    points[i] += velocities[i] * dt * factor;
                    if (points[i].Z < floorZ)
                    {
                        points[i].Z = floorZ;
                        velocities[i] = Vector3.Zero;
                    }
                }
            }

            // Catmull-Rom through the carrier points, so the rope is a smooth curve
            private Vector3[] SampleSpline()
            {
                var samples = new Vector3[(ZapWirePoints - 1) * ZapSubdivisions + 1];
                int n = 0;
                for (int i = 0; i < ZapWirePoints - 1; i++)
                {
                    var p0 = points[Math.Max(i - 1, 0)];
                    var p1 = points[i];
                    var p2 = points[i + 1];
                    var p3 = points[Math.Min(i + 2, ZapWirePoints - 1)];
                    for (int s = 0; s < ZapSubdivisions; s++)
                    {
                        float t = s / (float)ZapSubdivisions;
                        float t2 = t * t, t3 = t2 * t;
                        samples[n++] = 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
                    }
                }
                samples[n] = points[ZapWirePoints - 1];
                return samples;
            }

            // Thickest mid-span, thin at the gun and at the probe
            private static float RopeWidth(float u) =>
                ZapRopePeakWidth * (0.25f + 0.75f * MathF.Sin(u * MathF.PI));
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

        // One color drives both the zap and the lightning ball.
        // null = the player never picked one (or picked Default).
        private ZeusColorOption? GetZeusColor(CCSPlayerController? player)
        {
            if (player == null || !player.IsValid || player.IsBot)
                return null;

            if (!zeusColorPrefs.TryGetValue(player.SteamID, out var prefs))
                return null;

            // Older saves may hold separate zap/ball picks; the zap pick wins
            var option = FindColorOption(prefs.Primary ?? prefs.Alt);
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
        //
        // Driven entirely by keys the Zeus doesn't use, so it never stops
        // anyone moving or shooting: with the Zeus in hand, Reload opens the
        // menu and steps to the next color, Inspect (F) steps back. Each step
        // is saved immediately, and the panel hides itself a few seconds
        // after the last press. It never reads chat numbers or clears the
        // center panel, so other plugins' menus keep working as they do.

        private void OnZeusColorButtons(CCSPlayerController player, PlayerButtons pressed)
        {
            bool reload = (pressed & PlayerButtons.Reload) != 0;
            bool inspect = (pressed & PlayerButtons.Inspect) != 0;
            if ((!reload && !inspect) || player == null || !player.IsValid || player.IsBot)
                return;

            var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
            if (weapon == null || !weapon.IsValid || weapon.DesignerName != "weapon_taser")
                return;

            if (!colorMenus.TryGetValue(player.Slot, out var menu))
            {
                // Inspect alone just inspects; only Reload opens the menu
                if (reload)
                    OpenColorMenu(player);
                return;
            }

            StepColor(player, menu, reload ? 1 : -1);
        }

        private void OpenColorMenu(CCSPlayerController player)
        {
            var current = GetZeusColor(player)?.Key ?? "default";
            var menu = new ColorMenuState
            {
                Index = Math.Max(0, Array.FindIndex(ZeusColorOptions, o => o.Key == current)),
                ExpireAt = Server.CurrentTime + ColorMenuIdleSeconds
            };
            colorMenus[player.Slot] = menu;
            menu.Html = BuildColorMenuHtml(menu);
        }

        private void StepColor(CCSPlayerController player, ColorMenuState menu, int step)
        {
            int count = ZeusColorOptions.Length;
            menu.Index = ((menu.Index + step) % count + count) % count;
            menu.ExpireAt = Server.CurrentTime + ColorMenuIdleSeconds;
            menu.Html = BuildColorMenuHtml(menu);
            ApplyColorChoice(player, ZeusColorOptions[menu.Index]);
        }

        private void ApplyColorChoice(CCSPlayerController player, ZeusColorOption option)
        {
            if (!zeusColorPrefs.TryGetValue(player.SteamID, out var prefs))
                zeusColorPrefs[player.SteamID] = prefs = new ZeusColorPrefs();

            prefs.Primary = option.Key;
            prefs.Alt = option.Key;
            SaveZeusColors();
        }

        // A window of colors around the selected one, each row in its own
        // color, the selected row bold and marked
        private static string BuildColorMenuHtml(ColorMenuState menu)
        {
            var sb = new StringBuilder();
            sb.Append("<font color='#FFD700'><b>Zeus Color</b></font>");

            int count = ZeusColorOptions.Length;
            for (int offset = -ColorMenuWindow; offset <= ColorMenuWindow; offset++)
            {
                var option = ZeusColorOptions[((menu.Index + offset) % count + count) % count];
                string hex = Hex(option.Color);
                if (offset == 0)
                    sb.Append($"<br><font color='{hex}'><b>&gt; {option.Name} &lt;</b></font>");
                else
                    sb.Append($"<br><font color='{hex}'>{option.Name}</font>");
            }

            sb.Append("<br><font color='#9E9E9E'>Reload: Next | Inspect: Previous</font>");
            return sb.ToString();
        }

        // Fed every tick while open with a 1s display time; when the menu
        // hides we simply stop feeding it and let it fade, instead of
        // clearing the center panel another plugin might be using
        private void OnColorMenuTick()
        {
            if (colorMenus.Count == 0)
                return;

            float now = Server.CurrentTime;
            foreach (var slot in colorMenus.Keys.ToList())
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player == null || !player.IsValid || now >= colorMenus[slot].ExpireAt)
                {
                    colorMenus.Remove(slot);
                    continue;
                }

                try { player.PrintToCenterHtml(colorMenus[slot].Html, 1); }
                catch { /* never let a render error hit the tick */ }
            }
        }
    }
}
