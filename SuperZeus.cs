using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace ZeusOnlyMode
{
    public partial class ZeusOnlyPlugin
    {
        private const string SuperZeusPermission = "@css/generic";

        // The ball rides a grenade, and the engine deletes a grenade whose speed
        // along any one world axis passes a limit (somewhere between 1800 and
        // 2100; at 2100 balls fired north/south/east/west vanished on launch
        // while diagonal ones flew). 1900 stays under it in every direction.
        private const float BallSpeed = 1900f;
        private const float ReflectBallSpeed = 1600f;   // Reflect Zeus balls, a little slower to follow their bounces

        // Volume for every sound the plugin plays (1 = the game's own level).
        // Read from settings.json on every load, so editing it and reloading
        // the plugin applies the new value.
        private const float DefaultSoundVolume = 0.4f;
        private float SoundVolume = DefaultSoundVolume;
        private const float BallLifetime = 2.5f;
        private const float BallCooldown = 1.0f;
        private const float HyperBallGap = 0.1f;   // keeps a full Hyper Zeus pool from firing on one tick
        private const float BallOrbRadius = 12f;     // hit size: the visible center orb
        private const float PlayerHullRadius = 16f;  // half of the 32-unit player hull
        private const float PlayerHullHeight = 72f;
        private const float BallTickLookahead = 1f / 64f; // one server tick of travel

        // How hard a lightning ball kill flings the enemy's ragdoll
        // (horizontal speed along the ball's path, plus 30% of it upward)
        private const float RagdollLaunchSpeed = 800f;

        // Launch impulses are this many times the launch velocity: the body gets
        // the full push, the dropped gun half of it
        private const float RagdollForceScale = 100f;
        private const float DroppedGunForceScale = 50f;

        // Damage per lightning ball hit (a little over a third of full health);
        // Hyper Zeus fires much faster, so each of its balls hits for less
        private const int BallDamage = 34;
        private const int HyperBallDamage = 20;

        // Hyper Zeus balls are drawn (and hit) at this fraction of a Super Zeus ball's size
        private const float HyperBallSize = 0.6f;

        // All effects below exist in CS2's pak01 and only rely on control point 0,
        // so they render correctly from a plain info_particle_system.
        //
        // Avoid weapon_taser_flash (and weapon_muzzle_flash_taser, which
        // contains it): it's a viewmodel sprite drawn MOD2X with 10x overbright
        // and no z-buffering, so out in the world it renders as a white box.
        private const string FxBallGlow = "particles/generic_fx/fx_electricspark_glow.vpcf";        // electric arc sprites + light
        private const string FxBallSparks = "particles/weapons/cs_weapon_fx/weapon_taser_sparks.vpcf";
        private const string FxMuzzleGlow = "particles/weapons/cs_weapon_fx/weapon_taser_glow.vpcf";
        private const string FxMuzzleSmokePuff = "particles/unified_weapon_fx/uweapon_muzflsh_gen_large_smoke.vpcf";
        private const string FxMuzzleSmokeTrail = "particles/weapons/cs_weapon_fx/weapon_muzzle_smoke_long.vpcf";
        private const string FxImpactFlare = "particles/generic_fx/fx_electricspark_flare.vpcf";
        private const string FxImpactSparks = "particles/explosions_fx/bumpmine_detonate_sparks.vpcf";
        private const string FxImpactRing = "particles/explosions_fx/bumpmine_detonate_ring.vpcf";
        private const string FxImpactArcs = "particles/generic_fx/fx_electric_arc_spark.vpcf";
        private const string FxImpactDebris = "particles/impact_fx/impact_electronic.vpcf";

        // Burn-out and scorch effects (all stock, control point 0 only)
        private const string FxScorch = "particles/burning_fx/barrel_fire_scortch.vpcf";       // projected triplanar scorch, held while the entity lives
        private const string FxBurnFire = "particles/burning_fx/env_fire_small.vpcf";          // small fire with embers
        private const string FxBurnSmoke = "particles/burning_fx/barrel_burning_engine_smoke_dark.vpcf";

        private const string BallCarrierModel = "weapons/models/grenade/decoy/weapon_decoy.vmdl";

        private static readonly string[] SuperZeusResources =
        {
            BallCarrierModel,
            FxBallGlow, FxBallSparks, FxMuzzleGlow, FxMuzzleSmokePuff, FxMuzzleSmokeTrail,
            FxImpactFlare, FxImpactSparks, FxImpactRing, FxImpactArcs, FxImpactDebris,
            FxScorch, FxBurnFire, FxBurnSmoke
        };

        private static readonly Color ArcCoreColor = Color.FromArgb(255, 210, 240, 255);
        private static readonly Color ArcGlowColor = Color.FromArgb(255, 70, 150, 255);

        // 0 = off, 1 = lightning balls only, 2 = every fire mode (each player
        // picks theirs with Inspect, see FireModes.cs)
        private int superZeusLevel = 0;
        private bool superZeusEnabled => superZeusLevel > 0;
        private bool AllFireModes => superZeusLevel >= 2;
        private readonly Random rng = new();
        private readonly List<LightningBall> lightningBalls = new();
        private readonly Dictionary<int, float> nextBallTime = new();
        private readonly List<PendingRecharge> pendingRecharges = new();
        private ConVar? taserRechargeCvar;

        // CS2's stock mp_taser_recharge_time, used if the cvar can't be read
        private const float DefaultTaserRechargeSeconds = 30f;

        private sealed class PendingRecharge
        {
            public required CHandle<CWeaponTaser> Zeus;
            public required float ReadyAt;
        }

        // One crackling arc: jagged points stored relative to the ball so the
        // arc travels with it, each segment drawn as a dim wide glow beam with
        // a bright thin beam on top
        private sealed class Tendril
        {
            public readonly List<CHandle<CBeam>> Glow = new();
            public readonly List<CHandle<CBeam>> Line = new();
            public Vector3[] Offsets = Array.Empty<Vector3>();
        }

        private sealed class LightningBall
        {
            public required CHandle<CDecoyProjectile> Projectile;
            public CHandle<CBeam>? Tail;
            public readonly List<CHandle<CBeam>> Glow = new();      // soft glow wedges
            public readonly List<CHandle<CBeam>> Core = new();      // small hot center
            public readonly List<Tendril> Tendrils = new();          // electric arcs
            public readonly List<CHandle<CBeam>> Streaks = new();   // lines along the direction of travel
            public required int OwnerSlot;
            public required byte OwnerTeam;
            public required ZeusColorOption? Color; // null = stock electric blue
            public required Vector3 Velocity;
            public required Vector3 LastPosition;
            public required float ExpireTime;
            public required int Damage;
            public required float Size;   // 1 = a Super Zeus ball
            public int Age;
            public bool Reflect;          // fired while Reflect Zeus was on
            public int SeenBounces;       // the carrier's bounce count already handled
            public int Reflections;       // times this ball has bounced (Reflect Zeus)
        }

        // Super Zeus stays on across plugin reloads and map changes until an
        // admin turns it off: the state lives next to the plugin config in
        // configs/plugins/ZeusOnlyMode/superzeus_state.json
        private string superZeusStatePath = "";

        private void LoadSuperZeusState()
        {
            superZeusStatePath = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "ZeusOnlyMode", "superzeus_state.json"));
            try
            {
                if (!File.Exists(superZeusStatePath))
                    return;

                using var doc = JsonDocument.Parse(File.ReadAllText(superZeusStatePath));
                var root = doc.RootElement;
                superZeusLevel = root.TryGetProperty("level", out var level) && level.TryGetInt32(out var saved)
                    ? Math.Clamp(saved, 0, 2)
                    : root.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean() ? 1 : 0;
                Logger.LogInformation("[SuperZeus] Restored saved state: Super Zeus level {Level}", superZeusLevel);
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not read {Path}: {Message}", superZeusStatePath, ex.Message);
            }
        }

        private void SaveSuperZeusState()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(superZeusStatePath)!);
                File.WriteAllText(superZeusStatePath, JsonSerializer.Serialize(new { enabled = superZeusEnabled, level = superZeusLevel }));
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not save {Path}: {Message}", superZeusStatePath, ex.Message);
            }
        }

        // configs/plugins/ZeusOnlyMode/settings.json, created with defaults if missing
        private void LoadSettings()
        {
            string path = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "ZeusOnlyMode", "settings.json"));
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(new { SoundVolume = DefaultSoundVolume }, new JsonSerializerOptions { WriteIndented = true }));
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                SoundVolume = doc.RootElement.TryGetProperty("SoundVolume", out var volume) && volume.TryGetSingle(out var value)
                    ? Math.Clamp(value, 0f, 1f)
                    : DefaultSoundVolume;
            }
            catch (Exception ex)
            {
                SoundVolume = DefaultSoundVolume;
                Logger.LogError("Could not read {Path}: {Message}", path, ex.Message);
            }

            Logger.LogInformation("[SuperZeus] Sound volume {Volume} (settings.json)", SoundVolume);
        }

        // --- Sound volume -------------------------------------------------------
        //
        // CS2 ignores the volume EmitSound is given; a sound event's level is one
        // of its parameters ("public.volume"). Parameters travel in packed_params:
        // per parameter, the name's MurmurHash2 (lowercase, seed 0x31415926), a
        // type tag (0x08 = float), a 16-bit length and the value, little-endian.
        //
        // The volume is written into the start message itself (GE_SosStartSoundEvent)
        // so the sound begins at that level, and set again afterwards with
        // GE_SosSetSoundEventParams naming the sound by its guid.

        private const int SosStartSoundEventId = 208;
        private const int SosSetSoundEventParamsId = 210;
        private const uint VolumeParamHash = 0xBD6054E9;   // "public.volume"
        private (string Sound, float Volume)? emittingSound;   // set while PlaySound emits
        private readonly Dictionary<uint, (string Sound, float Volume)> recentSounds = new();
        private int soundLogsLeft;

        // volume: null = SoundVolume
        private void PlaySound(CBaseEntity? source, string sound, float? volume = null)
        {
            if (source == null || !source.IsValid)
                return;

            float level = volume ?? SoundVolume;
            var recipients = new RecipientFilter();
            recipients.AddAllPlayers();
            uint guid;
            emittingSound = (sound, level);
            try { guid = source.EmitSound(sound, recipients, level); }
            finally { emittingSound = null; }
            if (guid == 0 || level >= 1f)
                return;

            // In case the start message goes out after EmitSound returns
            recentSounds[guid] = (sound, level);
            Server.NextFrame(() =>
            {
                recentSounds.Remove(guid);
                SendSoundVolume(guid, recipients, sound, level);
            });
            SendSoundVolume(guid, recipients, sound, level);
        }

        private HookResult OnSosStartSoundEvent(UserMessage message)
        {
            try
            {
                uint guid = message.ReadUInt("soundevent_guid");
                bool during = emittingSound != null;
                (string Sound, float Volume) played;
                if (emittingSound is { } current)
                    played = current;
                else if (!recentSounds.TryGetValue(guid, out played))
                    return HookResult.Continue;

                string sound = played.Sound;
                if (played.Volume >= 1f)
                    return HookResult.Continue;

                byte[] before = message.ReadBytes("packed_params");
                byte[] after = WithVolume(before, played.Volume);
                message.SetBytes("packed_params", after);

                if (soundLogsLeft > 0)
                {
                    soundLogsLeft--;
                    Logger.LogInformation("[SuperZeus] {Sound} start (guid {Guid}, {When}): params {Before} -> {After}",
                        sound, guid, during ? "during EmitSound" : "after EmitSound",
                        before.Length == 0 ? "(none)" : Convert.ToHexString(before), Convert.ToHexString(after));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[SuperZeus] Setting a sound's start volume failed: {Message}", ex.Message);
            }
            return HookResult.Continue;
        }

        // The packed parameters with any volume entry replaced by ours
        private static byte[] WithVolume(byte[] packed, float volume)
        {
            var result = new List<byte>(packed.Length + 11);
            int i = 0;
            while (i + 7 <= packed.Length)
            {
                uint hash = BitConverter.ToUInt32(packed, i);
                int size = 7 + BitConverter.ToUInt16(packed, i + 5);
                if (i + size > packed.Length)
                    break;
                if (hash != VolumeParamHash)
                    result.AddRange(packed.AsSpan(i, size).ToArray());
                i += size;
            }
            if (i != packed.Length)   // not the layout we expect: keep it whole
            {
                result.Clear();
                result.AddRange(packed);
            }

            result.AddRange(VolumeParam(volume));
            return result.ToArray();
        }

        private static byte[] VolumeParam(float volume)
        {
            var bytes = new byte[11];
            BitConverter.GetBytes(VolumeParamHash).CopyTo(bytes, 0);
            bytes[4] = 0x08;   // float
            bytes[5] = 4;      // length 4
            BitConverter.GetBytes(volume).CopyTo(bytes, 7);
            return bytes;
        }

        private void SendSoundVolume(uint guid, RecipientFilter recipients, string sound, float volume)
        {
            try
            {
                using var message = UserMessage.FromId(SosSetSoundEventParamsId);
                message.SetUInt("soundevent_guid", guid);
                message.SetBytes("packed_params", VolumeParam(volume));
                message.Send(recipients);
            }
            catch (Exception ex)
            {
                Logger.LogError("[SuperZeus] Setting the volume of {Sound} failed: {Message}", sound, ex.Message);
            }
        }

        private void LoadSuperZeus()
        {
            LoadSettings();
            LoadSuperZeusState();
            LoadFireModes();
            LoadCameras();
            soundLogsLeft = 6;
            HookUserMessage(SosStartSoundEventId, OnSosStartSoundEvent, HookMode.Pre);

            // Both spellings work from console/RCON. CS# also forwards "!superzeus"
            // in chat to css_superzeus, so the chat hook below and this command
            // can both fire for one chat line; the debounce in ToggleSuperZeus
            // makes that a single toggle.
            AddCommand("superzeus", "Toggle Super Zeus (alt-fire lightning balls)", ConCommand_SuperZeus);


            // Handle the chat form ourselves so the message is swallowed and never shown to players
            // One distinct delegate per command. CS# keys its listener table by
            // the delegate, so registering the same method for "say" and
            // "say_team" overwrote the first entry and left that hook alive
            // after unload: a ghost copy of the plugin kept answering chat.
            AddCommandListener("say", (player, info) => OnSuperZeusChat(player, info));
            AddCommandListener("say_team", (player, info) => OnSuperZeusChat(player, info));

            RegisterListener<Listeners.OnTick>(PollButtons);
            RegisterListener<Listeners.OnEntityTakeDamagePre>(OnBallDamagePre);
            RegisterEventHandler<EventWeaponFire>(OnZeusZapForBalls);
            RegisterEventHandler<EventPlayerSpawn>(OnSpawnRestoreBody);
            RegisterListener<Listeners.OnTick>(OnSuperZeusTick);
            RegisterListener<Listeners.OnClientDisconnect>(slot =>
            {
                lastButtons.Remove(slot);
                noFallDamageUntil.Remove(slot);
            });
            RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
            {
                foreach (var resource in SuperZeusResources)
                    manifest.AddResource(resource);
            });
            RegisterListener<Listeners.OnMapStart>(_ =>
            {
                lightningBalls.Clear();
                ballSparks.Clear();
                pendingRecharges.Clear();
                ballEnergy.Clear();
                grapples.Clear();
                wallCameras.Clear();
                cameraUsedThisLife.Clear();
                noFallDamageUntil.Clear();
                bodyZaps.Clear();
                bodyZapsByPawn.Clear();
                bodyFades.Clear();
                scorches.Clear();
                hiddenPawns.Clear();
                pendingBallForces.Clear();
                nextBallTime.Clear();
                lastButtons.Clear();
                loggedOnce.Clear();
                nextHeartbeat = 0f;
                taserRechargeCvar = null;
                teammatesAreEnemiesCvar = null;
            });
            RegisterEventHandler<EventRoundStart>((ev, info) =>
            {
                ClearLightningBalls();
                ClearGrapples();
                ClearCameras();
                return HookResult.Continue;
            });
        }

        private void UnloadSuperZeus()
        {
            UnhookUserMessage(SosStartSoundEventId, OnSosStartSoundEvent, HookMode.Pre);
            ClearLightningBalls();
            ClearGrapples();
            ClearCameras();
        }

        // css_superzeus: registered by attribute, exactly like other plugins'
        // chat-triggered commands on this server (CS# forwards "!superzeus" here)
        [ConsoleCommand("css_superzeus", "Toggle Super Zeus (alt-fire lightning balls)")]
        public void ConCommand_SuperZeus(CCSPlayerController? player, CommandInfo info)
        {
            Logger.LogInformation("[SuperZeus] Command {Command} from {Who}", info.GetArg(0), player?.PlayerName ?? "console");
            ToggleSuperZeus(player, info.ArgCount > 1 ? info.GetArg(1) : null);
        }

        private HookResult OnSuperZeusChat(CCSPlayerController? player, CommandInfo info)
        {
            var parts = info.GetArg(1).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return HookResult.Continue;

            string word = parts[0].TrimStart('!', '/');

            if (!word.Equals("superzeus", StringComparison.OrdinalIgnoreCase))
                return HookResult.Continue;

            ToggleSuperZeus(player, parts.Length > 1 ? parts[1] : null);
            return HookResult.Handled;
        }

        // One chat line can reach the toggle twice (chat hook + CS#'s css_ dispatch)
        private DateTime lastToggle = DateTime.MinValue;

        // No argument toggles off <-> 1; "0", "1" or "2" sets the level
        private void ToggleSuperZeus(CCSPlayerController? player, string? arg)
        {
            if ((DateTime.UtcNow - lastToggle).TotalSeconds < 0.5)
                return;
            lastToggle = DateTime.UtcNow;

            if (player != null)
            {
                // Every attempt is logged with the player's admin flags, so a
                // toggle that "did nothing" can be explained from the console
                bool allowed = AdminManager.PlayerHasPermissions(player, SuperZeusPermission);
                var data = AdminManager.GetPlayerAdminData(player);
                string flags = data == null ? "no admin data" : string.Join(", ", data.Flags.SelectMany(kv => kv.Value));
                Logger.LogInformation("[SuperZeus] Toggle attempt by {Name} ({SteamId}), arg '{Arg}': {Result}; flags: {Flags}",
                    player.PlayerName, player.SteamID, arg ?? "", allowed ? "allowed" : "DENIED", flags);

                if (!allowed)
                {
                    player.PrintToChat($" {ChatColors.Red}Super Zeus:{ChatColors.Default} you need the {SuperZeusPermission} admin flag to toggle it.");
                    return;
                }
            }

            int newLevel = arg switch
            {
                "0" => 0,
                "1" => 1,
                "2" => 2,
                _ => superZeusEnabled ? 0 : 1
            };

            if (newLevel == superZeusLevel)
            {
                ReplyToAdmin(player, newLevel switch
                {
                    0 => "Super Zeus Is Already Disabled.",
                    1 => "Super Zeus Is Already Enabled.",
                    _ => "Super Zeus Is Already Enabled With All Fire Modes."
                });
                return;
            }

            superZeusLevel = newLevel;
            SaveSuperZeusState();
            Logger.LogInformation("[SuperZeus] Super Zeus set to level {Level} by {Who}", newLevel, player?.PlayerName ?? "console");

            if (newLevel == 2)
                Server.PrintToChatAll($" {ChatColors.LightBlue}SUPER ZEUS ENABLED - ALL FIRE MODES{ChatColors.Default} - Right-Click With Your Zeus To Fire, Inspect To Switch Fire Mode!");
            else if (newLevel == 1)
                Server.PrintToChatAll($" {ChatColors.LightBlue}SUPER ZEUS ENABLED{ChatColors.Default} - Right-Click With Your Zeus To Hurl A Lightning Ball!");
            else
            {
                Server.PrintToChatAll($" {ChatColors.LightBlue}Super Zeus Disabled.");
                ClearGrapples();
                ClearCameras();
            }
        }

        private static void ReplyToAdmin(CCSPlayerController? player, string message)
        {
            if (player == null)
                Server.PrintToConsole(message);
            else
                player.PrintToChat(message);
        }

        // Button presses are read straight off each pawn every tick instead
        // of through the OnPlayerButtonsChanged listener, so the alt-fire and
        // the color menu each reliably see every press
        private readonly Dictionary<int, PlayerButtons> lastButtons = new();

        private void PollButtons()
        {
            List<CCSPlayerController> players;
            try { players = Utilities.GetPlayers(); }
            catch (Exception ex)
            {
                LogOnce($"players:{ex.GetType().Name}", $"[SuperZeus] Could not list players: {ex}");
                return;
            }

            int polled = 0;
            foreach (var player in players)
            {
                // Each player is handled inside its own try/catch. An error on
                // one player (connecting, spectating, mid team-switch) used to
                // abort the whole loop and silently take right-click away from
                // every player after them.
                try
                {
                    if (player == null || !player.IsValid || player.IsBot || !player.PawnIsAlive)
                        continue;

                    // The player pawn specifically, never an observer pawn
                    var movement = player.PlayerPawn.Value?.MovementServices;
                    if (movement == null)
                        continue;

                    polled++;
                    var buttons = (PlayerButtons)movement.Buttons.ButtonStates[0];
                    lastButtons.TryGetValue(player.Slot, out var previous);
                    lastButtons[player.Slot] = buttons;

                    var pressed = buttons & ~previous;
                    if (pressed == 0)
                        continue;

                    if ((pressed & PlayerButtons.Attack2) != 0)
                        rightClicksSeen++;

                    OnSuperZeusButtons(player, pressed);
                    OnZeusColorButtons(player, pressed);
                    OnFireModeButtons(player, pressed);
                }
                catch (Exception ex)
                {
                    LogOnce($"poll:{ex.GetType().Name}:{ex.Message}",
                        $"[SuperZeus] Reading buttons for {SafeName(player)} failed: {ex}");
                }
            }

            // Heartbeat while Super Zeus is on: proves the poll is running and
            // shows whether right-clicks are reaching it at all
            if (superZeusEnabled && Server.CurrentTime >= nextHeartbeat)
            {
                nextHeartbeat = Server.CurrentTime + 15f;
                Logger.LogInformation("[SuperZeus] Poll alive: {Polled} living players polled, {Clicks} right-clicks seen so far, {Balls} balls in flight",
                    polled, rightClicksSeen, lightningBalls.Count);
            }
        }

        private int rightClicksSeen;
        private float nextHeartbeat;

        private static string SafeName(CCSPlayerController? player)
        {
            try { return player?.PlayerName ?? "?"; }
            catch { return "?"; }
        }

        // Errors that could repeat every tick are logged once per map
        private readonly HashSet<string> loggedOnce = new();

        private void LogOnce(string key, string message)
        {
            if (loggedOnce.Add(key))
                Logger.LogError("{Message}", message);
        }

        private void OnSuperZeusButtons(CCSPlayerController player, PlayerButtons pressed)
        {
            if ((pressed & PlayerButtons.Attack2) == 0)
                return;

            // Every right-click that doesn't fire logs why
            if (player == null || !player.IsValid || !player.PawnIsAlive)
            {
                Logger.LogInformation("[SuperZeus] Right-click ignored: {Name} is not alive", player?.PlayerName ?? "?");
                return;
            }

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
            {
                Logger.LogInformation("[SuperZeus] Right-click ignored: {Name} has no pawn", player.PlayerName);
                return;
            }

            // Watching a camera: right-click with anything in hand comes back
            if (IsViewingCamera(player.Slot))
            {
                StopCameraView(player.Slot);
                return;
            }

            var weapon = pawn.WeaponServices?.ActiveWeapon.Value;
            if (weapon == null || !weapon.IsValid || weapon.DesignerName != "weapon_taser")
            {
                if (superZeusEnabled)
                    Logger.LogInformation("[SuperZeus] Right-click ignored: {Name} is holding {Weapon}, not the Zeus",
                        player.PlayerName, weapon != null && weapon.IsValid ? weapon.DesignerName : "nothing");
                return;
            }

            // From here every right-click with the Zeus logs its outcome
            if (!superZeusEnabled)
            {
                Logger.LogInformation("[SuperZeus] {Name} right-clicked with the Zeus, but Super Zeus is off", player.PlayerName);
                return;
            }

            if (FireModeOf(player) == FireMode.Camera)
            {
                OnCameraClick(player, pawn, weapon.As<CCSWeaponBase>());
                return;
            }

            float now = Server.CurrentTime;
            if (nextBallTime.TryGetValue(player.Slot, out var readyAt) && now < readyAt)
                return;

            // Same energy as a normal zap: no charge, no ball
            var zeus = weapon.As<CWeaponTaser>();
            if (zeus.Clip1 <= 0)
            {
                Logger.LogInformation("[SuperZeus] {Name}'s alt-fire ignored: the Zeus is recharging", player.PlayerName);
                PlaySound(pawn, "Weapon_Taser.ChargeNotReady");
                return;
            }

            var mode = FireModeOf(player);
            if (mode == FireMode.Hyper)
            {
                float energy = BallEnergy(zeus.Index, now);
                if (energy < 1f)
                {
                    Logger.LogInformation("[SuperZeus] {Name}'s alt-fire ignored: ball energy {Energy:0.00}/{Max} (refilling)", player.PlayerName, energy, HyperBallsPerCharge);
                    PlaySound(pawn, "Weapon_Taser.ChargeNotReady");
                    return;
                }

                ballEnergy[zeus.Index] = (energy - 1f, now);
                nextBallTime[player.Slot] = now + HyperBallGap;
                Logger.LogInformation("[SuperZeus] {Name} fired a Hyper Zeus ball (energy {Energy:0.00}/{Max})", player.PlayerName, energy - 1f, HyperBallsPerCharge);
                FireLightningBall(player, pawn, zeus, HyperBallDamage, HyperBallSize, reflect: false);
                return;
            }

            nextBallTime[player.Slot] = now + BallCooldown;
            ConsumeZeusCharge(zeus);
            if (mode == FireMode.Grapple)
            {
                Logger.LogInformation("[SuperZeus] {Name} fired a grapple", player.PlayerName);
                FireGrapple(player, pawn, zeus);
                return;
            }

            Logger.LogInformation("[SuperZeus] {Name} fired a {Mode} ball", player.PlayerName, mode);
            FireLightningBall(player, pawn, zeus, BallDamage, 1f, reflect: mode == FireMode.Reflect);
        }

        // Hyper Zeus balls fire three times as fast as the normal zap: each
        // ball spends one point of a three-point pool that refills three
        // points per mp_taser_recharge_time (the zap's own refire time). A
        // normal zap empties the pool. Hyper balls don't spend the zap's
        // charge, which would cap them at the zap's rate.
        private const int HyperBallsPerCharge = 3;

        // Ball energy per Zeus (weapon index): value and when it was last updated
        private readonly Dictionary<uint, (float Value, float At)> ballEnergy = new();

        private float BallEnergy(uint zeusIndex, float now)
        {
            if (!ballEnergy.TryGetValue(zeusIndex, out var state))
                return HyperBallsPerCharge;

            float perSecond = HyperBallsPerCharge / Math.Max(TaserRechargeSeconds(), 0.05f);
            return Math.Min(HyperBallsPerCharge, state.Value + (now - state.At) * perSecond);
        }

        private HookResult OnZeusZapForBalls(EventWeaponFire ev, GameEventInfo info)
        {
            if (!ev.Weapon.Contains("taser"))
                return HookResult.Continue;

            var weapon = ev.Userid?.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
            if (weapon != null && weapon.IsValid)
                ballEnergy[weapon.Index] = (0f, Server.CurrentTime);
            return HookResult.Continue;
        }

        // Spend the charge like a primary fire does. Setting the fire time
        // drives the HUD's recharge meter, but the server-side refill a real
        // shot schedules isn't reachable from here, so the meter would stall
        // at 99% forever. We finish the recharge ourselves at the same moment
        // the meter fills (mp_taser_recharge_time after the shot).
        private void ConsumeZeusCharge(CWeaponTaser zeus)
        {
            zeus.Clip1 -= 1;
            zeus.FireTime = Server.CurrentTime;
            zeus.LastAttackTick = Server.TickCount;
            Utilities.SetStateChanged(zeus, "CBasePlayerWeapon", "m_iClip1");
            Utilities.SetStateChanged(zeus, "CWeaponTaser", "m_fFireTime");

            pendingRecharges.Add(new PendingRecharge
            {
                Zeus = HandleOf(zeus),
                ReadyAt = Server.CurrentTime + TaserRechargeSeconds()
            });
        }

        private float TaserRechargeSeconds()
        {
            taserRechargeCvar ??= ConVar.Find("mp_taser_recharge_time");
            return float.TryParse(taserRechargeCvar?.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? Math.Max(seconds, 0f)
                : DefaultTaserRechargeSeconds;
        }

        private void ProcessPendingRecharges()
        {
            float now = Server.CurrentTime;
            for (int i = pendingRecharges.Count - 1; i >= 0; i--)
            {
                var pending = pendingRecharges[i];
                var zeus = pending.Zeus.Value;

                // Gone, or already refilled (by the game or a fresh zeus)
                if (zeus == null || !zeus.IsValid || zeus.Clip1 > 0)
                {
                    pendingRecharges.RemoveAt(i);
                    continue;
                }

                if (now < pending.ReadyAt)
                    continue;

                zeus.Clip1 = Math.Max(1, zeus.VData?.MaxClip1 ?? 1);
                Utilities.SetStateChanged(zeus, "CBasePlayerWeapon", "m_iClip1");

                var owner = zeus.OwnerEntity.Value;
                if (owner != null && owner.IsValid)
                    PlaySound(owner, "Weapon_Taser.ChargeReady");

                pendingRecharges.RemoveAt(i);
            }
        }

        private void FireLightningBall(CCSPlayerController player, CCSPlayerPawn pawn, CCSWeaponBase zeus, int damage, float size, bool reflect)
        {
            var eye = ToVec3(pawn.AbsOrigin!) + new Vector3(0, 0, pawn.ViewOffset.Z);
            var angles = new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0);
            AngleVectors(angles, out var forward, out var right, out var up);

            var spawnPos = eye + forward * 24f;
            var muzzle = eye + forward * 28f + right * (pawn.LeftHanded ? -7f : 7f) - up * 7f;
            var velocity = forward * (reflect ? ReflectBallSpeed : BallSpeed);

            var ball = SpawnBall(pawn, player.Slot, pawn.TeamNum, GetZeusColor(player), spawnPos, velocity,
                damage, size, BallLifetime, reflect);
            if (ball == null)
            {
                Logger.LogError("[SuperZeus] Could not create the ball's carrier entity; no ball for {Name}", player.PlayerName);
                return;
            }

            // Muzzle: glow, sparks, a heavy puff, and a smoke wisp that trails off the shooter
            SpawnParticle(FxMuzzleGlow, muzzle, angles, 1f);
            SpawnParticle(FxImpactFlare, muzzle, angles, 1f);
            SpawnParticle(FxBallSparks, muzzle, angles, 1f);
            SpawnParticle(FxMuzzleSmokePuff, muzzle, angles, 3f);
            SpawnParticle(FxMuzzleSmokeTrail, muzzle, angles, 5f, pawn);

            PlaySound(pawn, "Weapon_Taser.Single");
            PlaySound(pawn, "Weapon_Taser.ChargeReady_Zap");

            KickZeus(zeus);
        }

        // An invisible decoy that carries a ball (or grapple tip) so the engine
        // handles world collision for us. It ignores the owner pawn, if given.
        private static CDecoyProjectile? CreateCarrier(CCSPlayerPawn? ownerPawn, byte team, Vector3 spawnPos, Vector3 velocity)
        {
            if (ownerPawn != null && !ownerPawn.IsValid)
                ownerPawn = null;

            var projectile = Utilities.CreateEntityByName<CDecoyProjectile>("decoy_projectile");
            if (projectile == null)
                return null;

            projectile.TeamNum = team;
            if (ownerPawn != null)
            {
                projectile.Thrower.Raw = ownerPawn.EntityHandle.Raw;
                projectile.OriginalThrower.Raw = ownerPawn.EntityHandle.Raw;
                projectile.OwnerEntity.Raw = ownerPawn.EntityHandle.Raw;
            }
            projectile.RenderMode = RenderMode_t.kRenderNone;
            SetDebrisCollision(projectile);
            if (ownerPawn != null) IgnoreOwnerCollision(projectile, ownerPawn);
            projectile.DispatchSpawn();
            projectile.SetModel(BallCarrierModel); // guarantees a physics hull for wall collision
            SetDebrisCollision(projectile);
            if (ownerPawn != null) IgnoreOwnerCollision(projectile, ownerPawn);
            projectile.GravityScale = 0.001f;
            projectile.Teleport(spawnPos, Vector3.Zero, velocity);
            return projectile;
        }

        // A ball and its carrier, flying from spawnPos at velocity. The owner
        // pawn (if still around) is who the carrier ignores and credits.
        private LightningBall? SpawnBall(CCSPlayerPawn? ownerPawn, int ownerSlot, byte team, ZeusColorOption? color,
            Vector3 spawnPos, Vector3 velocity, int damage, float size, float lifetime, bool reflect)
        {
            var projectile = CreateCarrier(ownerPawn, team, spawnPos, velocity);
            if (projectile == null)
                return null;

            var ball = new LightningBall
            {
                Projectile = HandleOf(projectile),
                OwnerSlot = ownerSlot,
                OwnerTeam = team,
                Color = color,
                Velocity = velocity,
                LastPosition = spawnPos,
                ExpireTime = Server.CurrentTime + lifetime,
                Damage = damage,
                Size = size,
                Reflect = reflect
            };

            // A small BFG-style ball in the player's color (stock blue on
            // Default): a soft glowing sphere, a hot core, crackling tendrils,
            // a faint tail and a few sparks. The stock particles are baked
            // white-blue and can't be recolored, so the ball is built from beams.
            for (int i = 0; i < BallGlowWedges; i++)
            {
                var wedge = CreateBeam(spawnPos, spawnPos, BallGlowColor(ball), BallGlowWidth * size, 0.05f);
                if (wedge != null) ball.Glow.Add(HandleOf(wedge));
            }

            for (int i = 0; i < BallCoreWedges; i++)
            {
                var wedge = CreateBeam(spawnPos, spawnPos, BallCoreColor(ball), BallCoreWidth * size, 0.05f);
                if (wedge != null) ball.Core.Add(HandleOf(wedge));
            }

            for (int t = 0; t < BallTendrils; t++)
            {
                var tendril = new Tendril { Offsets = TendrilShape(size) };
                for (int s = 0; s < BallTendrilSegments; s++)
                {
                    var glow = CreateBeam(spawnPos, spawnPos, BallTendrilGlowColor(ball), 0.6f * size, 0.25f * size);
                    if (glow != null) tendril.Glow.Add(HandleOf(glow));
                    var line = CreateBeam(spawnPos, spawnPos, BallTendrilLineColor(ball), 0.2f * size, 0.07f * size);
                    if (line != null) tendril.Line.Add(HandleOf(line));
                }
                ball.Tendrils.Add(tendril);
            }

            for (int i = 0; i < BallStreaks; i++)
            {
                var streak = CreateBeam(spawnPos, spawnPos, Whiten(BoltGlowColor(ball.Color), 0.5f), 0.8f * size, 0.05f);
                if (streak != null) ball.Streaks.Add(HandleOf(streak));
            }

            var tail = CreateBeam(spawnPos, spawnPos, Scale(BoltGlowColor(ball.Color), 0.6f), 0.5f * size, 0.05f);
            if (tail != null) ball.Tail = HandleOf(tail);

            lightningBalls.Add(ball);
            return ball;
        }

        // Debris only collides with the world, so the carrier flies through
        // players instead of bouncing off or wedging against them; whether a
        // player is hit is decided by FindHitPlayer alone
        private static void SetDebrisCollision(CBaseEntity entity)
        {
            var collision = entity.Collision;
            if (collision == null)
                return;

            collision.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_DEBRIS;
            collision.CollisionAttribute.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_DEBRIS;
            Utilities.SetStateChanged(entity, "CCollisionProperty", "m_CollisionGroup");
            Utilities.SetStateChanged(entity, "CCollisionProperty", "m_collisionAttribute");
        }

        // The physics engine skips collisions between an object and the entity
        // its collision OwnerId names. Pointing the carrier's OwnerId at the
        // shooter's pawn makes the ball pass straight through the shooter,
        // with every other collision unchanged.
        private static void IgnoreOwnerCollision(CBaseEntity entity, CBaseEntity owner)
        {
            var collision = entity.Collision;
            var ownerCollision = owner.Collision;
            if (collision == null || ownerCollision == null)
                return;

            collision.CollisionAttribute.OwnerId = ownerCollision.CollisionAttribute.EntityId;
            Utilities.SetStateChanged(entity, "CCollisionProperty", "m_collisionAttribute");
        }

        // Recoil the zeus itself (not the camera): put the weapon into its
        // shoot gameplay-anim state with a fresh timestamp, which makes the
        // client play the zeus's real firing kick on the first-person and
        // world models.
        private static void KickZeus(CCSWeaponBase zeus)
        {
            zeus.WeaponGameplayAnimState = WeaponGameplayAnimState.WPN_ANIMSTATE_SHOOT_PRIMARY;
            zeus.WeaponGameplayAnimStateTimestamp = Server.CurrentTime;
            zeus.LastShotTime = Server.CurrentTime;
            Utilities.SetStateChanged(zeus, "CCSWeaponBase", "m_iWeaponGameplayAnimState");
            Utilities.SetStateChanged(zeus, "CCSWeaponBase", "m_flWeaponGameplayAnimStateTimestamp");
        }

        private void OnSuperZeusTick()
        {
            if (pendingRecharges.Count > 0)
                ProcessPendingRecharges();

            if (ballSparks.Count > 0)
                UpdateBallSparks();

            if (bodyZaps.Count > 0)
                UpdateBodyZaps();

            if (bodyFades.Count > 0)
                UpdateBodyFades();

            if (grapples.Count > 0)
                UpdateGrapples();

            if (wallCameras.Count > 0)
                UpdateCameras();

            for (int i = lightningBalls.Count - 1; i >= 0; i--)
            {
                bool alive;
                try { alive = UpdateLightningBall(lightningBalls[i]); }
                catch (Exception ex)
                {
                    Logger.LogError("[SuperZeus] Ball update failed: {Error}", ex);
                    alive = false;
                }

                if (!alive)
                {
                    DestroyLightningBall(lightningBalls[i]);
                    lightningBalls.RemoveAt(i);
                }
            }
        }

        // Returns false once the ball is spent
        private bool UpdateLightningBall(LightningBall ball)
        {
            var projectile = ball.Projectile.Value;
            if (projectile == null || !projectile.IsValid || projectile.AbsOrigin == null)
            {
                LogBallEnd(ball, ball.LastPosition, "lost its carrier (deleted by something outside the plugin)");
                return false;
            }

            var pos = ToVec3(projectile.AbsOrigin);

            var hit = FindHitPlayer(ball, ball.LastPosition, pos + ball.Velocity * BallTickLookahead);
            if (hit != null)
            {
                if (IsEnemy(hit, ball))
                {
                    LogBallEnd(ball, pos, $"hit enemy {hit.PlayerName}");
                    StrikeVictim(hit, ball);
                }
                else
                {
                    LogBallEnd(ball, pos, $"dissipated on teammate {hit.PlayerName}");
                    WallBurst(pos); // teammates are never hurt; the ball just dissipates
                }
                return false;
            }

            if (projectile.Bounces > ball.SeenBounces)
            {
                ball.SeenBounces = projectile.Bounces;
                if (!ball.Reflect || ball.Reflections >= MaxReflections)
                {
                    LogBallEnd(ball, pos, ball.Reflections > 0 ? $"hit a wall after bouncing {ball.Reflections}x" : "hit a wall");
                    WallBurst(pos);
                    return false;
                }

                ReflectBall(ball, projectile, pos);
            }

            if (Server.CurrentTime >= ball.ExpireTime)
            {
                LogBallEnd(ball, pos, "timed out");
                SpawnParticle(FxImpactFlare, pos, Vector3.Zero, 1f);
                SpawnParticle(FxImpactArcs, pos, Vector3.Zero, 1f);
                return false;
            }

            // Keep the flight dead straight regardless of how the decoy simulates gravity
            projectile.Teleport(null, null, (Vector3?)ball.Velocity);

            AnimateBallBody(ball, pos);

            ball.LastPosition = pos;
            ball.Age++;
            return true;
        }

        private ConVar? teammatesAreEnemiesCvar;

        // Different team, or any other player when teammates are enemies
        // (free-for-all deathmatch and similar modes)
        private bool IsEnemy(CCSPlayerController player, LightningBall ball) =>
            player.TeamNum != ball.OwnerTeam || TeammatesAreEnemies();

        private bool TeammatesAreEnemies()
        {
            teammatesAreEnemiesCvar ??= ConVar.Find("mp_teammates_are_enemies");
            return teammatesAreEnemiesCvar?.StringValue is "1" or "true";
        }

        // One console line per ball saying how it ended and where the nearest
        // player was, so a ball that "didn't hit" can be diagnosed from the log
        private void LogBallEnd(LightningBall ball, Vector3 pos, string outcome)
        {
            string nearest = "no other players";
            float best = float.MaxValue;
            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid || player.Slot == ball.OwnerSlot)
                    continue;

                var origin = player.PlayerPawn.Value?.AbsOrigin;
                if (origin == null)
                    continue;

                var feet = ToVec3(origin);
                float horizontal = new Vector2(pos.X - feet.X, pos.Y - feet.Y).Length();
                if (horizontal < best)
                {
                    best = horizontal;
                    nearest = $"nearest {player.PlayerName} (team {player.TeamNum}, alive {player.PawnIsAlive}) "
                            + $"{horizontal:0} units away, {pos.Z - feet.Z:0} above their feet";
                }
            }

            Logger.LogInformation("[SuperZeus] Ball from slot {Slot} (team {Team}) {Outcome}; {Nearest}",
                ball.OwnerSlot, ball.OwnerTeam, outcome, nearest);
        }

        // Any living player (other than the shooter) whose body the ball's
        // center orb touched along this tick's path. The body is a capsule the
        // size of the player hull; the orb is a sphere of BallOrbRadius (times the ball's size).
        private CCSPlayerController? FindHitPlayer(LightningBall ball, Vector3 from, Vector3 to) =>
            FindHitPlayer(ball.OwnerSlot, BallOrbRadius * ball.Size, from, to);

        private static CCSPlayerController? FindHitPlayer(int ownerSlot, float radius, Vector3 from, Vector3 to)
        {
            float reach = radius + PlayerHullRadius;

            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid || !player.PawnIsAlive || player.Slot == ownerSlot)
                    continue;

                var pawn = player.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
                    continue;

                var feet = ToVec3(pawn.AbsOrigin);
                var axisLow = feet + new Vector3(0, 0, PlayerHullRadius);
                var axisHigh = feet + new Vector3(0, 0, PlayerHullHeight - PlayerHullRadius);

                if (SegmentDistanceSquared(from, to, axisLow, axisHigh) <= reach * reach)
                    return player;
            }

            return null;
        }

        // Squared distance between the closest points of segments p1-q1 and p2-q2
        private static float SegmentDistanceSquared(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            var d1 = q1 - p1;
            var d2 = q2 - p2;
            var r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;

            if (a < 1e-6f && e < 1e-6f)
                return r.LengthSquared();

            if (a < 1e-6f)
            {
                s = 0f;
                t = Math.Clamp(f / e, 0f, 1f);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e < 1e-6f)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom > 1e-6f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                    else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
                }
            }

            return ((p1 + d1 * s) - (p2 + d2 * t)).LengthSquared();
        }

        private void StrikeVictim(CCSPlayerController victim, LightningBall ball)
        {
            var pawn = victim.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
                return;

            var feet = ToVec3(pawn.AbsOrigin);
            var chest = feet + new Vector3(0, 0, 40f);
            var shooterPawn = Utilities.GetPlayerFromSlot(ball.OwnerSlot)?.PlayerPawn.Value;

            var flat = new Vector3(ball.Velocity.X, ball.Velocity.Y, 0f);
            var dir = flat.LengthSquared() < 1f ? Vector3.UnitX : Vector3.Normalize(flat);
            var launch = dir * RagdollLaunchSpeed + new Vector3(0f, 0f, RagdollLaunchSpeed * 0.3f);

            int before = pawn.Health;
            bool lethal = before <= ball.Damage;

            if (lethal)
            {
                // A CS2 body keeps the motion the player really had when they
                // died; pushes sent to the body after death are ignored (logs
                // showed force and bone set, no movement, and no server ragdoll
                // ever spawns). So throw the living player first and land the
                // killing damage a moment later, mid-flight.
                pawn.Teleport(null, null, (Vector3?)launch);
                var pawnHandle = HandleOf(pawn);
                string name = victim.PlayerName;
                AddTimer(MidFlightKillDelay, () => FinishMidFlight(pawnHandle, name, ball, shooterPawn, launch, before),
                    TimerFlags.STOP_ON_MAPCHANGE);
            }
            else
            {
                ApplyBallDamage(pawn, victim.PlayerName, shooterPawn, launch, chest, before, ball.Damage);

                if (pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE)
                {
                    try { KnockAndStun(pawn, dir); }
                    catch (Exception ex) { Logger.LogError("[SuperZeus] Knockback/stun failed for {Name}: {Message}", victim.PlayerName, ex.Message); }
                }
            }

            try
            {
                // Lethal: crackle rides the flying player, then their body.
                // Survivor: short crackle that stays on them.
                if (lethal)
                    bodyZapsByPawn[pawn.Index] = StartBodyZap(feet, launch, ball.Color, HandleOf(pawn), BodyZapLife);
                else
                    StartBodyZap(feet, Vector3.Zero, ball.Color, HandleOf(pawn), BodyZapHitLife);

                // A kill's shock burst and death sound play where the body
                // disintegrates (DisintegrationShow), not where the ball hit
                if (!lethal)
                {
                    ImpactBurst(chest);
                    PlaySound(pawn, "Weapon_Taser.Hit");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[SuperZeus] Hit effects failed: {Message}", ex.Message);
            }
        }

        // A survivor gets a slight shove along the ball's path and the same
        // "tagging" slowdown a gunshot gives: the pawn's networked velocity
        // modifier drops, and the game recovers it by itself as it does after
        // being shot.
        private const float SurvivorKnockback = 250f;     // horizontal, units/s
        private const float SurvivorKnockbackLift = 90f;  // upward, units/s
        private const float SurvivorStunModifier = 0.35f; // speed multiplier right after the hit

        private static void KnockAndStun(CCSPlayerPawn pawn, Vector3 direction)
        {
            var velocity = ToVec3(pawn.AbsVelocity) + direction * SurvivorKnockback + new Vector3(0f, 0f, SurvivorKnockbackLift);
            pawn.Teleport(null, null, (Vector3?)velocity);

            pawn.VelocityModifier = Math.Min(pawn.VelocityModifier, SurvivorStunModifier);
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
        }

        // Time the thrown player flies before the killing damage lands, so the
        // body has real momentum to keep (about seven server ticks)
        private const float MidFlightKillDelay = 0.12f;

        private void FinishMidFlight(CHandle<CCSPlayerPawn> pawnHandle, string name, LightningBall ball,
            CCSPlayerPawn? shooterPawn, Vector3 launch, int before)
        {
            var pawn = pawnHandle.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            {
                Logger.LogInformation("[SuperZeus] {Name} was already dead or gone before the mid-flight kill", name);
                return;
            }

            float speed = ToVec3(pawn.AbsVelocity).Length();
            var chest = ToVec3(pawn.AbsOrigin) + new Vector3(0, 0, 40f);
            if (shooterPawn != null && !shooterPawn.IsValid)
                shooterPawn = null;

            ApplyBallDamage(pawn, name, shooterPawn, launch, chest, Math.Min(before, pawn.Health), ball.Damage);
            Logger.LogInformation("[SuperZeus] {Name} was moving at {Speed:0} units/s when the killing damage landed", name, speed);

            if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
                AddTimer(DisintegrateDelay, () => DisintegrateBody(pawnHandle, name, ball.Color), TimerFlags.STOP_ON_MAPCHANGE);
        }

        // --- Disintegration ---------------------------------------------------
        //
        // A killed body disintegrates: the effects below play where the body
        // should be, and the body itself fades out (see Body fade).

        private const float DisintegrateDelay = 0.6f;   // let the body fly first

        private void DisintegrateBody(CHandle<CCSPlayerPawn> pawnHandle, string name, ZeusColorOption? color)
        {
            var pawn = pawnHandle.Value;
            if (pawn == null || !pawn.IsValid)
            {
                Logger.LogInformation("[SuperZeus] {Name}: body gone before it could disintegrate", name);
                return;
            }

            try { DisintegrationShow(pawn, color); }
            catch (Exception ex) { Logger.LogError("[SuperZeus] {Name}: disintegration effects failed: {Message}", name, ex.Message); }

            bodyFades.Add(new BodyFade { Pawn = pawnHandle, Name = name, Start = Server.CurrentTime });
        }

        // --- Body fade --------------------------------------------------------
        //
        // The engine's dissolve does nothing in CS2 (the dissolve time isn't
        // networked, and env_entity_dissolver never dissolved a body). The dead
        // body is drawn from the player's own pawn, whose render mode and color
        // are networked, so the body is faded to invisible under the effects
        // instead. CS2 reuses that pawn when the player respawns, so it's made
        // visible again on spawn, round start and map change.

        private const float BodyFadeTime = 1.2f;

        private sealed class BodyFade
        {
            public required CHandle<CCSPlayerPawn> Pawn;
            public required string Name;
            public required float Start;
        }

        private readonly List<BodyFade> bodyFades = new();
        private readonly HashSet<uint> hiddenPawns = new();

        private void UpdateBodyFades()
        {
            float now = Server.CurrentTime;
            for (int i = bodyFades.Count - 1; i >= 0; i--)
            {
                var fade = bodyFades[i];
                var pawn = fade.Pawn.Value;

                // Gone, or alive again (respawned before the fade finished)
                if (pawn == null || !pawn.IsValid || pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE)
                {
                    bodyFades.RemoveAt(i);
                    continue;
                }

                try
                {
                    // Burning away: the body chars to charcoal over the first
                    // half, then fades out over the second
                    float t = (now - fade.Start) / BodyFadeTime;
                    hiddenPawns.Add(pawn.Index);
                    if (t >= 1f)
                    {
                        SetPawnRender(pawn, RenderMode_t.kRenderNone, Color.FromArgb(0, CharredColor));
                        Logger.LogInformation("[SuperZeus] {Name}: body burned away", fade.Name);
                        bodyFades.RemoveAt(i);
                    }
                    else
                    {
                        float charring = Math.Min(1f, t * 2f);
                        float fading = Math.Max(0f, t * 2f - 1f);
                        var c = Color.FromArgb(
                            (int)(255 * (1f - fading)),
                            (int)(255 + (CharredColor.R - 255) * charring),
                            (int)(255 + (CharredColor.G - 255) * charring),
                            (int)(255 + (CharredColor.B - 255) * charring));
                        SetPawnRender(pawn, RenderMode_t.kRenderTransAlpha, c);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("[SuperZeus] {Name}: body fade failed: {Message}", fade.Name, ex.Message);
                    bodyFades.RemoveAt(i);
                }
            }
        }

        private static readonly Color CharredColor = Color.FromArgb(255, 35, 30, 28);

        private static void SetPawnRender(CCSPlayerPawn pawn, RenderMode_t mode, Color color)
        {
            pawn.RenderMode = mode;
            pawn.Render = color;
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_nRenderMode");
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        }

        // Make a reused pawn visible again
        private void RestorePawn(CCSPlayerPawn? pawn)
        {
            if (pawn == null || !pawn.IsValid || !hiddenPawns.Remove(pawn.Index))
                return;

            try { SetPawnRender(pawn, RenderMode_t.kRenderNormal, Color.FromArgb(255, 255, 255, 255)); }
            catch (Exception ex) { Logger.LogError("[SuperZeus] Restoring a faded body failed: {Message}", ex.Message); }
        }

        private void RestoreAllPawns()
        {
            bodyFades.Clear();
            foreach (var player in Utilities.GetPlayers())
            {
                var pawn = player?.PlayerPawn.Value;
                if (pawn != null && pawn.IsValid && hiddenPawns.Contains(pawn.Index))
                    RestorePawn(pawn);
            }
            hiddenPawns.Clear();
        }

        private HookResult OnSpawnRestoreBody(EventPlayerSpawn ev, GameEventInfo info)
        {
            var player = ev.Userid;
            Server.NextFrame(() =>
            {
                if (player != null && player.IsValid)
                    RestorePawn(player.PlayerPawn.Value);
            });
            return HookResult.Continue;
        }

        // The show around the dissolve, centered on where the body should be:
        // a heavy crackle, an expanding shockwave ring, a burst of embers, the
        // stock impact burst and a layered crackle of sound
        private void DisintegrationShow(CCSPlayerPawn pawn, ZeusColorOption? color)
        {
            Vector3 center;
            Vector3 drift = Vector3.Zero;
            if (bodyZapsByPawn.Remove(pawn.Index, out var tracker))
            {
                center = tracker.Center;
                drift = tracker.Velocity;
            }
            else
            {
                center = (pawn.AbsOrigin != null ? ToVec3(pawn.AbsOrigin) : Vector3.Zero) + new Vector3(0, 0, PlayerHullHeight * 0.5f);
            }

            var glow = BoltGlowColor(color);
            var feet = center - new Vector3(0, 0, PlayerHullHeight * 0.5f);

            StartBodyZap(feet, drift, color, null, DisintegrateCrackleLife, DisintegrateCrackleArcs);
            EmitBurstSparks(center, glow, 40, 180f, 520f);
            ImpactBurst(center);

            // Burn it out: a fire with embers and thick dark smoke where the
            // body lies. Each stops emitting and its last particles finish
            // before the entity is removed, so it dies down instead of popping.
            var ground = center - new Vector3(0, 0, PlayerHullHeight * 0.3f);
            SpawnFading(FxBurnFire, ground, BurnFireTime, BurnFireLinger);
            SpawnFading(FxBurnSmoke, ground, BurnSmokeTime, BurnSmokeLinger);

            var flash = SpawnParticle(FxImpactFlare, center, Vector3.Zero, 2f);
            PlaySound(flash, "Player.DeathTaser");
            PlaySound(flash, "Weapon_Taser.Hit");
            PlaySound(flash, "DoSpark");
            PlaySound(flash, "Breakable.Spark");
        }

        private const float DisintegrateCrackleLife = 0.4f;

        private const float BurnFireTime = 1.1f, BurnFireLinger = 2.5f;
        private const float BurnSmokeTime = 2.5f, BurnSmokeLinger = 5f;

        // A looping effect that stops emitting after `emit` seconds and is
        // removed `linger` seconds after that, once its particles have faded
        private void SpawnFading(string effect, Vector3 pos, float emit, float linger)
        {
            var particle = SpawnParticle(effect, pos, Vector3.Zero, 0f);
            if (particle == null)
                return;

            var handle = HandleOf(particle);
            AddTimer(emit, () =>
            {
                var p = handle.Value;
                if (p != null && p.IsValid)
                    p.AcceptInput("Stop");
            }, TimerFlags.STOP_ON_MAPCHANGE);
            RemoveLater(handle, emit + linger);
        }
        private const int DisintegrateCrackleArcs = 12;

        // 34 damage with the shooter's credit and the launch on the damage
        private void ApplyBallDamage(CCSPlayerPawn pawn, string name, CCSPlayerPawn? shooterPawn, Vector3 launch, Vector3 chest, int before, int damage)
        {
            var shooter = shooterPawn != null && shooterPawn.IsValid ? shooterPawn.Controller.Value?.As<CCSPlayerController>() : null;
            int scoreBefore = shooter?.Score ?? 0;

            pendingBallForces[pawn.Index] = new PendingBallHit(launch, chest, shooterPawn, FindZeus(shooterPawn));
            ballForceApplied = false;
            try { DamagePawn(pawn, shooterPawn, chest, before, damage); }
            finally { pendingBallForces.Remove(pawn.Index); }

            bool killed = pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE;
            if (killed)
                AwardBallKillScore(shooter, scoreBefore, name);
            Logger.LogInformation("[SuperZeus] Hit {Name} for {Damage}: health {Before} -> {After}{Killed} (shooter credit + launch force {Force})",
                name, damage, before, killed ? 0 : pawn.Health, killed ? ", killed" : "",
                ballForceApplied ? "applied" : "not applied");

            if (killed)
            {
                try { StampRagdollImpulse(pawn, launch); }
                catch (Exception ex) { Logger.LogError("[SuperZeus] Ragdoll launch failed: {Message}", ex.Message); }
            }
        }

        // The game credits the kill (kill feed, "with taser" in the log) but
        // adds no score for it, so the shooter gets a normal kill's points,
        // unless the game already added some
        private const int BallKillScore = 2;

        private void AwardBallKillScore(CCSPlayerController? shooter, int scoreBefore, string victim)
        {
            if (shooter == null || !shooter.IsValid)
                return;

            try
            {
                if (shooter.Score != scoreBefore)
                {
                    Logger.LogInformation("[SuperZeus] {Shooter} already got {Points} points for killing {Victim}", shooter.PlayerName, shooter.Score - scoreBefore, victim);
                    return;
                }

                shooter.Score += BallKillScore;
                Utilities.SetStateChanged(shooter, "CCSPlayerController", "m_iScore");
                Logger.LogInformation("[SuperZeus] {Shooter} +{Points} score for killing {Victim} (now {Score})", shooter.PlayerName, BallKillScore, victim, shooter.Score);
            }
            catch (Exception ex)
            {
                Logger.LogError("[SuperZeus] Awarding kill score failed: {Message}", ex.Message);
            }
        }

        // What the damage a ball is applying right now should carry, by pawn index
        private sealed record PendingBallHit(Vector3 Launch, Vector3 At, CCSPlayerPawn? Attacker, CBasePlayerWeapon? Zeus);

        private readonly Dictionary<uint, PendingBallHit> pendingBallForces = new();
        private bool ballForceApplied;

        // Stamps the ball's own damage as it's processed:
        //  - attacker = the shooter and weapon = their Zeus, so the game credits
        //    the shooter with the kill (score and kill feed) exactly like a zap;
        //  - the launch force, which the game builds the ragdoll's throw from
        //    (as an impulse, ~100x the velocity) and is what sends bodies flying
        private HookResult OnBallDamagePre(CBaseEntity entity, CTakeDamageInfo info)
        {
            if (entity != null && entity.IsValid && FallProtected(entity, info))
                return HookResult.Handled;

            if (pendingBallForces.Count == 0 || entity == null || !entity.IsValid
                || !pendingBallForces.TryGetValue(entity.Index, out var pending))
                return HookResult.Continue;

            var attacker = pending.Attacker;
            if (attacker != null && attacker.IsValid && attacker.TeamNum == entity.TeamNum && !TeammatesAreEnemies())
                return HookResult.Continue;

            if (attacker != null && attacker.IsValid)
            {
                info.Attacker.Raw = attacker.EntityHandle.Raw;
                info.Inflictor.Raw = attacker.EntityHandle.Raw;
                if (pending.Zeus != null && pending.Zeus.IsValid)
                    info.Ability.Raw = pending.Zeus.EntityHandle.Raw;
            }

            // The damage-time force is what the dropped gun gets thrown with,
            // so it's half the body's; the body's own push is stamped after
            // death (StampRagdollImpulse). The chest hit group gives the game
            // a body location for the ragdoll push.
            var direction = Vector3.Normalize(pending.Launch);
            info.DamageForce.X = pending.Launch.X * DroppedGunForceScale;
            info.DamageForce.Y = pending.Launch.Y * DroppedGunForceScale;
            info.DamageForce.Z = pending.Launch.Z * DroppedGunForceScale;
            info.HitGroupId = HitGroup_t.HITGROUP_CHEST;
            info.DamagePosition.X = pending.At.X;
            info.DamagePosition.Y = pending.At.Y;
            info.DamagePosition.Z = pending.At.Z;
            info.DamageDirection.X = direction.X;
            info.DamageDirection.Y = direction.Y;
            info.DamageDirection.Z = direction.Z;
            ballForceApplied = true;
            return HookResult.Continue;
        }

        // The shooter's Zeus, for the kill feed icon (null if they no longer have one)
        private static CBasePlayerWeapon? FindZeus(CCSPlayerPawn? pawn)
        {
            var weapons = pawn?.WeaponServices?.MyWeapons;
            if (weapons == null)
                return null;

            foreach (var handle in weapons)
            {
                var weapon = handle.Value;
                if (weapon != null && weapon.IsValid && weapon.DesignerName == "weapon_taser")
                    return weapon;
            }
            return null;
        }

        // The ball's shock damage credited to the shooter, applied by a point_hurt aimed
        // at the victim so it goes through the game's normal damage and death
        // handling. If that has no effect, health is lowered directly (and the
        // forced kill used when it would reach zero).
        private void DamagePawn(CCSPlayerPawn pawn, CCSPlayerPawn? attacker, Vector3 at, int before, int damage)
        {
            try
            {
                var hurt = Utilities.CreateEntityByName<CPointHurt>("point_hurt");
                var identity = pawn.Entity;
                if (hurt != null && identity != null)
                {
                    string previousName = identity.Name;
                    string targetName = $"zeusball_target_{pawn.Index}";
                    identity.Name = targetName;

                    hurt.Damage = damage;
                    hurt.BitsDamageType = DamageTypes_t.DMG_SHOCK;
                    hurt.StrTarget = targetName;
                    hurt.Teleport(at, Vector3.Zero, Vector3.Zero);
                    hurt.DispatchSpawn();
                    hurt.AcceptInput("Hurt", attacker ?? (CEntityInstance)pawn, attacker ?? (CEntityInstance)pawn);
                    hurt.Remove();

                    if (pawn.IsValid)
                        identity.Name = previousName;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[SuperZeus] point_hurt damage failed: {Message}", ex.Message);
            }

            if (!pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || pawn.Health < before)
                return;

            int remaining = before - damage;
            if (remaining <= 0)
            {
                KillPawn(pawn);
                return;
            }

            pawn.Health = remaining;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }

        // Kept in its own non-inlined method on purpose: schema fields come and
        // go between CS2 updates (m_vRagdollDamagePosition was removed), and a
        // missing member only fails the method that references it. Isolated
        // here, that can never stop the kill in StrikeVictim.
        //
        // The client applies the ragdoll push at m_nRagdollDamageBone. Damage
        // from point_hurt carries no hit location, so the game can leave that
        // bone unset and the push goes nowhere (the gun flew, the body didn't).
        // If it's unset, point it at the pelvis (bone 0) so the push lands.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StampRagdollImpulse(CCSPlayerPawn pawn, Vector3 launch)
        {
            int gameBone = pawn.RagdollDamageBone;
            if (gameBone < 0)
            {
                pawn.RagdollDamageBone = 0;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_nRagdollDamageBone");
            }

            pawn.RagdollDamageForce.X = launch.X * RagdollForceScale;
            pawn.RagdollDamageForce.Y = launch.Y * RagdollForceScale;
            pawn.RagdollDamageForce.Z = launch.Z * RagdollForceScale;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_vRagdollDamageForce");

        }

        // Forced suicide; if the pawn is somehow still alive, fall back to the
        // SetHealth input, which applies real damage through the normal death path
        private bool KillPawn(CCSPlayerPawn pawn)
        {
            try { pawn.CommitSuicide(false, true); }
            catch (Exception ex) { Logger.LogError("[SuperZeus] CommitSuicide failed: {Message}", ex.Message); }

            if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
                return true;

            try { pawn.AcceptInput("SetHealth", pawn, pawn, "0"); }
            catch (Exception ex) { Logger.LogError("[SuperZeus] SetHealth failed: {Message}", ex.Message); }

            return pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE;
        }

        // --- Reflect Zeus -------------------------------------------------------
        //
        // The carrier bounces off the surface like a grenade, damped and with
        // friction, so its new velocity only roughly mirrors the old one. The
        // change in velocity points along the surface's normal, though, so the
        // ball is mirrored off that normal exactly (angle in = angle out) and
        // keeps its full speed. Each bounce scorches the surface and sparks.
        private const int MaxReflections = 1;

        // Speed lines streaming back from every ball along its direction of
        // travel, spaced evenly around it (so a bounce's new angle shows too)
        private const int BallStreaks = 3;
        private const float BallStreakLength = 70f;
        private const float BallStreakSpread = 5f;

        private static void AimStreaks(LightningBall ball, Vector3 pos)
        {
            var dir = Vector3.Normalize(ball.Velocity);
            var reference = MathF.Abs(dir.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
            var side = Vector3.Normalize(Vector3.Cross(dir, reference));
            var up = Vector3.Cross(side, dir);

            for (int i = 0; i < ball.Streaks.Count; i++)
            {
                float a = i / (float)ball.Streaks.Count * MathF.PI * 2f;
                var offset = (side * MathF.Cos(a) + up * MathF.Sin(a)) * BallStreakSpread * ball.Size;
                var start = pos + offset - dir * BallGlowRadius * 0.5f * ball.Size;
                MoveBallBeam(ball.Streaks[i], start, start - dir * BallStreakLength * ball.Size);
            }
        }

        // The surface a carrier just bounced off faces along its change in
        // velocity (out of the surface: the bounce pushed it that way)
        private static Vector3 SurfaceNormal(Vector3 incoming, CDecoyProjectile projectile)
        {
            var normal = ToVec3(projectile.AbsVelocity) - incoming;
            return normal.LengthSquared() < 1f ? -Vector3.Normalize(incoming) : Vector3.Normalize(normal);
        }

        // Unit direction mirrored off the surface (angle in = angle out)
        private static Vector3 Reflected(Vector3 incoming, Vector3 normal)
        {
            var reflected = incoming - 2f * Vector3.Dot(incoming, normal) * normal;
            return Vector3.Normalize(reflected.LengthSquared() < 1f ? -incoming : reflected);
        }

        private void ReflectBall(LightningBall ball, CDecoyProjectile projectile, Vector3 pos)
        {
            var incoming = ball.Velocity;
            var normal = SurfaceNormal(incoming, projectile);
            ball.Velocity = Reflected(incoming, normal) * incoming.Length();
            ball.Reflections++;

            LeaveScorch(pos);
            var flash = SpawnParticle(FxImpactFlare, pos, Vector3.Zero, 1f);
            SpawnParticle(FxImpactArcs, pos, Vector3.Zero, 1f);
            PlaySound(flash, "DoSpark");

            Logger.LogInformation("[SuperZeus] Ball from slot {Slot} bounced ({Count}/{Max}) off a surface facing ({NX:0.00}, {NY:0.00}, {NZ:0.00})",
                ball.OwnerSlot, ball.Reflections, MaxReflections, normal.X, normal.Y, normal.Z);
        }

        private void WallBurst(Vector3 pos)
        {
            LeaveScorch(pos);
            var burst = ImpactBurst(pos);
            PlaySound(burst, "Weapon_Taser.Hit");
            PlaySound(burst, "DoSpark");
        }

        // --- Scorch marks -----------------------------------------------------
        //
        // A burn mark where a ball hits the world: the stock barrel-fire scorch
        // is a triplanar projected decal, so it marks walls, floors and
        // ceilings whichever way they face. It lasts as long as its entity, so
        // each mark is kept ScorchLifetime seconds, and only the newest
        // MaxScorches are kept.
        private const float ScorchLifetime = 20f;
        private const int MaxScorches = 24;

        private readonly Queue<CHandle<CParticleSystem>> scorches = new();

        private void LeaveScorch(Vector3 pos)
        {
            var scorch = SpawnParticle(FxScorch, pos, Vector3.Zero, ScorchLifetime);
            if (scorch == null)
                return;

            scorches.Enqueue(HandleOf(scorch));
            while (scorches.Count > MaxScorches)
                RemoveEntity(scorches.Dequeue());
        }

        private CParticleSystem? ImpactBurst(Vector3 pos)
        {
            var flash = SpawnParticle(FxImpactFlare, pos, Vector3.Zero, 2f);
            SpawnParticle(FxImpactSparks, pos, Vector3.Zero, 2f);
            SpawnParticle(FxImpactRing, pos, Vector3.Zero, 2f);
            SpawnParticle(FxImpactArcs, pos, Vector3.Zero, 2f);
            SpawnParticle(FxImpactDebris, pos, Vector3.Zero, 2f);
            SpawnParticle(FxBallGlow, pos, Vector3.Zero, 2f);
            return flash;
        }

        private const int BallGlowWedges = 12;
        private const float BallGlowRadius = 12f;
        private const float BallGlowWidth = 10f;
        private const int BallCoreWedges = 4;
        private const float BallCoreRadius = 4.5f;
        private const float BallCoreWidth = 4.5f;
        private const int BallTendrils = 3;
        private const int BallTendrilSegments = 4;

        // Dim wide wedges overlap into a soft sphere: brightest in the middle,
        // fading toward the edge. The core is small and nearly white-hot.
        private static Color BallGlowColor(LightningBall ball) => Scale(BoltGlowColor(ball.Color), 0.22f);
        private static Color BallCoreColor(LightningBall ball) => Scale(Whiten(BoltGlowColor(ball.Color), 0.7f), 0.8f);
        private static Color BallTendrilGlowColor(LightningBall ball) => Scale(BoltGlowColor(ball.Color), 0.45f);
        private static Color BallTendrilLineColor(LightningBall ball) => Whiten(BoltGlowColor(ball.Color), 0.4f);

        // A jagged arc reaching out from the ball, as offsets from its center
        private Vector3[] TendrilShape(float size)
        {
            var tip = RandomUnit() * (11f + rng.NextSingle() * 10f) * size;
            var points = new Vector3[BallTendrilSegments + 1];
            for (int i = 0; i <= BallTendrilSegments; i++)
            {
                float t = i / (float)BallTendrilSegments;
                var jitter = i == 0 || i == BallTendrilSegments ? Vector3.Zero : RandomUnit() * 3f * size * MathF.Sin(t * MathF.PI);
                points[i] = tip * t + jitter;
            }
            return points;
        }

        private void AnimateBallBody(LightningBall ball, Vector3 pos)
        {
            // Glow and core wedges re-angle every tick, so the sphere shimmers
            foreach (var handle in ball.Glow)
            {
                var wedge = handle.Value;
                if (wedge == null || !wedge.IsValid) continue;
                wedge.Teleport(pos, Vector3.Zero, Vector3.Zero);
                SetBeamEnd(wedge, pos + RandomUnit() * BallGlowRadius * ball.Size * (0.8f + rng.NextSingle() * 0.3f), true);
            }

            foreach (var handle in ball.Core)
            {
                var wedge = handle.Value;
                if (wedge == null || !wedge.IsValid) continue;
                wedge.Teleport(pos, Vector3.Zero, Vector3.Zero);
                SetBeamEnd(wedge, pos + RandomUnit() * BallCoreRadius * ball.Size, true);
            }

            // Tendrils re-strike at random and otherwise ride along with the ball
            foreach (var tendril in ball.Tendrils)
            {
                if (rng.NextSingle() < 0.35f)
                    tendril.Offsets = TendrilShape(ball.Size);

                for (int s = 0; s < BallTendrilSegments; s++)
                {
                    var a = pos + tendril.Offsets[s];
                    var b = pos + tendril.Offsets[s + 1];
                    MoveBallBeam(s < tendril.Glow.Count ? tendril.Glow[s] : null, a, b);
                    MoveBallBeam(s < tendril.Line.Count ? tendril.Line[s] : null, a, b);
                }
            }

            if (ball.Streaks.Count > 0)
                AimStreaks(ball, pos);

            var tail = ball.Tail?.Value;
            if (tail != null && tail.IsValid)
            {
                tail.Teleport(pos, Vector3.Zero, Vector3.Zero);
                SetBeamEnd(tail, pos - Vector3.Normalize(ball.Velocity) * 48f * ball.Size, true);
            }

            // A few sparks shed as it flies
            if (rng.NextSingle() < 0.5f)
                EmitBallSparks(ball, pos);
        }

        private static void MoveBallBeam(CHandle<CBeam>? handle, Vector3 start, Vector3 end)
        {
            var beam = handle?.Value;
            if (beam == null || !beam.IsValid) return;
            beam.Teleport(start, Vector3.Zero, Vector3.Zero);
            SetBeamEnd(beam, end, true);
        }

        // Player color, or the stock blue
        private static Color BoltGlowColor(ZeusColorOption? color) =>
            color == null ? ArcGlowColor : color.Color;

        private static Color BoltCoreColor(ZeusColorOption? color) =>
            color == null ? ArcCoreColor : Whiten(color.Color, 0.6f);

        // --- Colored ball sparks -------------------------------------------
        //
        // Real sparks: tiny glowing streaks flung off the ball that arc down
        // under gravity, slow with drag, and cool from white-hot to the ball's
        // color before fading out. Each is a short tapered beam stretched
        // along its velocity (motion blur), so it reads as a smooth streak.

        private const int MaxBallSparks = 160;
        private const float SparkGravity = 800f;
        private const float SparkDrag = 2.5f;

        private sealed class BallSpark
        {
            public required CHandle<CBeam> Beam;
            public required Color Color;
            public required float Born;
            public required float Life;
            public Vector3 Pos;
            public Vector3 Vel;
            public float LastUpdate;
        }

        private readonly List<BallSpark> ballSparks = new();

        private void EmitBallSparks(LightningBall ball, Vector3 pos)
        {
            int count = 1;
            for (int i = 0; i < count && ballSparks.Count < MaxBallSparks; i++)
            {
                var start = pos + RandomUnit() * 3f;
                var vel = ball.Velocity * 0.1f + RandomUnit() * (140f + rng.NextSingle() * 260f) + new Vector3(0f, 0f, 60f);
                var color = BoltGlowColor(ball.Color);

                var beam = CreateBeam(start, start - vel * 0.03f, Whiten(color, 0.8f), 0.8f, 0.2f);
                if (beam == null) return;

                float now = Server.CurrentTime;
                ballSparks.Add(new BallSpark
                {
                    Beam = HandleOf(beam),
                    Color = color,
                    Born = now,
                    Life = 0.25f + rng.NextSingle() * 0.3f,
                    Pos = start,
                    Vel = vel,
                    LastUpdate = now
                });
            }
        }

        // A burst of white-hot sparks flung out in every direction
        private void EmitBurstSparks(Vector3 center, Color color, int count, float minSpeed, float maxSpeed)
        {
            float now = Server.CurrentTime;
            for (int i = 0; i < count && ballSparks.Count < MaxBallSparks; i++)
            {
                var start = center + RandomUnit() * 6f;
                var vel = RandomUnit() * (minSpeed + rng.NextSingle() * (maxSpeed - minSpeed)) + new Vector3(0f, 0f, 120f);
                var beam = CreateBeam(start, start - vel * 0.03f, Whiten(color, 0.8f), 0.8f, 0.2f);
                if (beam == null) return;

                ballSparks.Add(new BallSpark
                {
                    Beam = HandleOf(beam),
                    Color = color,
                    Born = now,
                    Life = 0.5f + rng.NextSingle() * 0.6f,
                    Pos = start,
                    Vel = vel,
                    LastUpdate = now
                });
            }
        }

        private void UpdateBallSparks()
        {
            float now = Server.CurrentTime;
            for (int i = ballSparks.Count - 1; i >= 0; i--)
            {
                var spark = ballSparks[i];
                var beam = spark.Beam.Value;
                float age = now - spark.Born;

                if (beam == null || !beam.IsValid || age >= spark.Life)
                {
                    RemoveEntity(spark.Beam);
                    ballSparks.RemoveAt(i);
                    continue;
                }

                float dt = now - spark.LastUpdate;
                spark.LastUpdate = now;
                spark.Vel.Z -= SparkGravity * dt;
                spark.Vel *= Math.Max(0f, 1f - SparkDrag * dt);
                spark.Pos += spark.Vel * dt;

                // White-hot at birth, cooling to the ball color, then fading out
                float t = age / spark.Life;
                var color = Scale(Whiten(spark.Color, Math.Max(0f, 0.8f - t * 3f)), MathF.Pow(1f - t, 1.3f));

                beam.Teleport(spark.Pos, Vector3.Zero, Vector3.Zero);
                SetBeamEnd(beam, spark.Pos - spark.Vel * 0.03f, true);
                beam.Render = color;
                Utilities.SetStateChanged(beam, "CBaseModelEntity", "m_clrRender");
            }
        }

        private void ClearBallSparks()
        {
            foreach (var spark in ballSparks)
                RemoveEntity(spark.Beam);
            ballSparks.Clear();
        }

        // --- Body zap ------------------------------------------------------
        //
        // A colored take on the stock taser death crackle (taser_body_fx, which
        // needs the body's model snapshot and can't be spawned by a plugin):
        // short jagged arcs crawling over a player-sized capsule, re-striking
        // every tick. The ragdoll is simulated on the client, so the capsule
        // follows the body's expected flight from the launch velocity, with
        // gravity, and settles on the floor it died on.

        private const int BodyZapArcs = 5;
        private const int BodyZapSegments = 3;
        private const float BodyZapLife = 0.35f;     // on a kill
        private const float BodyZapHitLife = 0.2f;   // on a survivor
        private const float BodyZapFade = 0.3f;
        private const float BodyGravity = 800f;
        private const float BodyGroundFriction = 6f;

        private sealed class BodyZap
        {
            public required ZeusColorOption? Color;
            public required float FloorZ;
            public required float Born;
            public required float Life;
            public required int Arcs;
            public CHandle<CCSPlayerPawn>? Follow;     // a living victim to stay on
            public Vector3 Center;
            public Vector3 Velocity;
            public float LastUpdate;
            public readonly List<CHandle<CBeam>> Glow = new();
            public readonly List<CHandle<CBeam>> Line = new();
        }

        private readonly List<BodyZap> bodyZaps = new();

        // The crackle tracking each thrown victim; its center is the best
        // estimate of where their client-side body is
        private readonly Dictionary<uint, BodyZap> bodyZapsByPawn = new();

        private BodyZap StartBodyZap(Vector3 feet, Vector3 launch, ZeusColorOption? color, CHandle<CCSPlayerPawn>? follow, float life, int arcs = BodyZapArcs)
        {
            float now = Server.CurrentTime;
            var zap = new BodyZap
            {
                Color = color,
                FloorZ = feet.Z + PlayerHullRadius,
                Born = now,
                Life = life,
                Arcs = arcs,
                Follow = follow,
                Center = feet + new Vector3(0, 0, PlayerHullHeight * 0.5f),
                Velocity = launch,
                LastUpdate = now
            };

            for (int i = 0; i < arcs * BodyZapSegments; i++)
            {
                var glow = CreateBeam(zap.Center, zap.Center, Scale(BoltGlowColor(color), 0.45f), 1.0f, 0.4f);
                if (glow != null) zap.Glow.Add(HandleOf(glow));
                var line = CreateBeam(zap.Center, zap.Center, Whiten(BoltGlowColor(color), 0.4f), 0.3f, 0.1f);
                if (line != null) zap.Line.Add(HandleOf(line));
            }

            bodyZaps.Add(zap);
            UpdateBodyZap(zap);
            return zap;
        }

        private void UpdateBodyZaps()
        {
            for (int i = bodyZaps.Count - 1; i >= 0; i--)
            {
                if (!UpdateBodyZap(bodyZaps[i]))
                {
                    DestroyBodyZap(bodyZaps[i]);
                    bodyZaps.RemoveAt(i);
                }
            }
        }

        // Returns false once the crackle has faded
        private bool UpdateBodyZap(BodyZap zap)
        {
            float now = Server.CurrentTime;
            float age = now - zap.Born;
            if (age >= zap.Life)
                return false;

            float dt = now - zap.LastUpdate;
            zap.LastUpdate = now;

            var followed = zap.Follow?.Value;
            if (followed != null && followed.IsValid && followed.AbsOrigin != null
                && followed.LifeState == (byte)LifeState_t.LIFE_ALIVE)
            {
                // Alive (a survivor, or a thrown player not yet dead): stay on
                // them, and keep their motion for when they die
                zap.Center = ToVec3(followed.AbsOrigin) + new Vector3(0, 0, PlayerHullHeight * 0.5f);
                zap.Velocity = ToVec3(followed.AbsVelocity);
            }
            else
            {
                // A kill: follow the body's expected flight, ballistic and then
                // sliding to a stop once it reaches the floor
                zap.Velocity.Z -= BodyGravity * dt;
                zap.Center += zap.Velocity * dt;
                if (zap.Center.Z <= zap.FloorZ)
                {
                    zap.Center.Z = zap.FloorZ;
                    zap.Velocity.Z = 0f;
                    zap.Velocity *= Math.Max(0f, 1f - BodyGroundFriction * dt);
                }
            }

            float fade = Math.Clamp((zap.Life - age) / BodyZapFade, 0f, 1f);
            var glowColor = Scale(BoltGlowColor(zap.Color), 0.45f * fade);
            var lineColor = Scale(Whiten(BoltGlowColor(zap.Color), 0.4f), fade);

            int beam = 0;
            for (int arc = 0; arc < zap.Arcs; arc++)
            {
                var points = BodyArcPoints(zap.Center);
                for (int s = 0; s < BodyZapSegments; s++, beam++)
                {
                    if (beam < zap.Glow.Count) MoveColoredBeam(zap.Glow[beam], points[s], points[s + 1], glowColor);
                    if (beam < zap.Line.Count) MoveColoredBeam(zap.Line[beam], points[s], points[s + 1], lineColor);
                }
            }

            return true;
        }

        // A short arc hugging the body: it starts on the capsule surface and
        // crawls part of the way around it, with jagged midpoints
        private Vector3[] BodyArcPoints(Vector3 center)
        {
            float angle = rng.NextSingle() * MathF.PI * 2f;
            float sweep = (0.8f + rng.NextSingle()) * (rng.NextSingle() < 0.5f ? -1f : 1f);
            float height = (rng.NextSingle() - 0.5f) * (PlayerHullHeight - 8f);
            float climb = (rng.NextSingle() - 0.5f) * 24f;

            var points = new Vector3[BodyZapSegments + 1];
            for (int i = 0; i <= BodyZapSegments; i++)
            {
                float t = i / (float)BodyZapSegments;
                float a = angle + sweep * t;
                float radius = PlayerHullRadius * (0.7f + rng.NextSingle() * 0.4f);
                points[i] = center + new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, height + climb * t);
            }
            return points;
        }

        private static void MoveColoredBeam(CHandle<CBeam> handle, Vector3 start, Vector3 end, Color color)
        {
            var beam = handle.Value;
            if (beam == null || !beam.IsValid) return;
            beam.Teleport(start, Vector3.Zero, Vector3.Zero);
            SetBeamEnd(beam, end, true);
            beam.Render = color;
            Utilities.SetStateChanged(beam, "CBaseModelEntity", "m_clrRender");
        }

        private static void DestroyBodyZap(BodyZap zap)
        {
            foreach (var beam in zap.Glow) RemoveEntity(beam);
            foreach (var beam in zap.Line) RemoveEntity(beam);
        }

        private void ClearBodyZaps()
        {
            foreach (var zap in bodyZaps)
                DestroyBodyZap(zap);
            bodyZaps.Clear();
        }

        // endWidth below width tapers the beam (0 = to a point)
        private static CBeam? CreateBeam(Vector3 start, Vector3 end, Color color, float width, float? endWidth = null)
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null)
                return null;

            beam.Render = color;
            beam.Width = width;
            beam.EndWidth = endWidth ?? width;
            beam.Teleport(start, Vector3.Zero, Vector3.Zero);
            SetBeamEnd(beam, end, false);
            beam.DispatchSpawn();
            return beam;
        }

        private static void SetBeamEnd(CBeam beam, Vector3 end, bool networked)
        {
            beam.EndPos.X = end.X;
            beam.EndPos.Y = end.Y;
            beam.EndPos.Z = end.Z;
            if (networked)
                Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");
        }

        // lifetime <= 0 means the caller owns cleanup
        private CParticleSystem? SpawnParticle(string effect, Vector3 pos, Vector3 angles, float lifetime, CBaseEntity? parent = null)
        {
            var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
            if (particle == null)
                return null;

            particle.EffectName = effect;
            particle.StartActive = true;
            particle.Teleport(pos, angles, Vector3.Zero);
            particle.DispatchSpawn();

            if (parent != null)
                particle.AcceptInput("SetParent", parent, particle, "!activator");

            if (lifetime > 0)
                RemoveLater(HandleOf(particle), lifetime);

            return particle;
        }

        private void DestroyLightningBall(LightningBall ball)
        {
            RemoveEntity(ball.Projectile);
            if (ball.Tail != null) RemoveEntity(ball.Tail);
            foreach (var beam in ball.Streaks) RemoveEntity(beam);
            foreach (var beam in ball.Glow) RemoveEntity(beam);
            foreach (var beam in ball.Core) RemoveEntity(beam);
            foreach (var tendril in ball.Tendrils)
            {
                foreach (var beam in tendril.Glow) RemoveEntity(beam);
                foreach (var beam in tendril.Line) RemoveEntity(beam);
            }
        }

        private void ClearLightningBalls()
        {
            foreach (var ball in lightningBalls)
                DestroyLightningBall(ball);
            lightningBalls.Clear();
            ClearBallSparks();
            ClearBodyZaps();
            bodyZapsByPawn.Clear();
            RestoreAllPawns();
            while (scorches.Count > 0)
                RemoveEntity(scorches.Dequeue());
        }

        // Handles survive round restarts safely, unlike raw entity references held across ticks
        private static CHandle<T> HandleOf<T>(T entity) where T : CEntityInstance =>
            new CHandle<T>(entity.EntityHandle.Raw);

        private void RemoveLater<T>(CHandle<T> handle, float delay) where T : CEntityInstance =>
            AddTimer(delay, () => RemoveEntity(handle), TimerFlags.STOP_ON_MAPCHANGE);

        private static void RemoveEntity<T>(CHandle<T> handle) where T : CEntityInstance
        {
            var entity = handle.Value;
            if (entity != null && entity.IsValid)
                entity.Remove();
        }

        private Vector3 RandomUnit()
        {
            var v = new Vector3(rng.NextSingle() * 2f - 1f, rng.NextSingle() * 2f - 1f, rng.NextSingle() * 2f - 1f);
            return v.LengthSquared() < 0.0001f ? Vector3.UnitZ : Vector3.Normalize(v);
        }

        private static Vector3 ToVec3(Vector v) => new(v.X, v.Y, v.Z);

        private static void AngleVectors(Vector3 angles, out Vector3 forward, out Vector3 right, out Vector3 up)
        {
            float pitch = angles.X * MathF.PI / 180f;
            float yaw = angles.Y * MathF.PI / 180f;
            float sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
            float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);

            forward = new Vector3(cp * cy, cp * sy, -sp);
            right = new Vector3(sy, -cy, 0f);
            up = new Vector3(sp * cy, sp * sy, cp);
        }

        private static Vector3 DirectionToAngles(Vector3 dir)
        {
            dir = Vector3.Normalize(dir);
            float pitch = -MathF.Asin(Math.Clamp(dir.Z, -1f, 1f)) * 180f / MathF.PI;
            float yaw = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;
            return new Vector3(pitch, yaw, 0f);
        }

        private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 point)
        {
            var ab = b - a;
            float lengthSq = ab.LengthSquared();
            if (lengthSq < 0.0001f)
                return a;

            float t = Math.Clamp(Vector3.Dot(point - a, ab) / lengthSq, 0f, 1f);
            return a + ab * t;
        }
    }
}
