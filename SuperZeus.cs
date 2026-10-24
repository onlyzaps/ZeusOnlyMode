using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Drawing;
using Vector3 = System.Numerics.Vector3;

namespace ZeusOnlyMode
{
    public partial class ZeusOnlyPlugin
    {
        private const float BallSpeed = 1500f;
        private const float BallLifetime = 2.5f;
        private const float BallCooldown = 1.0f;
        private const float BallHitRadius = 28f;

        // All effects below exist in CS2's pak01 and only rely on control point 0,
        // so they render correctly from a plain info_particle_system.
        private const string FxBallCore = "particles/ambient_fx/ambient_sparks_glow.vpcf";          // looping arcs + flares + particle light
        private const string FxBallGlow = "particles/generic_fx/fx_electricspark_glow.vpcf";        // electric arc sprites + light
        private const string FxBallSparks = "particles/weapons/cs_weapon_fx/weapon_taser_sparks.vpcf";
        private const string FxMuzzleFlash = "particles/weapons/cs_weapon_fx/weapon_muzzle_flash_taser.vpcf";
        private const string FxMuzzleSmokePuff = "particles/unified_weapon_fx/uweapon_muzflsh_gen_large_smoke.vpcf";
        private const string FxMuzzleSmokeTrail = "particles/weapons/cs_weapon_fx/weapon_muzzle_smoke_long.vpcf";
        private const string FxImpactFlash = "particles/weapons/cs_weapon_fx/weapon_taser_flash.vpcf";
        private const string FxImpactFlare = "particles/generic_fx/fx_electricspark_flare.vpcf";
        private const string FxImpactSparks = "particles/explosions_fx/bumpmine_detonate_sparks.vpcf";
        private const string FxImpactRing = "particles/explosions_fx/bumpmine_detonate_ring.vpcf";
        private const string FxImpactArcs = "particles/generic_fx/fx_electric_arc_spark.vpcf";
        private const string FxImpactDebris = "particles/impact_fx/impact_electronic.vpcf";

        private const string BallCarrierModel = "weapons/models/grenade/decoy/weapon_decoy.vmdl";

        private static readonly string[] SuperZeusResources =
        {
            BallCarrierModel,
            FxBallCore, FxBallGlow, FxBallSparks, FxMuzzleFlash, FxMuzzleSmokePuff, FxMuzzleSmokeTrail,
            FxImpactFlash, FxImpactFlare, FxImpactSparks, FxImpactRing, FxImpactArcs, FxImpactDebris
        };

        private static readonly Color ArcCoreColor = Color.FromArgb(255, 210, 240, 255);
        private static readonly Color ArcGlowColor = Color.FromArgb(255, 70, 150, 255);

        private ConVar? friendlyFireCvar;
        private readonly List<LightningBall> lightningBalls = new();
        private readonly Dictionary<int, float> nextBallTime = new();

        private sealed class LightningBall
        {
            public required CHandle<CDecoyProjectile> Projectile;
            public CHandle<CParticleSystem>? Core;
            public CHandle<COmniLight>? Light;
            public readonly List<CHandle<CBeam>> Arcs = new();
            public required int OwnerSlot;
            public required byte OwnerTeam;
            public required Vector3 Velocity;
            public required Vector3 LastPosition;
            public required float ExpireTime;
            public int Age;
        }

        // ------------------------------------------------------------------
        // Super Zeus alt-fire: right-click with the zeus while Super Zeus is
        // on to hurl an insta-kill lightning ball. Toggled by the existing
        // superzeus command (DoSuperToggle); this file only adds the ball.
        // ------------------------------------------------------------------
        private void LoadLightningBalls()
        {
            RegisterListener<Listeners.OnPlayerButtonsChanged>(OnSuperZeusButtons);
            RegisterListener<Listeners.OnTick>(OnSuperZeusTick);
            RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
            {
                foreach (var resource in SuperZeusResources)
                    manifest.AddResource(resource);
            });
            RegisterEventHandler<EventRoundStart>((ev, info) =>
            {
                ClearLightningBalls();
                return HookResult.Continue;
            });
        }

        // Map teardown: entities are unsafe to touch, so just forget them
        private void ResetLightningBalls()
        {
            lightningBalls.Clear();
            nextBallTime.Clear();
            friendlyFireCvar = null;
        }

        private void OnSuperZeusButtons(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
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

            nextBallTime[player.Slot] = now + BallCooldown;
            FireLightningBall(player, pawn, weapon.As<CCSWeaponBase>());
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
                Velocity = velocity,
                LastPosition = spawnPos,
                ExpireTime = Server.CurrentTime + BallLifetime
            };

            var core = SpawnParticle(FxBallCore, spawnPos, angles, 0f);
            if (core != null) ball.Core = HandleOf(core);

            var light = SpawnBallLight(spawnPos);
            if (light != null) ball.Light = HandleOf(light);

            for (int i = 0; i < 4; i++)
            {
                var arc = CreateBeam(spawnPos, spawnPos, i % 2 == 0 ? ArcCoreColor : ArcGlowColor, i % 2 == 0 ? 1.2f : 2.4f);
                if (arc != null) ball.Arcs.Add(HandleOf(arc));
            }

            lightningBalls.Add(ball);

            // Muzzle: flash, a heavy puff, and a smoke wisp that trails off the shooter
            SpawnParticle(FxMuzzleFlash, muzzle, angles, 1f);
            SpawnParticle(FxImpactFlare, muzzle, angles, 1f);
            SpawnParticle(FxMuzzleSmokePuff, muzzle, angles, 3f);
            SpawnParticle(FxMuzzleSmokeTrail, muzzle, angles, 5f, pawn);

            pawn.EmitSound("Weapon_Taser.Single");
            pawn.EmitSound("Weapon_Taser.ChargeReady_Zap");

            KickZeus(zeus);

            // Shove the shooter backwards; a small hop keeps ground friction from eating it
            var push = -forward * 320f;
            if ((pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0)
                push.Z = Math.Max(push.Z, 0f) + 110f;
            var newVelocity = ToVec3(pawn.AbsVelocity) + push;
            pawn.Teleport(null, null, (Vector3?)newVelocity);
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

            var victim = FindVictim(ball, ball.LastPosition, pos);
            if (victim != null)
            {
                StrikeVictim(victim);
                return false;
            }

            if (projectile.Bounces > 0)
            {
                var burst = ImpactBurst(pos);
                burst?.EmitSound("Weapon_Taser.Hit");
                burst?.EmitSound("DoSpark");
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

            // Re-roll the crackling arcs every tick so the ball constantly writhes
            foreach (var arcHandle in ball.Arcs)
            {
                var arc = arcHandle.Value;
                if (arc == null || !arc.IsValid) continue;

                arc.Teleport(pos + RandomUnit() * 3f, Vector3.Zero, Vector3.Zero);
                SetBeamEnd(arc, pos + RandomUnit() * (14f + rng.NextSingle() * 14f), true);
            }

            // Spark and glow puffs left behind in the world form the trail
            if (ball.Age % 4 == 0)
                SpawnParticle(FxBallSparks, pos, angles, 1f);
            if (ball.Age % 6 == 0)
                SpawnParticle(FxBallGlow, pos, angles, 1f);

            ball.LastPosition = pos;
            ball.Age++;
            return true;
        }

        private CCSPlayerController? FindVictim(LightningBall ball, Vector3 from, Vector3 to)
        {
            friendlyFireCvar ??= ConVar.Find("mp_friendlyfire");
            bool friendlyFire = friendlyFireCvar?.GetPrimitiveValue<bool>() ?? false;

            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid || !player.PawnIsAlive || player.Slot == ball.OwnerSlot)
                    continue;

                if (!friendlyFire && player.TeamNum == ball.OwnerTeam)
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

        private void StrikeVictim(CCSPlayerController victim)
        {
            var pawn = victim.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
                return;

            var chest = ToVec3(pawn.AbsOrigin) + new Vector3(0, 0, 40f);
            var sky = chest + new Vector3((rng.NextSingle() - 0.5f) * 120f, (rng.NextSingle() - 0.5f) * 120f, 620f);

            // Two quick strokes with different shapes read as a flickering strike
            DrawLightningBolt(sky, chest, 0.12f);
            AddTimer(0.14f, () => DrawLightningBolt(sky, chest, 0.22f), TimerFlags.STOP_ON_MAPCHANGE);

            ImpactBurst(chest);
            SpawnParticle(FxImpactFlash, sky, Vector3.Zero, 1f);

            pawn.EmitSound("Player.DeathTaser");
            pawn.EmitSound("Flashbang.Explode");
            pawn.CommitSuicide(false, true);
        }

        private CParticleSystem? ImpactBurst(Vector3 pos)
        {
            var flash = SpawnParticle(FxImpactFlash, pos, Vector3.Zero, 2f);
            SpawnParticle(FxImpactFlare, pos, Vector3.Zero, 2f);
            SpawnParticle(FxImpactSparks, pos, Vector3.Zero, 2f);
            SpawnParticle(FxImpactRing, pos, Vector3.Zero, 2f);
            SpawnParticle(FxImpactArcs, pos, Vector3.Zero, 2f);
            SpawnParticle(FxImpactDebris, pos, Vector3.Zero, 2f);
            SpawnParticle(FxBallGlow, pos, Vector3.Zero, 2f);
            return flash;
        }

        private void DrawLightningBolt(Vector3 start, Vector3 end, float lifetime)
        {
            var beams = new List<CHandle<CBeam>>();

            var trunk = JaggedPath(start, end, 12, 26f);
            for (int i = 0; i < trunk.Count - 1; i++)
            {
                AddBeam(beams, trunk[i], trunk[i + 1], ArcGlowColor, 9f);
                AddBeam(beams, trunk[i], trunk[i + 1], ArcCoreColor, 3f);
            }

            for (int b = 0; b < 3; b++)
            {
                var origin = trunk[2 + rng.Next(trunk.Count - 5)];
                var dir = Vector3.Normalize(RandomUnit() + new Vector3(0, 0, -1.2f));
                var branch = JaggedPath(origin, origin + dir * (70f + rng.NextSingle() * 90f), 4, 14f);
                for (int i = 0; i < branch.Count - 1; i++)
                    AddBeam(beams, branch[i], branch[i + 1], ArcGlowColor, 3f);
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

        private static CBeam? CreateBeam(Vector3 start, Vector3 end, Color color, float width)
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null)
                return null;

            beam.Render = color;
            beam.Width = width;
            beam.EndWidth = width;
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

        private static COmniLight? SpawnBallLight(Vector3 pos)
        {
            var light = Utilities.CreateEntityByName<COmniLight>("light_omni2");
            if (light == null)
                return null;

            light.Enabled = true;
            light.DirectLight = 3; // dynamic
            light.CastShadows = 0;
            light.Color = ArcGlowColor;
            light.Brightness = 3f;
            light.Range = 320f;
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
            foreach (var arc in ball.Arcs)
                RemoveEntity(arc);
        }

        private void ClearLightningBalls()
        {
            foreach (var ball in lightningBalls)
                DestroyLightningBall(ball);
            lightningBalls.Clear();
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
