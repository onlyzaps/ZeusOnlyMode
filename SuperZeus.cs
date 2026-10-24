using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Drawing;
using System.Globalization;
using Vector3 = System.Numerics.Vector3;

namespace ZeusOnlyMode
{
    public partial class ZeusOnlyPlugin
    {
        private const string SuperZeusPermission = "@css/generic";

        private const float BallSpeed = 1500f;
        private const float BallLifetime = 2.5f;
        private const float BallCooldown = 1.0f;
        private const float BallHitRadius = 28f;

        // How hard a lightning ball kill flings the enemy's ragdoll
        // (horizontal speed along the ball's path, plus 30% of it upward)
        private const float RagdollLaunchSpeed = 2400f;

        // All effects below exist in CS2's pak01 and only rely on control point 0,
        // so they render correctly from a plain info_particle_system.
        //
        // Avoid weapon_taser_flash (and weapon_muzzle_flash_taser, which
        // contains it): it's a viewmodel sprite drawn MOD2X with 10x overbright
        // and no z-buffering, so out in the world it renders as a white box.
        private const string FxBallCore = "particles/ambient_fx/ambient_sparks_glow.vpcf";          // looping arcs + flares + particle light
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
            FxBallCore, FxBallGlow, FxBallSparks, FxMuzzleGlow, FxMuzzleSmokePuff, FxMuzzleSmokeTrail,
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

        private sealed class LightningBall
        {
            public required CHandle<CDecoyProjectile> Projectile;
            public CHandle<CParticleSystem>? Core;
            public CHandle<COmniLight>? Light;
            public CHandle<CBeam>? Tail;
            public readonly List<CHandle<CBeam>> Arcs = new();
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
                nextBallTime.Clear();
                lastButtons.Clear();
                pendingRecharges.Clear();
                taserRechargeCvar = null;
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
                if (player == null || !player.IsValid || player.IsBot)
                    continue;

                var movement = player.Pawn.Value?.MovementServices;
                if (movement == null)
                    continue;

                var buttons = (PlayerButtons)movement.Buttons.ButtonStates[0];
                lastButtons.TryGetValue(player.Slot, out var previous);
                lastButtons[player.Slot] = buttons;

                var pressed = buttons & ~previous;
                if (pressed == 0)
                    continue;

                OnSuperZeusButtons(player, pressed);
                OnZeusColorButtons(player, pressed);
            }
        }

        private void OnSuperZeusButtons(CCSPlayerController player, PlayerButtons pressed)
        {
            if (!superZeusEnabled || (pressed & PlayerButtons.Attack2) == 0)
                return;

            if (player == null || !player.IsValid || !player.PawnIsAlive)
                return;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
                return;

            var weapon = pawn.WeaponServices?.ActiveWeapon.Value;
            if (weapon == null || !weapon.IsValid || weapon.DesignerName != "weapon_taser")
                return;

            float now = Server.CurrentTime;
            if (nextBallTime.TryGetValue(player.Slot, out var readyAt) && now < readyAt)
                return;

            // Same energy as a normal zap: no charge, no ball
            var zeus = weapon.As<CWeaponTaser>();
            if (zeus.Clip1 <= 0)
            {
                pawn.EmitSound("Weapon_Taser.ChargeNotReady");
                return;
            }

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
            projectile.DispatchSpawn();
            projectile.SetModel(BallCarrierModel); // guarantees a physics hull for wall collision
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

            // The stock particles are baked white-blue and can't be recolored,
            // so a colored ball drops them: its body is a few soft tapered
            // rays, a tapered comet tail, and real sparks (EmitBallSparks)
            bool colored = ball.Color != null;
            if (!colored)
            {
                var core = SpawnParticle(FxBallCore, spawnPos, angles, 0f);
                if (core != null) ball.Core = HandleOf(core);
            }

            var light = SpawnBallLight(spawnPos, BoltGlowColor(ball.Color));
            if (light != null) ball.Light = HandleOf(light);

            int arcCount = colored ? ColoredBallRays : 4;
            for (int i = 0; i < arcCount; i++)
            {
                // Colored rays taper to a point so they read as soft glow, not lines
                var arc = colored
                    ? CreateBeam(spawnPos, spawnPos, BallArcColor(ball, i), i % 2 == 0 ? 2.5f : 3.5f, 0.4f)
                    : CreateBeam(spawnPos, spawnPos, BallArcColor(ball, i), i % 2 == 0 ? 1.2f : 2.4f);
                if (arc != null) ball.Arcs.Add(HandleOf(arc));
            }

            if (colored)
            {
                var tail = CreateBeam(spawnPos, spawnPos, BoltGlowColor(ball.Color), 4f, 0.4f);
                if (tail != null) ball.Tail = HandleOf(tail);
            }

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

            for (int i = lightningBalls.Count - 1; i >= 0; i--)
            {
                if (!UpdateLightningBall(lightningBalls[i]))
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

            var hit = FindHitPlayer(ball, ball.LastPosition, pos);
            if (hit != null)
            {
                if (hit.TeamNum != ball.OwnerTeam)
                    StrikeVictim(hit, ball);
                else
                    WallBurst(pos); // teammates are never hurt; the ball just dissipates
                return false;
            }

            if (projectile.Bounces > 0)
            {
                WallBurst(pos);
                return false;
            }

            if (Server.CurrentTime >= ball.ExpireTime)
            {
                SpawnParticle(FxImpactFlare, pos, Vector3.Zero, 1f);
                SpawnParticle(FxImpactArcs, pos, Vector3.Zero, 1f);
                return false;
            }

            // Keep the flight dead straight regardless of how the decoy simulates gravity
            projectile.Teleport(null, null, (Vector3?)ball.Velocity);

            var angles = DirectionToAngles(ball.Velocity);
            ball.Core?.Value?.Teleport(pos, angles, Vector3.Zero);

            ball.Light?.Value?.Teleport(pos, Vector3.Zero, Vector3.Zero);

            // Re-roll the arcs every tick so the ball constantly writhes; a
            // colored ball's soft rays stay short so they read as a glow
            bool colored = ball.Color != null;
            for (int i = 0; i < ball.Arcs.Count; i++)
            {
                var arc = ball.Arcs[i].Value;
                if (arc == null || !arc.IsValid) continue;

                float reach = colored ? 6f + rng.NextSingle() * 8f : 14f + rng.NextSingle() * 14f;
                arc.Teleport(pos + RandomUnit() * (colored ? 1f : 3f), Vector3.Zero, Vector3.Zero);
                SetBeamEnd(arc, pos + RandomUnit() * reach, true);
            }

            var tail = ball.Tail?.Value;
            if (tail != null && tail.IsValid)
            {
                tail.Teleport(pos, Vector3.Zero, Vector3.Zero);
                SetBeamEnd(tail, pos - Vector3.Normalize(ball.Velocity) * 56f, true);
            }

            if (colored)
            {
                EmitBallSparks(ball, pos);
            }
            else
            {
                // Spark and glow puffs left behind in the world form the stock trail
                if (ball.Age % 4 == 0)
                    SpawnParticle(FxBallSparks, pos, angles, 1f);
                if (ball.Age % 6 == 0)
                    SpawnParticle(FxBallGlow, pos, angles, 1f);
            }

            ball.LastPosition = pos;
            ball.Age++;
            return true;
        }

        // Any living player (other than the shooter) the ball passed through this tick
        private CCSPlayerController? FindHitPlayer(LightningBall ball, Vector3 from, Vector3 to)
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid || !player.PawnIsAlive || player.Slot == ball.OwnerSlot)
                    continue;

                var pawn = player.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
                    continue;

                // Treat the player as an upright capsule and test it against this tick's travel segment
                var feet = ToVec3(pawn.AbsOrigin);
                var closest = ClosestPointOnSegment(from, to, feet + new Vector3(0, 0, 36f));
                float dx = closest.X - feet.X;
                float dy = closest.Y - feet.Y;

                if (dx * dx + dy * dy <= BallHitRadius * BallHitRadius
                    && closest.Z >= feet.Z - 8f && closest.Z <= feet.Z + 80f)
                    return player;
            }

            return null;
        }

        private void StrikeVictim(CCSPlayerController victim, LightningBall ball)
        {
            var pawn = victim.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
                return;

            var chest = ToVec3(pawn.AbsOrigin) + new Vector3(0, 0, 40f);
            var sky = chest + new Vector3((rng.NextSingle() - 0.5f) * 120f, (rng.NextSingle() - 0.5f) * 120f, 620f);
            var color = ball.Color;

            // Two quick strokes with different shapes read as a flickering strike
            DrawLightningBolt(sky, chest, 0.12f, color, 1f, true);
            AddTimer(0.14f, () => DrawLightningBolt(sky, chest, 0.22f, color, 1f, true), TimerFlags.STOP_ON_MAPCHANGE);

            ImpactBurst(chest);
            SpawnParticle(FxImpactFlare, sky, Vector3.Zero, 1f);

            pawn.EmitSound("Player.DeathTaser");
            pawn.EmitSound("Flashbang.Explode");

            // Fling the body along the ball's path: velocity set BEFORE death is
            // inherited by the ragdoll, and the ragdoll impulse stamped right
            // after (same tick, so the client gets both with the death) throws
            // the client-side ragdoll.
            var flat = new Vector3(ball.Velocity.X, ball.Velocity.Y, 0f);
            var dir = flat.LengthSquared() < 1f ? Vector3.UnitX : Vector3.Normalize(flat);
            var launch = dir * RagdollLaunchSpeed + new Vector3(0f, 0f, RagdollLaunchSpeed * 0.3f);

            pawn.Teleport(null, null, (Vector3?)launch);
            pawn.CommitSuicide(false, true);

            pawn.RagdollDamageForce.X = launch.X * 100f;
            pawn.RagdollDamageForce.Y = launch.Y * 100f;
            pawn.RagdollDamageForce.Z = launch.Z * 100f;
            pawn.RagdollDamagePosition.X = chest.X;
            pawn.RagdollDamagePosition.Y = chest.Y;
            pawn.RagdollDamagePosition.Z = chest.Z;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_vRagdollDamageForce");
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_vRagdollDamagePosition");
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

        private const int ColoredBallRays = 5;

        // Stock ball: hot whitened cores alternating with blue glow arcs.
        // Colored ball: barely whitened cores so the color stays saturated.
        private static Color BallArcColor(LightningBall ball, int index)
        {
            if (index % 2 != 0)
                return BoltGlowColor(ball.Color);
            return ball.Color == null ? ArcCoreColor : Whiten(ball.Color.Color, 0.25f);
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
            int count = rng.NextSingle() < 0.5f ? 1 : 2;
            for (int i = 0; i < count && ballSparks.Count < MaxBallSparks; i++)
            {
                var start = pos + RandomUnit() * 3f;
                var vel = ball.Velocity * 0.1f + RandomUnit() * (140f + rng.NextSingle() * 260f) + new Vector3(0f, 0f, 60f);
                var color = ball.Color!.Color;

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

        // Jagged multi-segment bolt: a wide glow beam with a hot core beam on
        // top, plus optional side branches. scale shrinks it for zaps.
        private void DrawLightningBolt(Vector3 start, Vector3 end, float lifetime, ZeusColorOption? color, float scale, bool branches)
        {
            var beams = new List<CHandle<CBeam>>();

            int segments = Math.Max(4, (int)(Vector3.Distance(start, end) / 55f));
            var trunk = JaggedPath(start, end, Math.Min(segments, 12), 26f * scale);
            for (int i = 0; i < trunk.Count - 1; i++)
            {
                float t = i / (float)trunk.Count;
                AddBeam(beams, trunk[i], trunk[i + 1], BoltGlowColor(color), 9f * scale);
                AddBeam(beams, trunk[i], trunk[i + 1], BoltCoreColor(color), 3f * scale);
            }

            if (branches && trunk.Count > 5)
            {
                for (int b = 0; b < 3; b++)
                {
                    var origin = trunk[2 + rng.Next(trunk.Count - 5)];
                    var dir = Vector3.Normalize(RandomUnit() + new Vector3(0, 0, -1.2f));
                    var branch = JaggedPath(origin, origin + dir * (70f + rng.NextSingle() * 90f), 4, 14f);
                    for (int i = 0; i < branch.Count - 1; i++)
                        AddBeam(beams, branch[i], branch[i + 1], BoltGlowColor(color), 3f);
                }
            }

            AddTimer(lifetime, () =>
            {
                foreach (var beam in beams)
                    RemoveEntity(beam);
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }

        private List<Vector3> JaggedPath(Vector3 start, Vector3 end, int segments, float jitter)
        {
            var points = new List<Vector3> { start };
            for (int i = 1; i < segments; i++)
            {
                float t = (float)i / segments;
                points.Add(Vector3.Lerp(start, end, t) + RandomUnit() * jitter * (rng.NextSingle() * 0.5f + 0.5f));
            }
            points.Add(end);
            return points;
        }

        private void AddBeam(List<CHandle<CBeam>> list, Vector3 start, Vector3 end, Color color, float width)
        {
            var beam = CreateBeam(start, end, color, width);
            if (beam != null)
                list.Add(HandleOf(beam));
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

        private static COmniLight? SpawnBallLight(Vector3 pos, Color color)
        {
            var light = Utilities.CreateEntityByName<COmniLight>("light_omni2");
            if (light == null)
                return null;

            light.Enabled = true;
            light.DirectLight = 3; // dynamic
            light.CastShadows = 0;
            light.Color = color;
            light.Brightness = 0.35f;
            light.Range = 140f;
            light.InnerAngle = 180f;
            light.OuterAngle = 180f;
            light.Teleport(pos, Vector3.Zero, Vector3.Zero);
            light.DispatchSpawn();
            return light;
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
            if (ball.Core != null) RemoveEntity(ball.Core);
            if (ball.Light != null) RemoveEntity(ball.Light);
            if (ball.Tail != null) RemoveEntity(ball.Tail);
            foreach (var arc in ball.Arcs)
                RemoveEntity(arc);
        }

        private void ClearLightningBalls()
        {
            foreach (var ball in lightningBalls)
                DestroyLightningBall(ball);
            lightningBalls.Clear();
            ClearBallSparks();
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
