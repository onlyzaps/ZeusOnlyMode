using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Drawing;
using Vector3 = System.Numerics.Vector3;

namespace ZeusOnlyMode
{
    public partial class ZeusOnlyPlugin
    {
        // ------------------------------------------------------------------
        // Camera fire mode: right-click shoots a little webcam onto a wall
        // (one per life; a shot that hits nothing is refunded). After that,
        // right-click switches the player's view to the camera and back. The
        // view starts looking out from the wall, 45 degrees down, and pans
        // with the mouse. The view is an invisible entity the player's camera
        // services are pointed at (the way third-person plugins do it); the
        // webcam itself is drawn from beams, its lens following the view and
        // its light turning red while someone is watching. The camera goes
        // away when its owner dies.
        // ------------------------------------------------------------------

        private const float CameraShotLifetime = 1.5f;   // about 2800 units of reach
        private const float CameraDefaultPitch = 45f;    // looking down
        private const float CameraFloorPitch = -20f;     // a floor camera looks up a little
        private const float CameraMaxPanFromWall = 80f;  // a wall camera never looks into its own wall
        private const float CameraStandOff = 10f;        // the webcam's center, out from the wall

        private static readonly Color CameraBodyColor = Color.FromArgb(255, 120, 125, 135);
        private static readonly Color CameraLedOn = Color.FromArgb(255, 255, 30, 30);
        private static readonly Color CameraLedOff = Color.FromArgb(255, 70, 10, 10);

        private sealed class WallCamera
        {
            public required int OwnerSlot;
            public required ZeusColorOption? Color;
            public required float ShooterYaw;
            public CHandle<CDecoyProjectile>? Tip;
            public Vector3 Velocity;
            public Vector3 TipPosition;
            public float ExpireTime;

            public bool Placed;
            public Vector3 Anchor;
            public Vector3 Normal;
            public bool OnWall;
            public float OutwardYaw;
            public Vector3 Angles;                     // where the camera looks (pitch, yaw, 0)
            public CHandle<CDynamicProp>? View;
            public readonly List<CHandle<CBeam>> Parts = new();   // stem, body, cross, lens, light

            public bool Viewing;
            public Vector3 EyeAtToggle;
            public Vector3 AnglesAtToggle;
        }

        private readonly Dictionary<int, WallCamera> wallCameras = new();
        private readonly HashSet<int> cameraUsedThisLife = new();

        private void LoadCameras()
        {
            RegisterEventHandler<EventPlayerDeath>((ev, info) =>
            {
                if (ev.Userid != null && ev.Userid.IsValid)
                    RemoveCamera(ev.Userid.Slot);
                return HookResult.Continue;
            });
            RegisterEventHandler<EventPlayerSpawn>((ev, info) =>
            {
                if (ev.Userid != null && ev.Userid.IsValid)
                    cameraUsedThisLife.Remove(ev.Userid.Slot);
                return HookResult.Continue;
            });
            RegisterListener<Listeners.OnClientDisconnect>(slot =>
            {
                RemoveCamera(slot);
                cameraUsedThisLife.Remove(slot);
            });
        }

        private bool IsViewingCamera(int slot) => wallCameras.TryGetValue(slot, out var camera) && camera.Viewing;

        // Right-click in Camera mode: shoot the camera, or switch to its view
        private void OnCameraClick(CCSPlayerController player, CCSPlayerPawn pawn, CCSWeaponBase zeus)
        {
            if (wallCameras.TryGetValue(player.Slot, out var camera))
            {
                if (camera.Placed)
                    StartCameraView(player, pawn, camera);
                return;   // still flying
            }

            if (cameraUsedThisLife.Contains(player.Slot))
            {
                player.PrintToCenter("You get one camera per life");
                return;
            }

            FireCamera(player, pawn, zeus);
        }

        private void FireCamera(CCSPlayerController player, CCSPlayerPawn pawn, CCSWeaponBase zeus)
        {
            var eye = ToVec3(pawn.AbsOrigin!) + new Vector3(0, 0, pawn.ViewOffset.Z);
            AngleVectors(new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0), out var forward, out _, out _);
            var spawnPos = eye + forward * 24f;
            var velocity = forward * GrappleShotSpeed;

            var tip = CreateCarrier(pawn, pawn.TeamNum, spawnPos, velocity);
            if (tip == null)
            {
                Logger.LogError("[SuperZeus] Could not create the camera's carrier; no camera for {Name}", player.PlayerName);
                return;
            }

            wallCameras[player.Slot] = new WallCamera
            {
                OwnerSlot = player.Slot,
                Color = GetZeusColor(player),
                ShooterYaw = pawn.EyeAngles.Y,
                Tip = HandleOf(tip),
                Velocity = velocity,
                TipPosition = spawnPos,
                ExpireTime = Server.CurrentTime + CameraShotLifetime
            };
            cameraUsedThisLife.Add(player.Slot);

            PlaySound(pawn, "Weapon_Taser.Single");
            KickZeus(zeus);
            Logger.LogInformation("[SuperZeus] {Name} shot a camera", player.PlayerName);
        }

        private void UpdateCameras()
        {
            foreach (var slot in wallCameras.Keys.ToList())
            {
                bool keep;
                try { keep = UpdateCamera(wallCameras[slot]); }
                catch (Exception ex)
                {
                    Logger.LogError("[SuperZeus] Camera update failed: {Error}", ex);
                    keep = false;
                }

                if (!keep)
                    RemoveCamera(slot);
            }
        }

        // Returns false once the camera should go
        private bool UpdateCamera(WallCamera camera)
        {
            var player = Utilities.GetPlayerFromSlot(camera.OwnerSlot);
            var pawn = player?.PlayerPawn.Value;
            if (player == null || !player.IsValid || !player.PawnIsAlive || pawn == null || !pawn.IsValid)
                return false;

            if (!camera.Placed)
            {
                var tip = camera.Tip?.Value;
                if (tip == null || !tip.IsValid || tip.AbsOrigin == null)
                    return false;

                var pos = ToVec3(tip.AbsOrigin);
                if (tip.Bounces > 0)
                {
                    PlaceCamera(camera, player, pos, SurfaceNormal(camera.Velocity, tip));
                    return true;
                }

                if (Server.CurrentTime >= camera.ExpireTime)
                {
                    cameraUsedThisLife.Remove(camera.OwnerSlot);   // it hit nothing: try again
                    player.PrintToCenter("The camera hit nothing - shoot it at a wall");
                    return false;
                }

                tip.Teleport(null, null, (Vector3?)camera.Velocity);
                camera.TipPosition = pos;
                return true;
            }

            if (camera.Viewing)
            {
                // Pan by however far the player has turned since switching to the camera
                float pitch = Math.Clamp(camera.AnglesAtToggle.X + (pawn.EyeAngles.X - camera.EyeAtToggle.X), -89f, 89f);
                float yaw = camera.AnglesAtToggle.Y + WrapDegrees(pawn.EyeAngles.Y - camera.EyeAtToggle.Y);
                if (camera.OnWall)
                    yaw = camera.OutwardYaw + Math.Clamp(WrapDegrees(yaw - camera.OutwardYaw), -CameraMaxPanFromWall, CameraMaxPanFromWall);

                camera.Angles = new Vector3(pitch, yaw, 0f);
                AimCamera(camera);
            }
            return true;
        }

        private static float WrapDegrees(float angle)
        {
            angle %= 360f;
            return angle > 180f ? angle - 360f : angle < -180f ? angle + 360f : angle;
        }

        private void PlaceCamera(WallCamera camera, CCSPlayerController player, Vector3 pos, Vector3 normal)
        {
            if (camera.Tip != null)
                RemoveEntity(camera.Tip);
            camera.Tip = null;

            camera.Placed = true;
            camera.Anchor = pos;
            camera.Normal = normal;
            camera.OnWall = MathF.Abs(normal.Z) < 0.7f;
            camera.OutwardYaw = MathF.Atan2(normal.Y, normal.X) * 180f / MathF.PI;

            // Out from a wall, 45 degrees down; on a floor or ceiling, back
            // toward where it was shot from
            if (camera.OnWall)
                camera.Angles = new Vector3(CameraDefaultPitch, camera.OutwardYaw, 0f);
            else
                camera.Angles = new Vector3(normal.Z > 0f ? CameraFloorPitch : CameraDefaultPitch, camera.ShooterYaw + 180f, 0f);

            var view = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
            if (view != null)
            {
                view.DispatchSpawn();
                view.SetModel(BallCarrierModel);   // a model keeps it networked; it's never drawn
                view.RenderMode = RenderMode_t.kRenderTransAlpha;
                view.Render = Color.FromArgb(0, 255, 255, 255);
                Utilities.SetStateChanged(view, "CBaseModelEntity", "m_nRenderMode");
                Utilities.SetStateChanged(view, "CBaseModelEntity", "m_clrRender");
                SetDebrisCollision(view);
                camera.View = HandleOf(view);
            }

            var lens = Whiten(BoltGlowColor(camera.Color), 0.5f);
            AddCameraPart(camera, pos, CameraBodyColor, 1.2f);   // stem
            AddCameraPart(camera, pos, CameraBodyColor, 6f);     // body
            AddCameraPart(camera, pos, CameraBodyColor, 5f);     // cross
            AddCameraPart(camera, pos, lens, 3.5f);              // lens
            AddCameraPart(camera, pos, CameraLedOff, 1.2f);      // light
            AimCamera(camera);

            var flash = SpawnParticle(FxImpactFlare, pos, Vector3.Zero, 1f);
            PlaySound(flash, "DoSpark");
            player.PrintToCenter("Camera placed - right-click to watch it");
            Logger.LogInformation("[SuperZeus] {Name}'s camera stuck to a {Surface}", player.PlayerName,
                camera.OnWall ? "wall" : normal.Z > 0f ? "floor" : "ceiling");
        }

        private static void AddCameraPart(WallCamera camera, Vector3 at, Color color, float width)
        {
            var beam = CreateBeam(at, at, color, width, width);
            if (beam != null) camera.Parts.Add(HandleOf(beam));
        }

        // Lays out the webcam and the view along where the camera looks
        private void AimCamera(WallCamera camera)
        {
            AngleVectors(camera.Angles, out var look, out var right, out var up);
            var center = camera.Anchor + camera.Normal * CameraStandOff;

            var points = new (Vector3 A, Vector3 B)[]
            {
                (camera.Anchor, camera.Anchor + camera.Normal * (CameraStandOff - 3f)),   // stem
                (center - look * 4f, center + look * 4f),                                 // body
                (center - right * 3f, center + right * 3f),                               // cross
                (center + look * 4f, center + look * 5.5f),                               // lens
                (center + up * 3.2f + look * 2.5f, center + up * 3.2f + look * 3.2f),     // light
            };
            for (int i = 0; i < camera.Parts.Count && i < points.Length; i++)
                MoveBallBeam(camera.Parts[i], points[i].A, points[i].B);

            if (camera.Parts.Count == points.Length && camera.Parts[^1].Value is { } light && light.IsValid)
            {
                light.Render = camera.Viewing ? CameraLedOn : CameraLedOff;
                Utilities.SetStateChanged(light, "CBaseModelEntity", "m_clrRender");
            }

            var view = camera.View?.Value;
            if (view != null && view.IsValid)
                view.Teleport(center + look * 7f, camera.Angles, Vector3.Zero);
        }

        private void StartCameraView(CCSPlayerController player, CCSPlayerPawn pawn, WallCamera camera)
        {
            var view = camera.View?.Value;
            var services = pawn.CameraServices;
            if (view == null || !view.IsValid || services == null)
                return;

            services.ViewEntity.Raw = view.EntityHandle.Raw;
            Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pCameraServices");

            camera.Viewing = true;
            camera.EyeAtToggle = new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0f);
            camera.AnglesAtToggle = camera.Angles;
            AimCamera(camera);
            player.PrintToCenter("Watching your camera - move the mouse to pan, right-click to return");
        }

        // Back to the player's own eyes (the camera keeps where it was panned to)
        private void StopCameraView(WallCamera camera)
        {
            camera.Viewing = false;
            var pawn = Utilities.GetPlayerFromSlot(camera.OwnerSlot)?.PlayerPawn.Value;
            if (pawn != null && pawn.IsValid && pawn.CameraServices is { } services)
            {
                services.ViewEntity.Raw = uint.MaxValue;
                Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pCameraServices");
            }
            if (camera.Placed)
                AimCamera(camera);
        }

        // Right-click while watching, with anything in hand, comes back
        private void StopCameraView(int slot)
        {
            if (wallCameras.TryGetValue(slot, out var camera) && camera.Viewing)
                StopCameraView(camera);
        }

        private void RemoveCamera(int slot)
        {
            if (!wallCameras.Remove(slot, out var camera))
                return;

            if (camera.Viewing)
            {
                try { StopCameraView(camera); }
                catch (Exception ex) { Logger.LogError("[SuperZeus] Restoring a camera viewer failed: {Message}", ex.Message); }
            }
            if (camera.Tip != null) RemoveEntity(camera.Tip);
            if (camera.View != null) RemoveEntity(camera.View);
            foreach (var part in camera.Parts) RemoveEntity(part);
        }

        private void ClearCameras()
        {
            foreach (var slot in wallCameras.Keys.ToList())
                RemoveCamera(slot);
        }
    }
}
