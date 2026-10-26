using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Vector3 = System.Numerics.Vector3;

namespace ZeusOnlyMode
{
    public partial class ZeusOnlyPlugin
    {
        // ------------------------------------------------------------------
        // Grapple fire mode: a single line shoots out from the Zeus. Its tip
        // rides an invisible decoy, so the engine handles wall collision as
        // it does for the lightning ball. Nothing moves the shooter until the
        // tip makes contact:
        //  - a wall or floor: the shooter is pulled to that spot;
        //  - an enemy, struck by the tip itself (not the line): that enemy is
        //    killed with the shooter's credit, and the shooter is pulled to
        //    where they died.
        // The pull sets the shooter's velocity every tick, so the engine's own
        // collision applies to them. If they stop making progress (a wall, a
        // ledge, another player in the way) the line lets go, so nobody is
        // pinned to a wall or dragged through one.
        // ------------------------------------------------------------------

        private const float GrappleShotSpeed = 1900f;      // under the carrier's per-axis limit, as with the ball
        private const float GrappleShotLifetime = 0.6f;    // about 1100 units of reach
        private const float GrappleTipRadius = 3f;         // only the tip kills
        private const int GrappleKillDamage = 500;         // lethal through any armor
        private const float GrapplePullSpeed = 1600f;
        private const float GrappleArriveDistance = 56f;   // from the shooter's center to the anchor
        private const float GrappleMaxPullTime = 1.5f;
        private const float GrappleStuckGrace = 0.06f;     // let the pull get going first
        private const float GrappleStuckFraction = 0.35f;  // moving slower than this share of the pull is "stopped"
        private const int GrappleStuckTicks = 3;
        private const float GrappleReleaseKeep = 0.25f;    // share of the pull speed kept on arrival

        private sealed class Grapple
        {
            public required int OwnerSlot;
            public required byte OwnerTeam;
            public required string OwnerName;
            public CHandle<CBeam>? Line;
            public CHandle<CDecoyProjectile>? Tip;
            public required Vector3 Velocity;
            public required Vector3 TipPosition;
            public required float ExpireTime;
            public bool Pulling;
            public Vector3 Anchor;
            public float PullStart;
            public Vector3 LastPawnPos;
            public float LastUpdate;
            public int StuckTicks;
        }

        private readonly List<Grapple> grapples = new();

        // No fall damage from contact, through the pull, until this long after
        // the line lets go, by player slot
        private const float GrappleNoFallDamage = 3f;
        private readonly Dictionary<int, float> noFallDamageUntil = new();

        // Called from the damage hook: true blocks this fall damage
        private bool FallProtected(CBaseEntity entity, CTakeDamageInfo info)
        {
            if (noFallDamageUntil.Count == 0 || (info.BitsDamageType & DamageTypes_t.DMG_FALL) == 0 || entity.DesignerName != "player")
                return false;

            var controller = entity.As<CCSPlayerPawn>().Controller.Value?.As<CCSPlayerController>();
            if (controller == null || !controller.IsValid || !noFallDamageUntil.TryGetValue(controller.Slot, out var until))
                return false;

            if (Server.CurrentTime > until)
            {
                noFallDamageUntil.Remove(controller.Slot);
                return false;
            }

            info.Damage = 0;
            return true;
        }

        private void FireGrapple(CCSPlayerController player, CCSPlayerPawn pawn, CCSWeaponBase zeus)
        {
            CancelGrapple(player.Slot);

            var eye = ToVec3(pawn.AbsOrigin!) + new Vector3(0, 0, pawn.ViewOffset.Z);
            AngleVectors(new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0), out var forward, out _, out _);
            var spawnPos = eye + forward * 24f;
            var velocity = forward * GrappleShotSpeed;

            var tip = CreateCarrier(pawn, pawn.TeamNum, spawnPos, velocity);
            if (tip == null)
            {
                Logger.LogError("[SuperZeus] Could not create the grapple's carrier; no grapple for {Name}", player.PlayerName);
                return;
            }

            var color = Whiten(BoltGlowColor(GetZeusColor(player)), 0.3f);
            var line = CreateBeam(HandPosition(pawn), spawnPos, color, 1.2f, 1.2f);

            grapples.Add(new Grapple
            {
                OwnerSlot = player.Slot,
                OwnerTeam = pawn.TeamNum,
                OwnerName = player.PlayerName,
                Line = line != null ? HandleOf(line) : null,
                Tip = HandleOf(tip),
                Velocity = velocity,
                TipPosition = spawnPos,
                ExpireTime = Server.CurrentTime + GrappleShotLifetime
            });

            PlaySound(pawn, "Weapon_Taser.Single");
            KickZeus(zeus);
        }

        // Where the line leaves the Zeus
        private static Vector3 HandPosition(CCSPlayerPawn pawn)
        {
            var eye = ToVec3(pawn.AbsOrigin!) + new Vector3(0, 0, pawn.ViewOffset.Z);
            AngleVectors(new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0), out var forward, out var right, out var up);
            return eye + forward * 16f + right * (pawn.LeftHanded ? -5f : 5f) - up * 6f;
        }

        private void UpdateGrapples()
        {
            for (int i = grapples.Count - 1; i >= 0; i--)
            {
                var grapple = grapples[i];
                bool keep;
                try { keep = UpdateGrapple(grapple); }
                catch (Exception ex)
                {
                    Logger.LogError("[SuperZeus] Grapple update failed: {Error}", ex);
                    keep = false;
                }

                if (!keep)
                {
                    EndGrapple(grapple);
                    grapples.RemoveAt(i);
                }
            }
        }

        // Returns false once the grapple is done
        private bool UpdateGrapple(Grapple grapple)
        {
            var player = Utilities.GetPlayerFromSlot(grapple.OwnerSlot);
            var pawn = player?.PlayerPawn.Value;
            if (player == null || !player.IsValid || !player.PawnIsAlive || pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
                return false;

            float now = Server.CurrentTime;
            var hand = HandPosition(pawn);

            if (!grapple.Pulling)
            {
                var tip = grapple.Tip?.Value;
                if (tip == null || !tip.IsValid || tip.AbsOrigin == null)
                    return false;

                var pos = ToVec3(tip.AbsOrigin);
                var hit = FindHitPlayer(grapple.OwnerSlot, GrappleTipRadius, grapple.TipPosition, pos + grapple.Velocity * BallTickLookahead);
                if (hit != null && (hit.TeamNum != grapple.OwnerTeam || TeammatesAreEnemies()))
                {
                    StartPull(grapple, pawn, GrappleKill(hit, pawn, grapple) ?? pos, now);
                }
                else if (tip.Bounces > 0)
                {
                    var flash = SpawnParticle(FxImpactFlare, pos, Vector3.Zero, 1f);
                    PlaySound(flash, "DoSpark");
                    StartPull(grapple, pawn, pos, now);
                }
                else if (now >= grapple.ExpireTime)
                {
                    return false;   // reached nothing
                }
                else
                {
                    // Dead straight, regardless of how the decoy simulates gravity
                    tip.Teleport(null, null, (Vector3?)grapple.Velocity);
                    grapple.TipPosition = pos;
                }

                MoveBallBeam(grapple.Line, hand, grapple.Pulling ? grapple.Anchor : pos);
                return true;
            }

            var origin = ToVec3(pawn.AbsOrigin);
            var toAnchor = grapple.Anchor - (origin + new Vector3(0, 0, PlayerHullHeight * 0.5f));
            float distance = toAnchor.Length();
            if (distance <= GrappleArriveDistance)
            {
                pawn.Teleport(null, null, (Vector3?)(Vector3.Normalize(toAnchor) * GrapplePullSpeed * GrappleReleaseKeep));
                return false;
            }

            if (now - grapple.PullStart >= GrappleMaxPullTime)
                return false;

            float dt = now - grapple.LastUpdate;
            if (now - grapple.PullStart > GrappleStuckGrace && dt > 0f)
            {
                float moved = Vector3.Distance(origin, grapple.LastPawnPos);
                grapple.StuckTicks = moved < GrapplePullSpeed * dt * GrappleStuckFraction ? grapple.StuckTicks + 1 : 0;
                if (grapple.StuckTicks >= GrappleStuckTicks)
                {
                    pawn.Teleport(null, null, (Vector3?)Vector3.Zero);
                    return false;
                }
            }

            pawn.Teleport(null, null, (Vector3?)(toAnchor / distance * GrapplePullSpeed));
            grapple.LastPawnPos = origin;
            grapple.LastUpdate = now;
            MoveBallBeam(grapple.Line, hand, grapple.Anchor);
            return true;
        }

        // Contact: drop the tip, anchor the line and start pulling
        private void StartPull(Grapple grapple, CCSPlayerPawn pawn, Vector3 anchor, float now)
        {
            if (grapple.Tip != null)
                RemoveEntity(grapple.Tip);
            grapple.Tip = null;

            var origin = ToVec3(pawn.AbsOrigin!);
            noFallDamageUntil[grapple.OwnerSlot] = float.MaxValue;   // until the line lets go
            grapple.Pulling = true;
            grapple.Anchor = anchor;
            grapple.PullStart = now;
            grapple.LastUpdate = now;
            grapple.LastPawnPos = origin;

            var toAnchor = anchor - (origin + new Vector3(0, 0, PlayerHullHeight * 0.5f));
            if (toAnchor.LengthSquared() > 1f)
                pawn.Teleport(null, null, (Vector3?)(Vector3.Normalize(toAnchor) * GrapplePullSpeed));
        }

        // The tip struck an enemy: kill them with the shooter's credit and
        // return where they died (null if they couldn't be found)
        private Vector3? GrappleKill(CCSPlayerController victim, CCSPlayerPawn shooterPawn, Grapple grapple)
        {
            var pawn = victim.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
                return null;

            var chest = ToVec3(pawn.AbsOrigin) + new Vector3(0, 0, 40f);
            var push = Vector3.Normalize(grapple.Velocity) * 300f;
            ApplyBallDamage(pawn, victim.PlayerName, shooterPawn, push, chest, pawn.Health, GrappleKillDamage);

            var burst = ImpactBurst(chest);
            PlaySound(burst, "Weapon_Taser.Hit");
            return chest;
        }

        private void EndGrapple(Grapple grapple)
        {
            if (grapple.Pulling)
                noFallDamageUntil[grapple.OwnerSlot] = Server.CurrentTime + GrappleNoFallDamage;
            if (grapple.Tip != null)
                RemoveEntity(grapple.Tip);
            if (grapple.Line != null)
                RemoveEntity(grapple.Line);
        }

        private void CancelGrapple(int slot)
        {
            for (int i = grapples.Count - 1; i >= 0; i--)
            {
                if (grapples[i].OwnerSlot != slot)
                    continue;
                EndGrapple(grapples[i]);
                grapples.RemoveAt(i);
            }
        }

        private void ClearGrapples()
        {
            foreach (var grapple in grapples)
                EndGrapple(grapple);
            grapples.Clear();
        }
    }
}
