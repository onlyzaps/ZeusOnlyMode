using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Vector3 = System.Numerics.Vector3;

namespace ZeusOnlyMode
{
    public partial class ZeusOnlyPlugin
    {
        // --- Chicken Zeus -----------------------------------------------------
        //
        // While Chicken Zeus is on, right-clicking with the Zeus hurls a live
        // chicken. It rides an invisible decoy (the engine handles gravity and
        // world collision, as with the lightning ball) at a random angle,
        // tumbling end over end. It lands as a normal chicken where it hits a
        // wall or the floor, and an enemy it hits takes ChickenDamage. All of
        // a hurled chicken's sounds, including its own clucking, play at
        // ChickenSoundVolume.

        private const float ChickenCooldown = 0.25f;
        private const float ChickenSpeed = 1100f;
        private const float ChickenLift = 160f;          // extra upward speed, for an arc
        private const float ChickenLifetime = 3f;
        private const float ChickenHitRadius = 10f;
        private const int ChickenDamage = 15;
        private const float ChickenKnockback = 300f;
        private const float ChickenMaxSpin = 720f;       // degrees/s per axis
        private const int MaxLandedChickens = 30;        // oldest are removed past this

        private const float DefaultChickenSoundVolume = 0.2f;
        private float ChickenSoundVolume = DefaultChickenSoundVolume;

        private sealed class FlyingChicken
        {
            public required CHandle<CDecoyProjectile> Carrier;
            public required CHandle<CChicken> Chicken;
            public required int OwnerSlot;
            public required byte OwnerTeam;
            public required Vector3 Angles;
            public required Vector3 Spin;
            public required Vector3 LastPosition;
            public required float LastUpdate;
            public required float ExpireTime;
            public required byte CollisionGroup;
        }

        private readonly List<FlyingChicken> flyingChickens = new();
        private readonly Queue<CHandle<CChicken>> landedChickens = new();

        // Entity indexes of every chicken we hurled that may still be around,
        // so the sound hook can quiet their own sounds too
        private readonly HashSet<int> chickenIndexes = new();

        private void FireChicken(CCSPlayerController player, CCSPlayerPawn pawn, CCSWeaponBase zeus)
        {
            var eye = ToVec3(pawn.AbsOrigin!) + new Vector3(0, 0, pawn.ViewOffset.Z);
            var aim = new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0);
            AngleVectors(aim, out var forward, out _, out _);

            var spawnPos = eye + forward * 32f;
            var velocity = forward * ChickenSpeed + new Vector3(0, 0, ChickenLift);

            var carrier = Utilities.CreateEntityByName<CDecoyProjectile>("decoy_projectile");
            var chicken = Utilities.CreateEntityByName<CChicken>("chicken");
            if (carrier == null || chicken == null)
            {
                Logger.LogError("[SuperZeus] Could not create a chicken for {Name}", player.PlayerName);
                if (carrier != null) carrier.Remove();
                if (chicken != null) chicken.Remove();
                return;
            }

            carrier.TeamNum = pawn.TeamNum;
            carrier.Thrower.Raw = pawn.EntityHandle.Raw;
            carrier.OriginalThrower.Raw = pawn.EntityHandle.Raw;
            carrier.OwnerEntity.Raw = pawn.EntityHandle.Raw;
            carrier.RenderMode = RenderMode_t.kRenderNone;
            SetDebrisCollision(carrier);
            IgnoreOwnerCollision(carrier, pawn);
            carrier.DispatchSpawn();
            carrier.SetModel(BallCarrierModel);
            SetDebrisCollision(carrier);
            IgnoreOwnerCollision(carrier, pawn);
            carrier.Teleport(spawnPos, aim, velocity);

            var angles = new Vector3(rng.NextSingle() * 360f, rng.NextSingle() * 360f, rng.NextSingle() * 360f);
            chicken.DispatchSpawn();
            byte group = chicken.Collision?.CollisionGroup ?? (byte)CollisionGroup.COLLISION_GROUP_NONE;
            SetDebrisCollision(chicken);   // flies through players; only the hit test decides a hit
            chicken.Teleport(spawnPos, angles, Vector3.Zero);
            chickenIndexes.Add((int)chicken.Index);

            float now = Server.CurrentTime;
            flyingChickens.Add(new FlyingChicken
            {
                Carrier = HandleOf(carrier),
                Chicken = HandleOf(chicken),
                OwnerSlot = player.Slot,
                OwnerTeam = pawn.TeamNum,
                Angles = angles,
                Spin = new Vector3(RandomSpin(), RandomSpin(), RandomSpin()),
                LastPosition = spawnPos,
                LastUpdate = now,
                ExpireTime = now + ChickenLifetime,
                CollisionGroup = group
            });

            PlaySound(chicken, "Chicken.Panic", ChickenSoundVolume);
            PlaySound(chicken, "Chicken.FlapWings", ChickenSoundVolume);
            KickZeus(zeus);
            Logger.LogInformation("[SuperZeus] {Name} hurled a chicken", player.PlayerName);
        }

        private float RandomSpin() => (rng.NextSingle() * 2f - 1f) * ChickenMaxSpin;

        private void UpdateChickens()
        {
            float now = Server.CurrentTime;
            for (int i = flyingChickens.Count - 1; i >= 0; i--)
            {
                var flying = flyingChickens[i];
                bool keep;
                try { keep = UpdateChicken(flying, now); }
                catch (Exception ex)
                {
                    Logger.LogError("[SuperZeus] Chicken update failed: {Error}", ex);
                    keep = false;
                }

                if (!keep)
                {
                    LandChicken(flying);
                    flyingChickens.RemoveAt(i);
                }
            }
        }

        // Returns false once the chicken should land
        private bool UpdateChicken(FlyingChicken flying, float now)
        {
            var carrier = flying.Carrier.Value;
            var chicken = flying.Chicken.Value;
            if (chicken == null || !chicken.IsValid)
                return false;
            if (carrier == null || !carrier.IsValid || carrier.AbsOrigin == null)
                return false;

            var pos = ToVec3(carrier.AbsOrigin);
            var velocity = ToVec3(carrier.AbsVelocity);

            var hit = FindHitPlayer(flying.OwnerSlot, ChickenHitRadius, flying.LastPosition, pos + velocity * BallTickLookahead);
            if (hit != null)
            {
                if (hit.TeamNum != flying.OwnerTeam || TeammatesAreEnemies())
                    ChickenHit(hit, flying, velocity);
                chicken.Teleport(pos, null, null);
                return false;
            }

            if (carrier.Bounces > 0 || now >= flying.ExpireTime)
            {
                chicken.Teleport(pos, null, null);
                return false;
            }

            // Tumble along the flight path
            float dt = Math.Max(now - flying.LastUpdate, 0f);
            flying.Angles = Wrap(flying.Angles + flying.Spin * dt);
            chicken.Teleport(pos, flying.Angles, Vector3.Zero);

            flying.LastPosition = pos;
            flying.LastUpdate = now;
            return true;
        }

        private static Vector3 Wrap(Vector3 angles) =>
            new(angles.X % 360f, angles.Y % 360f, angles.Z % 360f);

        private void ChickenHit(CCSPlayerController victim, FlyingChicken flying, Vector3 velocity)
        {
            var pawn = victim.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
                return;

            var flat = new Vector3(velocity.X, velocity.Y, 0f);
            var dir = flat.LengthSquared() < 1f ? Vector3.UnitX : Vector3.Normalize(flat);
            var push = dir * ChickenKnockback + new Vector3(0f, 0f, ChickenKnockback * 0.3f);
            var chest = ToVec3(pawn.AbsOrigin) + new Vector3(0, 0, 40f);
            var shooterPawn = Utilities.GetPlayerFromSlot(flying.OwnerSlot)?.PlayerPawn.Value;

            ApplyBallDamage(pawn, victim.PlayerName, shooterPawn, push, chest, pawn.Health, ChickenDamage);
            if (pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE)
            {
                try { KnockAndStun(pawn, dir); }
                catch (Exception ex) { Logger.LogError("[SuperZeus] Chicken knockback failed for {Name}: {Message}", victim.PlayerName, ex.Message); }
            }

            PlaySound(flying.Chicken.Value, "Chicken.Panic", ChickenSoundVolume);
        }

        // Let the chicken go: upright, solid again, and free to wander
        private void LandChicken(FlyingChicken flying)
        {
            RemoveEntity(flying.Carrier);

            var chicken = flying.Chicken.Value;
            if (chicken == null || !chicken.IsValid)
                return;

            try
            {
                var collision = chicken.Collision;
                if (collision != null)
                {
                    collision.CollisionGroup = flying.CollisionGroup;
                    collision.CollisionAttribute.CollisionGroup = flying.CollisionGroup;
                    Utilities.SetStateChanged(chicken, "CCollisionProperty", "m_CollisionGroup");
                }
                chicken.Teleport(null, new Vector3(0f, flying.Angles.Y, 0f), Vector3.Zero);
                PlaySound(chicken, "Chicken.Idle", ChickenSoundVolume);
            }
            catch (Exception ex)
            {
                Logger.LogError("[SuperZeus] Landing a chicken failed: {Message}", ex.Message);
            }

            landedChickens.Enqueue(flying.Chicken);
            while (landedChickens.Count > MaxLandedChickens)
            {
                var oldest = landedChickens.Dequeue();
                if (oldest.Value is { } old && old.IsValid)
                    chickenIndexes.Remove((int)old.Index);
                RemoveEntity(oldest);
            }
            PruneChickenIndexes();
        }

        // Drop indexes of chickens that are gone (killed, cleaned up)
        private void PruneChickenIndexes()
        {
            var alive = new HashSet<int>();
            foreach (var f in flyingChickens)
                if (f.Chicken.Value is { } c && c.IsValid) alive.Add((int)c.Index);
            foreach (var handle in landedChickens)
                if (handle.Value is { } c && c.IsValid) alive.Add((int)c.Index);
            chickenIndexes.IntersectWith(alive);
        }

        private void ClearChickens()
        {
            foreach (var flying in flyingChickens)
            {
                RemoveEntity(flying.Carrier);
                RemoveEntity(flying.Chicken);
            }
            flyingChickens.Clear();
            while (landedChickens.Count > 0)
                RemoveEntity(landedChickens.Dequeue());
            chickenIndexes.Clear();
        }
    }
}
