using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace ZeusOnlyMode
{
    public partial class ZeusOnlyPlugin
    {
        private const string SuperZeusPermission = "@css/generic";

        private const float BallSpeed = 1500f;
        private const float BallLifetime = 2.5f;
        private const float BallCooldown = 1.0f;
        private const float BallOrbRadius = 12f;     // hit size: the visible center orb
        private const float PlayerHullRadius = 16f;  // half of the 32-unit player hull
        private const float PlayerHullHeight = 72f;
        private const float BallTickLookahead = 1f / 64f; // one server tick of travel

        // How hard a lightning ball kill flings the enemy's ragdoll
        // (horizontal speed along the ball's path, plus 30% of it upward)
        private const float RagdollLaunchSpeed = 2400f;

        // Damage per lightning ball hit (a little over a third of full health)
        private const int BallDamage = 34;

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

        private const string BallCarrierModel = "weapons/models/grenade/decoy/weapon_decoy.vmdl";

        private static readonly string[] SuperZeusResources =
        {
            BallCarrierModel,
            FxBallGlow, FxBallSparks, FxMuzzleGlow, FxMuzzleSmokePuff, FxMuzzleSmokeTrail,
            FxImpactFlare, FxImpactSparks, FxImpactRing, FxImpactArcs, FxImpactDebris
        };

        private static readonly Color ArcCoreColor = Color.FromArgb(255, 210, 240, 255);
        private static readonly Color ArcGlowColor = Color.FromArgb(255, 70, 150, 255);

        private bool superZeusEnabled = false;
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
            public required int OwnerSlot;
            public required byte OwnerTeam;
            public required ZeusColorOption? Color; // null = stock electric blue
            public required Vector3 Velocity;
            public required Vector3 LastPosition;
            public required float ExpireTime;
            public int Age;
        }

        private void LoadSuperZeus()
        {
            AddCommand("superzeus", "Toggle Super Zeus (alt-fire lightning balls)", (player, info) =>
                ToggleSuperZeus(player, info.ArgCount > 1 ? info.GetArg(1) : null));

            // Handle the chat form ourselves so the message is swallowed and never shown to players
            AddCommandListener("say", OnSuperZeusChat);
            AddCommandListener("say_team", OnSuperZeusChat);

            RegisterListener<Listeners.OnTick>(PollButtons);
            RegisterListener<Listeners.OnEntityTakeDamagePre>(OnBallDamagePre);
            RegisterListener<Listeners.OnTick>(OnSuperZeusTick);
            RegisterListener<Listeners.OnClientDisconnect>(slot => lastButtons.Remove(slot));
            RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
            {
                foreach (var resource in SuperZeusResources)
                    manifest.AddResource(resource);
            });
            RegisterListener<Listeners.OnMapStart>(_ =>
            {
                lightningBalls.Clear();
                ballSparks.Clear();
                bodyZaps.Clear();
                pendingBallForces.Clear();
                nextBallTime.Clear();
                lastButtons.Clear();
                loggedOnce.Clear();
                pendingRecharges.Clear();
                taserRechargeCvar = null;
                teammatesAreEnemiesCvar = null;
            });
            RegisterEventHandler<EventRoundStart>((ev, info) =>
            {
                ClearLightningBalls();
                return HookResult.Continue;
            });
        }

        private void UnloadSuperZeus()
        {
            ClearLightningBalls();
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

        private void ToggleSuperZeus(CCSPlayerController? player, string? arg)
        {
            if (player != null && !AdminManager.PlayerHasPermissions(player, SuperZeusPermission))
            {
                player.PrintToChat("You do not have access to this command.");
                return;
            }

            bool newState = arg switch
            {
                "1" => true,
                "0" => false,
                _ => !superZeusEnabled
            };

            if (newState == superZeusEnabled)
            {
                ReplyToAdmin(player, $"Super Zeus Is Already {(superZeusEnabled ? "Enabled" : "Disabled")}.");
                return;
            }

            superZeusEnabled = newState;

            if (superZeusEnabled)
                Server.PrintToChatAll($" {ChatColors.LightBlue}SUPER ZEUS ENABLED{ChatColors.Default} - Right-Click With Your Zeus To Hurl A Lightning Ball!");
            else
                Server.PrintToChatAll($" {ChatColors.LightBlue}Super Zeus Disabled.");
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
            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                    continue;

                // Each player is isolated: one player in an odd state
                // (spectating, connecting, ...) must never stop the loop and
                // take right-click away from everyone after them
                try
                {
                    var movement = player.Pawn.Value?.MovementServices;
                    if (movement == null)
                        continue;

                    // ButtonStates: [0] held now, [1] toggled an odd number of
                    // times this tick, [2] toggled two or more times this tick.
                    // A quick tap the server processed within one tick (common
                    // on a busy server, where several of a player's inputs can
                    // land in the same tick) only shows up in [2].
                    var states = movement.Buttons.ButtonStates;
                    var held = (PlayerButtons)states[0];
                    var tapped = (PlayerButtons)states[2];

                    lastButtons.TryGetValue(player.Slot, out var previous);
                    lastButtons[player.Slot] = held;

                    var pressed = (held & ~previous) | tapped;
                    if (pressed == 0)
                        continue;

                    OnSuperZeusButtons(player, pressed);
                    OnZeusColorButtons(player, pressed);
                }
                catch (Exception ex)
                {
                    LogOnce($"poll:{ex.GetType().Name}:{ex.Message}",
                        $"[SuperZeus] Reading buttons for {player.PlayerName} failed: {ex}");
                }
            }
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

            if (player == null || !player.IsValid || !player.PawnIsAlive)
                return;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
                return;

            var weapon = pawn.WeaponServices?.ActiveWeapon.Value;
            if (weapon == null || !weapon.IsValid || weapon.DesignerName != "weapon_taser")
                return;

            // From here on every right-click with the Zeus is logged with its
            // outcome, so a missing ball can be explained from the console
            if (!superZeusEnabled)
            {
                Logger.LogInformation("[SuperZeus] {Name} right-clicked with the Zeus, but Super Zeus is off", player.PlayerName);
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
                pawn.EmitSound("Weapon_Taser.ChargeNotReady");
                return;
            }

            Logger.LogInformation("[SuperZeus] {Name} fired a lightning ball", player.PlayerName);
            nextBallTime[player.Slot] = now + BallCooldown;
            ConsumeZeusCharge(zeus);
            FireLightningBall(player, pawn, zeus);
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
                    owner.EmitSound("Weapon_Taser.ChargeReady");

                pendingRecharges.RemoveAt(i);
            }
        }

        private void FireLightningBall(CCSPlayerController player, CCSPlayerPawn pawn, CCSWeaponBase zeus)
        {
            var eye = ToVec3(pawn.AbsOrigin!) + new Vector3(0, 0, pawn.ViewOffset.Z);
            var angles = new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0);
            AngleVectors(angles, out var forward, out var right, out var up);

            var spawnPos = eye + forward * 24f;
            var muzzle = eye + forward * 28f + right * (pawn.LeftHanded ? -7f : 7f) - up * 7f;
            var velocity = forward * BallSpeed;

            // An invisible decoy carries the ball so the engine handles world collision for us
            var projectile = Utilities.CreateEntityByName<CDecoyProjectile>("decoy_projectile");
            if (projectile == null)
                return;

            projectile.TeamNum = pawn.TeamNum;
            projectile.Thrower.Raw = pawn.EntityHandle.Raw;
            projectile.OriginalThrower.Raw = pawn.EntityHandle.Raw;
            projectile.OwnerEntity.Raw = pawn.EntityHandle.Raw;
            projectile.RenderMode = RenderMode_t.kRenderNone;
            SetDebrisCollision(projectile);
            projectile.DispatchSpawn();
            projectile.SetModel(BallCarrierModel); // guarantees a physics hull for wall collision
            SetDebrisCollision(projectile);
            projectile.GravityScale = 0.001f;
            projectile.Teleport(spawnPos, angles, velocity);

            var ball = new LightningBall
            {
                Projectile = HandleOf(projectile),
                OwnerSlot = player.Slot,
                OwnerTeam = pawn.TeamNum,
                Color = GetZeusColor(player),
                Velocity = velocity,
                LastPosition = spawnPos,
                ExpireTime = Server.CurrentTime + BallLifetime
            };

            // A small BFG-style ball in the player's color (stock blue on
            // Default): a soft glowing sphere, a hot core, crackling tendrils,
            // a faint tail and a few sparks. The stock particles are baked
            // white-blue and can't be recolored, so the ball is built from beams.
            for (int i = 0; i < BallGlowWedges; i++)
            {
                var wedge = CreateBeam(spawnPos, spawnPos, BallGlowColor(ball), BallGlowWidth, 0.05f);
                if (wedge != null) ball.Glow.Add(HandleOf(wedge));
            }

            for (int i = 0; i < BallCoreWedges; i++)
            {
                var wedge = CreateBeam(spawnPos, spawnPos, BallCoreColor(ball), BallCoreWidth, 0.05f);
                if (wedge != null) ball.Core.Add(HandleOf(wedge));
            }

            for (int t = 0; t < BallTendrils; t++)
            {
                var tendril = new Tendril { Offsets = TendrilShape() };
                for (int s = 0; s < BallTendrilSegments; s++)
                {
                    var glow = CreateBeam(spawnPos, spawnPos, BallTendrilGlowColor(ball), 1.0f, 0.4f);
                    if (glow != null) tendril.Glow.Add(HandleOf(glow));
                    var line = CreateBeam(spawnPos, spawnPos, BallTendrilLineColor(ball), 0.3f, 0.1f);
                    if (line != null) tendril.Line.Add(HandleOf(line));
                }
                ball.Tendrils.Add(tendril);
            }

            var tail = CreateBeam(spawnPos, spawnPos, Scale(BoltGlowColor(ball.Color), 0.6f), 0.5f, 0.05f);
            if (tail != null) ball.Tail = HandleOf(tail);

            lightningBalls.Add(ball);

            // Muzzle: glow, sparks, a heavy puff, and a smoke wisp that trails off the shooter
            SpawnParticle(FxMuzzleGlow, muzzle, angles, 1f);
            SpawnParticle(FxImpactFlare, muzzle, angles, 1f);
            SpawnParticle(FxBallSparks, muzzle, angles, 1f);
            SpawnParticle(FxMuzzleSmokePuff, muzzle, angles, 3f);
            SpawnParticle(FxMuzzleSmokeTrail, muzzle, angles, 5f, pawn);

            pawn.EmitSound("Weapon_Taser.Single");
            pawn.EmitSound("Weapon_Taser.ChargeReady_Zap");

            KickZeus(zeus);
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
                return false;

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

            if (projectile.Bounces > 0)
            {
                LogBallEnd(ball, pos, "hit a wall");
                WallBurst(pos);
                return false;
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
        private bool IsEnemy(CCSPlayerController player, LightningBall ball)
        {
            if (player.TeamNum != ball.OwnerTeam)
                return true;

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
        // size of the player hull; the orb is a sphere of BallOrbRadius.
        private CCSPlayerController? FindHitPlayer(LightningBall ball, Vector3 from, Vector3 to)
        {
            const float reach = BallOrbRadius + PlayerHullRadius;

            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid || !player.PawnIsAlive || player.Slot == ball.OwnerSlot)
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

            // Fling a body the hit is about to kill along the ball's path:
            // velocity set BEFORE death is inherited by the ragdoll, and the
            // ragdoll impulse stamped right after throws the client ragdoll
            var flat = new Vector3(ball.Velocity.X, ball.Velocity.Y, 0f);
            var dir = flat.LengthSquared() < 1f ? Vector3.UnitX : Vector3.Normalize(flat);
            var launch = dir * RagdollLaunchSpeed + new Vector3(0f, 0f, RagdollLaunchSpeed * 0.3f);

            int before = pawn.Health;
            if (before <= BallDamage)
                pawn.Teleport(null, null, (Vector3?)launch);

            // Damage comes first, so nothing cosmetic below can ever stop it.
            // While it's applied, OnBallDamagePre puts the launch on the damage
            // itself, which is what the game builds the ragdoll's throw from.
            pendingBallForces[pawn.Index] = new PendingBallHit(launch, chest, shooterPawn, FindZeus(shooterPawn));
            ballForceApplied = false;
            try { DamagePawn(pawn, shooterPawn, chest, before); }
            finally { pendingBallForces.Remove(pawn.Index); }
            bool killed = pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE;
            Logger.LogInformation("[SuperZeus] Hit {Name} for {Damage}: health {Before} -> {After}{Killed} (shooter credit + launch force {Force})",
                victim.PlayerName, BallDamage, before, killed ? 0 : pawn.Health, killed ? ", killed" : "",
                ballForceApplied ? "applied" : "not applied");

            if (killed)
            {
                try { StampRagdollImpulse(pawn, launch); }
                catch (Exception ex) { Logger.LogError("[SuperZeus] Ragdoll launch failed: {Message}", ex.Message); }
            }

            try
            {
                if (killed)
                    StartBodyZap(feet, launch, ball.Color, null, BodyZapLife);
                else
                    StartBodyZap(feet, Vector3.Zero, ball.Color, HandleOf(pawn), BodyZapHitLife);

                ImpactBurst(chest);
                pawn.EmitSound(killed ? "Player.DeathTaser" : "Weapon_Taser.Hit");
            }
            catch (Exception ex)
            {
                Logger.LogError("[SuperZeus] Hit effects failed: {Message}", ex.Message);
            }
        }

        // Launch force for the damage a ball is applying right now, by pawn index
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
            if (pendingBallForces.Count == 0 || entity == null || !entity.IsValid
                || !pendingBallForces.TryGetValue(entity.Index, out var pending))
                return HookResult.Continue;

            var attacker = pending.Attacker;
            if (attacker != null && attacker.IsValid)
            {
                info.Attacker.Raw = attacker.EntityHandle.Raw;
                info.Inflictor.Raw = attacker.EntityHandle.Raw;
                if (pending.Zeus != null && pending.Zeus.IsValid)
                    info.Ability.Raw = pending.Zeus.EntityHandle.Raw;
            }

            var direction = Vector3.Normalize(pending.Launch);
            info.DamageForce.X = pending.Launch.X * 100f;
            info.DamageForce.Y = pending.Launch.Y * 100f;
            info.DamageForce.Z = pending.Launch.Z * 100f;
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

        // 34 shock damage credited to the shooter, applied by a point_hurt aimed
        // at the victim so it goes through the game's normal damage and death
        // handling. If that has no effect, health is lowered directly (and the
        // forced kill used when it would reach zero).
        private void DamagePawn(CCSPlayerPawn pawn, CCSPlayerPawn? attacker, Vector3 at, int before)
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

                    hurt.Damage = BallDamage;
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

            int remaining = before - BallDamage;
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
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StampRagdollImpulse(CCSPlayerPawn pawn, Vector3 launch)
        {
            pawn.RagdollDamageForce.X = launch.X * 100f;
            pawn.RagdollDamageForce.Y = launch.Y * 100f;
            pawn.RagdollDamageForce.Z = launch.Z * 100f;
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

        private void WallBurst(Vector3 pos)
        {
            var burst = ImpactBurst(pos);
            burst?.EmitSound("Weapon_Taser.Hit");
            burst?.EmitSound("DoSpark");
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
        private Vector3[] TendrilShape()
        {
            var tip = RandomUnit() * (22f + rng.NextSingle() * 20f);
            var points = new Vector3[BallTendrilSegments + 1];
            for (int i = 0; i <= BallTendrilSegments; i++)
            {
                float t = i / (float)BallTendrilSegments;
                var jitter = i == 0 || i == BallTendrilSegments ? Vector3.Zero : RandomUnit() * 6f * MathF.Sin(t * MathF.PI);
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
                SetBeamEnd(wedge, pos + RandomUnit() * BallGlowRadius * (0.8f + rng.NextSingle() * 0.3f), true);
            }

            foreach (var handle in ball.Core)
            {
                var wedge = handle.Value;
                if (wedge == null || !wedge.IsValid) continue;
                wedge.Teleport(pos, Vector3.Zero, Vector3.Zero);
                SetBeamEnd(wedge, pos + RandomUnit() * BallCoreRadius, true);
            }

            // Tendrils re-strike at random and otherwise ride along with the ball
            foreach (var tendril in ball.Tendrils)
            {
                if (rng.NextSingle() < 0.35f)
                    tendril.Offsets = TendrilShape();

                for (int s = 0; s < BallTendrilSegments; s++)
                {
                    var a = pos + tendril.Offsets[s];
                    var b = pos + tendril.Offsets[s + 1];
                    MoveBallBeam(s < tendril.Glow.Count ? tendril.Glow[s] : null, a, b);
                    MoveBallBeam(s < tendril.Line.Count ? tendril.Line[s] : null, a, b);
                }
            }

            var tail = ball.Tail?.Value;
            if (tail != null && tail.IsValid)
            {
                tail.Teleport(pos, Vector3.Zero, Vector3.Zero);
                SetBeamEnd(tail, pos - Vector3.Normalize(ball.Velocity) * 48f, true);
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

        private const int MaxBallSparks = 90;
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
        private const float BodyZapLife = 0.9f;      // on a kill
        private const float BodyZapHitLife = 0.5f;   // on a survivor
        private const float BodyZapFade = 0.3f;
        private const float BodyGravity = 800f;
        private const float BodyGroundFriction = 6f;

        private sealed class BodyZap
        {
            public required ZeusColorOption? Color;
            public required float FloorZ;
            public required float Born;
            public required float Life;
            public CHandle<CCSPlayerPawn>? Follow;     // a living victim to stay on
            public Vector3 Center;
            public Vector3 Velocity;
            public float LastUpdate;
            public readonly List<CHandle<CBeam>> Glow = new();
            public readonly List<CHandle<CBeam>> Line = new();
        }

        private readonly List<BodyZap> bodyZaps = new();

        private void StartBodyZap(Vector3 feet, Vector3 launch, ZeusColorOption? color, CHandle<CCSPlayerPawn>? follow, float life)
        {
            float now = Server.CurrentTime;
            var zap = new BodyZap
            {
                Color = color,
                FloorZ = feet.Z + PlayerHullRadius,
                Born = now,
                Life = life,
                Follow = follow,
                Center = feet + new Vector3(0, 0, PlayerHullHeight * 0.5f),
                Velocity = launch,
                LastUpdate = now
            };

            for (int i = 0; i < BodyZapArcs * BodyZapSegments; i++)
            {
                var glow = CreateBeam(zap.Center, zap.Center, Scale(BoltGlowColor(color), 0.45f), 1.0f, 0.4f);
                if (glow != null) zap.Glow.Add(HandleOf(glow));
                var line = CreateBeam(zap.Center, zap.Center, Whiten(BoltGlowColor(color), 0.4f), 0.3f, 0.1f);
                if (line != null) zap.Line.Add(HandleOf(line));
            }

            bodyZaps.Add(zap);
            UpdateBodyZap(zap);
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
            if (followed != null && followed.IsValid && followed.AbsOrigin != null)
            {
                // A survivor: stay on them as they move
                zap.Center = ToVec3(followed.AbsOrigin) + new Vector3(0, 0, PlayerHullHeight * 0.5f);
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
            for (int arc = 0; arc < BodyZapArcs; arc++)
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
