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
        private const float CameraArmLength = 8f;        // the head's pivot, out from the wall
        private const float CameraViewAhead = 16f;       // the view sits just past the lens
        private const float CameraBlinkPeriod = 1f;      // the light blinks while nobody watches

        private static readonly Color CameraLedOn = Color.FromArgb(255, 255, 30, 30);
        private static readonly Color CameraLedOff = Color.FromArgb(255, 60, 8, 8);

        // --- The webcam model -------------------------------------------------
        //
        // Neon line segments in the player's color. Plate and Arm are laid on
        // the surface (x = out of it, y = across, z = up it). Everything else
        // is the head, in the camera's own frame (x = where it looks, y = right,
        // z = up) from the pivot at the end of the arm, so it turns with the
        // view: a wireframe housing with a soft glowing core, a round lens with
        // a glint, a red light on top, and faint sightlines fanning out to
        // show where it's looking (hidden while its owner watches through it).

        private enum CameraPartKind { Plate, Arm, Edge, Core, Lens, Glint, Light, Sightline }

        private sealed record CameraPart(CameraPartKind Kind, Vector3 A, Vector3 B);

        private static readonly CameraPart[] CameraModel = BuildCameraModel();

        private static CameraPart[] BuildCameraModel()
        {
            var parts = new List<CameraPart>();

            // Mounting plate and arm
            const float plate = 6f;
            var corners = new[] { new Vector3(0.5f, -plate, -plate), new Vector3(0.5f, plate, -plate), new Vector3(0.5f, plate, plate), new Vector3(0.5f, -plate, plate) };
            for (int i = 0; i < 4; i++)
                parts.Add(new(CameraPartKind.Plate, corners[i], corners[(i + 1) % 4]));
            parts.Add(new(CameraPartKind.Arm, new Vector3(0.5f, 0f, 0f), new Vector3(CameraArmLength, 0f, 0f)));

            // Housing: a box from just behind the pivot to the lens
            const float back = -3f, front = 11f, half = 4f;
            Vector3 Box(float x, int sy, int sz) => new(x, sy * half, sz * half);
            foreach (var (sy, sz) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                parts.Add(new(CameraPartKind.Edge, Box(back, sy, sz), Box(front, sy, sz)));
            foreach (var x in new[] { back, front })
            {
                parts.Add(new(CameraPartKind.Edge, Box(x, -1, -1), Box(x, 1, -1)));
                parts.Add(new(CameraPartKind.Edge, Box(x, 1, -1), Box(x, 1, 1)));
                parts.Add(new(CameraPartKind.Edge, Box(x, 1, 1), Box(x, -1, 1)));
                parts.Add(new(CameraPartKind.Edge, Box(x, -1, 1), Box(x, -1, -1)));
            }
            parts.Add(new(CameraPartKind.Core, new Vector3(back + 1f, 0f, 0f), new Vector3(front - 1f, 0f, 0f)));

            // Lens ring and glint
            const int ring = 10;
            const float lensX = front + 0.5f, lensRadius = 3f;
            for (int i = 0; i < ring; i++)
            {
                float a0 = i / (float)ring * MathF.PI * 2f, a1 = (i + 1) / (float)ring * MathF.PI * 2f;
                parts.Add(new(CameraPartKind.Lens,
                    new Vector3(lensX, MathF.Cos(a0) * lensRadius, MathF.Sin(a0) * lensRadius),
                    new Vector3(lensX, MathF.Cos(a1) * lensRadius, MathF.Sin(a1) * lensRadius)));
            }
            parts.Add(new(CameraPartKind.Glint, new Vector3(lensX, 0f, 0f), new Vector3(lensX + 1f, 0f, 0f)));

            // Recording light on top
            parts.Add(new(CameraPartKind.Light, new Vector3(6f, 0f, half + 1.3f), new Vector3(6.9f, 0f, half + 1.3f)));

            // Sightlines: the corners of its view, fading out
            const float reach = 60f;
            float wide = reach * MathF.Tan(22f * MathF.PI / 180f), tall = reach * MathF.Tan(14f * MathF.PI / 180f);
            foreach (var (sy, sz) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                parts.Add(new(CameraPartKind.Sightline, new Vector3(lensX + 1f, sy * 1.5f, sz * 1.5f), new Vector3(lensX + reach, sy * wide, sz * tall)));

            return parts.ToArray();
        }

        // Color and width (start, end) of each kind of part
        private static (Color Color, float Width, float EndWidth) CameraPartStyle(CameraPartKind kind, ZeusColorOption? color)
        {
            var accent = BoltGlowColor(color);
            return kind switch
            {
                CameraPartKind.Plate => (Scale(accent, 0.6f), 0.8f, 0.8f),
                CameraPartKind.Arm => (Scale(Whiten(accent, 0.3f), 0.7f), 2.5f, 2.5f),
                CameraPartKind.Edge => (Whiten(accent, 0.55f), 0.7f, 0.7f),
                CameraPartKind.Core => (Scale(accent, 0.3f), 7f, 7f),
                CameraPartKind.Lens => (Whiten(accent, 0.2f), 1f, 1f),
                CameraPartKind.Glint => (Color.White, 2.5f, 2.5f),
                CameraPartKind.Light => (CameraLedOff, 1.8f, 1.8f),
                _ => (Scale(accent, 0.35f), 0.4f, 0.05f)
            };
        }

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
            public readonly List<CHandle<CBeam>?> Parts = new();   // one per CameraModel part
            public Vector3 AimedAngles = new(float.NaN, float.NaN, 0f);
            public bool LightLit;

            public bool Viewing;
            public Vector3 EyeAtToggle;
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
                // The camera looks wherever the player looks. Their view was
                // snapped to the camera when they started watching, and while
                // watching their view angles follow the camera, so adding the
                // change on top each tick fed back into a runaway spin; taking
                // the angles as they are is stable either way.
                float pitch = Math.Clamp(pawn.EyeAngles.X, -89f, 89f);
                float yaw = pawn.EyeAngles.Y;
                if (camera.OnWall)
                    yaw = camera.OutwardYaw + Math.Clamp(WrapDegrees(yaw - camera.OutwardYaw), -CameraMaxPanFromWall, CameraMaxPanFromWall);

                camera.Angles = new Vector3(pitch, yaw, 0f);
                if (Vector3.DistanceSquared(camera.Angles, camera.AimedAngles) > 0.01f)
                    AimCamera(camera, mount: false);
            }

            bool lit = camera.Viewing || Server.CurrentTime % CameraBlinkPeriod < CameraBlinkPeriod * 0.5f;
            if (lit != camera.LightLit)
            {
                camera.LightLit = lit;
                SetCameraLight(camera);
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

            foreach (var part in CameraModel)
            {
                var (color, width, endWidth) = CameraPartStyle(part.Kind, camera.Color);
                var beam = CreateBeam(pos, pos, color, width, endWidth);
                camera.Parts.Add(beam != null ? HandleOf(beam) : null);
            }
            AimCamera(camera);

            var flash = SpawnParticle(FxImpactFlare, pos, Vector3.Zero, 1f);
            PlaySound(flash, "DoSpark");
            player.PrintToCenter("Camera placed - right-click to watch it");
            Logger.LogInformation("[SuperZeus] {Name}'s camera stuck to a {Surface}", player.PlayerName,
                camera.OnWall ? "wall" : normal.Z > 0f ? "floor" : "ceiling");
        }

        // Lays out the webcam along where the camera looks, and puts the view
        // just past its lens. The plate and arm only need placing once.
        private void AimCamera(WallCamera camera, bool mount = true)
        {
            AngleVectors(camera.Angles, out var look, out var right, out var up);
            var normal = camera.Normal;
            var across = MathF.Abs(normal.Z) < 0.95f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, normal)) : Vector3.UnitX;
            var wallUp = Vector3.Cross(normal, across);
            var pivot = camera.Anchor + normal * CameraArmLength;

            for (int i = 0; i < camera.Parts.Count && i < CameraModel.Length; i++)
            {
                var part = CameraModel[i];
                if (part.Kind is CameraPartKind.Plate or CameraPartKind.Arm)
                {
                    if (mount)
                        MoveBallBeam(camera.Parts[i],
                            camera.Anchor + normal * part.A.X + across * part.A.Y + wallUp * part.A.Z,
                            camera.Anchor + normal * part.B.X + across * part.B.Y + wallUp * part.B.Z);
                    continue;
                }

                if (part.Kind == CameraPartKind.Sightline && camera.Viewing)
                {
                    MoveBallBeam(camera.Parts[i], pivot, pivot);   // out of the owner's view
                    continue;
                }

                MoveBallBeam(camera.Parts[i],
                    pivot + look * part.A.X + right * part.A.Y + up * part.A.Z,
                    pivot + look * part.B.X + right * part.B.Y + up * part.B.Z);
            }

            camera.AimedAngles = camera.Angles;
            var view = camera.View?.Value;
            if (view != null && view.IsValid)
                view.Teleport(pivot + look * CameraViewAhead, camera.Angles, Vector3.Zero);
        }

        private static void SetCameraLight(WallCamera camera)
        {
            int index = Array.FindIndex(CameraModel, p => p.Kind == CameraPartKind.Light);
            if (index < 0 || index >= camera.Parts.Count || camera.Parts[index]?.Value is not { } light || !light.IsValid)
                return;

            light.Render = camera.LightLit ? CameraLedOn : CameraLedOff;
            Utilities.SetStateChanged(light, "CBaseModelEntity", "m_clrRender");
        }

        private void StartCameraView(CCSPlayerController player, CCSPlayerPawn pawn, WallCamera camera)
        {
            var view = camera.View?.Value;
            var services = pawn.CameraServices;
            if (view == null || !view.IsValid || services == null)
                return;

            services.ViewEntity.Raw = view.EntityHandle.Raw;
            Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pCameraServices");

            // Remember where they were looking, then turn them to the camera's
            // angle so the view picks up exactly where the camera points
            camera.Viewing = true;
            camera.EyeAtToggle = new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0f);
            pawn.Teleport(null, camera.Angles, null);
            AimCamera(camera);

            var pawnHandle = HandleOf(pawn);
            Server.NextFrame(() =>
            {
                if (pawnHandle.Value is { } p && p.IsValid)
                    Logger.LogInformation("[SuperZeus] {Name} watching: view ({VP:0}, {VY:0}), camera ({CP:0}, {CY:0})",
                        player.PlayerName, p.EyeAngles.X, p.EyeAngles.Y, camera.Angles.X, camera.Angles.Y);
            });
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
                pawn.Teleport(null, camera.EyeAtToggle, null);   // facing where they were before
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
            foreach (var part in camera.Parts)
                if (part != null) RemoveEntity(part);
        }

        private void ClearCameras()
        {
            foreach (var slot in wallCameras.Keys.ToList())
                RemoveCamera(slot);
        }
    }
}
