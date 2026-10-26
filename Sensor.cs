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
        // Sensor fire mode: right-click shoots a probe that sticks into the
        // ground, or whatever it hits. Planted, it stands out of the surface
        // and flashes blue. When an enemy comes near it flashes red, and the
        // owner's team hears an alarm and gets a chat line saying where. One
        // probe per player per round (a shot that hits nothing is refunded);
        // it stays until the round ends, even if its owner dies.
        // ------------------------------------------------------------------

        private const float SensorShotLifetime = 1.5f;      // about 2800 units of reach
        private const float SensorRange = 160f;             // across the ground
        private const float SensorHeightRange = 120f;       // up or down
        private const float SensorAlertCooldown = 2.5f;     // between alarms while an enemy stays near
        private const float SensorAlarmVolume = 0.8f;
        private const string SensorAlarmSound = "Alert.WarmupTimeoutBeep";
        private const float SensorBlinkPeriod = 1.2f;       // calm: a short blue flash this often
        private const float SensorBlinkOn = 0.15f;
        private const float SensorAlertBlink = 0.2f;        // alerting: red on / off this fast

        private static readonly Color SensorShaftColor = Color.FromArgb(255, 110, 120, 135);
        private static readonly Color SensorBlue = Color.FromArgb(255, 40, 140, 255);
        private static readonly Color SensorRed = Color.FromArgb(255, 255, 30, 30);
        private static readonly Color SensorDark = Color.FromArgb(255, 10, 20, 40);

        private sealed class Sensor
        {
            public required int OwnerSlot;
            public required byte OwnerTeam;
            public required string OwnerName;
            public CHandle<CDecoyProjectile>? Tip;
            public Vector3 Velocity;
            public float ExpireTime;
            public CHandle<CBeam>? Dart;                     // the probe in flight

            public bool Planted;
            public float PlantedAt;
            public Vector3 Anchor;
            public Vector3 Normal;
            public readonly List<CHandle<CBeam>> Body = new();   // shaft, fins
            public CHandle<CBeam>? Light;
            public float NextAlert;
            public float AlertUntil;
            public Color? LightShown;
        }

        private readonly List<Sensor> sensors = new();
        private readonly HashSet<int> sensorUsedThisRound = new();

        // Right-click in Sensor mode: one probe a round
        private void OnSensorClick(CCSPlayerController player, CCSPlayerPawn pawn, CCSWeaponBase zeus)
        {
            if (sensorUsedThisRound.Contains(player.Slot))
            {
                player.PrintToCenter("You get one probe per round");
                return;
            }

            sensorUsedThisRound.Add(player.Slot);
            FireSensor(player, pawn, zeus);
        }

        private void FireSensor(CCSPlayerController player, CCSPlayerPawn pawn, CCSWeaponBase zeus)
        {
            var eye = ToVec3(pawn.AbsOrigin!) + new Vector3(0, 0, pawn.ViewOffset.Z);
            AngleVectors(new Vector3(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0), out var forward, out _, out _);
            var spawnPos = eye + forward * 24f;
            var velocity = forward * GrappleShotSpeed;

            var tip = CreateCarrier(pawn, pawn.TeamNum, spawnPos, velocity);
            if (tip == null)
            {
                Logger.LogError("[SuperZeus] Could not create the probe's carrier; no probe for {Name}", player.PlayerName);
                return;
            }

            var dart = CreateBeam(spawnPos, spawnPos, SensorBlue, 1.5f, 0.3f);
            sensors.Add(new Sensor
            {
                OwnerSlot = player.Slot,
                OwnerTeam = pawn.TeamNum,
                OwnerName = player.PlayerName,
                Tip = HandleOf(tip),
                Velocity = velocity,
                ExpireTime = Server.CurrentTime + SensorShotLifetime,
                Dart = dart != null ? HandleOf(dart) : null
            });

            PlaySound(pawn, "Weapon_Taser.Single");
            KickZeus(zeus);
        }

        private void UpdateSensors()
        {
            for (int i = sensors.Count - 1; i >= 0; i--)
            {
                var sensor = sensors[i];
                bool keep;
                try { keep = UpdateSensor(sensor); }
                catch (Exception ex)
                {
                    Logger.LogError("[SuperZeus] Probe update failed: {Error}", ex);
                    keep = false;
                }

                if (!keep)
                {
                    DestroySensor(sensor);
                    sensors.RemoveAt(i);
                }
            }
        }

        // Returns false once the probe should go
        private bool UpdateSensor(Sensor sensor)
        {
            float now = Server.CurrentTime;

            if (!sensor.Planted)
            {
                var tip = sensor.Tip?.Value;
                if (tip == null || !tip.IsValid || tip.AbsOrigin == null)
                    return false;

                var pos = ToVec3(tip.AbsOrigin);
                if (tip.Bounces > 0)
                {
                    PlantSensor(sensor, pos, SurfaceNormal(sensor.Velocity, tip));
                    return true;
                }
                if (now >= sensor.ExpireTime)
                {
                    sensorUsedThisRound.Remove(sensor.OwnerSlot);   // hit nothing: try again
                    Utilities.GetPlayerFromSlot(sensor.OwnerSlot)?.PrintToCenter("The probe hit nothing - shoot it at the ground");
                    return false;
                }

                tip.Teleport(null, null, (Vector3?)sensor.Velocity);
                MoveBallBeam(sensor.Dart, pos, pos - Vector3.Normalize(sensor.Velocity) * 14f);
                return true;
            }

            // Any enemy near?
            CCSPlayerController? intruder = null;
            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid || !player.PawnIsAlive || player.Slot == sensor.OwnerSlot)
                    continue;
                if (player.TeamNum == sensor.OwnerTeam && !TeammatesAreEnemies())
                    continue;
                if (player.TeamNum is not ((byte)CsTeam.Terrorist or (byte)CsTeam.CounterTerrorist))
                    continue;

                var origin = player.PlayerPawn.Value?.AbsOrigin;
                if (origin == null)
                    continue;

                var feet = ToVec3(origin);
                float across = new System.Numerics.Vector2(feet.X - sensor.Anchor.X, feet.Y - sensor.Anchor.Y).Length();
                if (across <= SensorRange && MathF.Abs(feet.Z - sensor.Anchor.Z) <= SensorHeightRange)
                {
                    intruder = player;
                    break;
                }
            }

            if (intruder != null && now >= sensor.NextAlert)
            {
                sensor.NextAlert = now + SensorAlertCooldown;
                sensor.AlertUntil = now + SensorAlertCooldown;
                RaiseSensorAlarm(sensor, intruder);
            }

            // Red flashing while alerting, otherwise a short blue flash now and then
            Color shown = now < sensor.AlertUntil
                ? ((now % (SensorAlertBlink * 2f)) < SensorAlertBlink ? SensorRed : SensorDark)
                : ((now - sensor.PlantedAt) % SensorBlinkPeriod < SensorBlinkOn ? SensorBlue : SensorDark);
            if (sensor.LightShown != shown && sensor.Light?.Value is { } light && light.IsValid)
            {
                sensor.LightShown = shown;
                light.Render = shown;
                Utilities.SetStateChanged(light, "CBaseModelEntity", "m_clrRender");
            }
            return true;
        }

        // A spike standing out of the surface with three fins at its base and
        // a light on top
        private void PlantSensor(Sensor sensor, Vector3 pos, Vector3 normal)
        {
            if (sensor.Tip != null) RemoveEntity(sensor.Tip);
            if (sensor.Dart != null) RemoveEntity(sensor.Dart);
            sensor.Tip = null;
            sensor.Dart = null;

            sensor.Planted = true;
            sensor.PlantedAt = Server.CurrentTime;
            sensor.Anchor = pos;
            sensor.Normal = normal;

            var across = MathF.Abs(normal.Z) < 0.95f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, normal)) : Vector3.UnitX;
            var other = Vector3.Cross(normal, across);

            AddSensorPart(sensor, pos - normal * 3f, pos + normal * 14f, SensorShaftColor, 1.2f, 0.6f);
            for (int i = 0; i < 3; i++)
            {
                float a = i / 3f * MathF.PI * 2f;
                var outward = across * MathF.Cos(a) + other * MathF.Sin(a);
                AddSensorPart(sensor, pos + normal * 4f, pos + outward * 5f + normal * 0.5f, SensorShaftColor, 0.8f, 0.4f);
            }

            var light = CreateBeam(pos + normal * 14f, pos + normal * 16.5f, SensorDark, 3.5f, 3.5f);
            if (light != null) sensor.Light = HandleOf(light);

            var flash = SpawnParticle(FxImpactFlare, pos, Vector3.Zero, 1f);
            PlaySound(flash, "DoSpark");
        }

        private static void AddSensorPart(Sensor sensor, Vector3 a, Vector3 b, Color color, float width, float endWidth)
        {
            var beam = CreateBeam(a, b, color, width, endWidth);
            if (beam != null) sensor.Body.Add(HandleOf(beam));
        }

        // Alarm and a chat line for the owner's team only
        private void RaiseSensorAlarm(Sensor sensor, CCSPlayerController intruder)
        {
            var team = new RecipientFilter();
            foreach (var player in Utilities.GetPlayers())
                if (player != null && player.IsValid && !player.IsBot && player.TeamNum == sensor.OwnerTeam)
                    team.Add(player);
            if (team.Count == 0)
                return;

            PlaySound(sensor.Light?.Value, SensorAlarmSound, SensorAlarmVolume, team);

            string place = intruder.PlayerPawn.Value?.LastPlaceName ?? "";
            string where = string.IsNullOrWhiteSpace(place) ? "" : $" at {ChatColors.Yellow}{place}{ChatColors.Default}";
            foreach (var player in team)
                player.PrintToChat($" {ChatColors.Red}PROBE ALERT{ChatColors.Default}: enemy{where} near {sensor.OwnerName}'s probe!");
        }

        private static void DestroySensor(Sensor sensor)
        {
            if (sensor.Tip != null) RemoveEntity(sensor.Tip);
            if (sensor.Dart != null) RemoveEntity(sensor.Dart);
            if (sensor.Light != null) RemoveEntity(sensor.Light);
            foreach (var part in sensor.Body) RemoveEntity(part);
        }

        private void RemoveSensors(int ownerSlot)
        {
            for (int i = sensors.Count - 1; i >= 0; i--)
            {
                if (sensors[i].OwnerSlot != ownerSlot)
                    continue;
                DestroySensor(sensors[i]);
                sensors.RemoveAt(i);
            }
        }

        private void ClearSensors()
        {
            foreach (var sensor in sensors)
                DestroySensor(sensor);
            sensors.Clear();
            sensorUsedThisRound.Clear();
        }
    }
}
