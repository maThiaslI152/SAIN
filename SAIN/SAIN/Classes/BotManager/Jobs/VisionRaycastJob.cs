using System.Collections;
using System.Collections.Generic;
using SAIN.Helpers;
using SAIN.Models.Enums;
using SAIN.Models.Structs;
using SAIN.Plugin;
using SAIN.Preset.GlobalSettings;
using SAIN.SAINComponent.Classes.EnemyClasses;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace SAIN.Components;

public class VisionRaycastJob : BotManagerBase
{
    private static readonly QueryParameters _losParams = new(LayerMaskClass.HighPolyWithTerrainNoGrassMask);
    private static readonly QueryParameters _visParams = new(LayerMaskClass.AI);
    private static readonly QueryParameters _shootParams = new(LayerMaskClass.HighPolyWithTerrainMaskAI);

    private bool _disposed = false;
    private readonly List<Enemy> _enemies = [];
    private readonly List<EBodyPartColliderType> _colliderTypes = [];
    private readonly List<Vector3> _castPoints = [];

    private static PerformanceSettings PerfSettings
    {
        get { return SAINPlugin.LoadedPreset?.GlobalSettings?.General?.Performance; }
    }

    private static float VisionJobInterval
    {
        get
        {
            var settings = PerfSettings;
            if (settings != null && settings.PerformanceMode)
                return 1f / settings.VisionRaycastFrequency;
            return 1f / 30f;
        }
    }

    private static float VisionUpdateInterval
    {
        get
        {
            var settings = PerfSettings;
            if (settings != null && settings.PerformanceMode)
                return 1f / settings.LookUpdateFrequency;
            return 1f / 30f;
        }
    }

    private static int MaxRaycastChecks
    {
        get
        {
            var settings = PerfSettings;
            if (settings != null && settings.PerformanceMode)
                return settings.MaxRaycastsPerEnemy;
            return 3;
        }
    }

    public VisionRaycastJob(BotManagerComponent botcontroller)
        : base(botcontroller)
    {
        botcontroller.StartCoroutine(EnemyVisionJob());
        botcontroller.StartCoroutine(UpdateEFTVision());
    }

    private WaitForSeconds _visionJobWait;
    private float _lastVisionJobInterval;

    private IEnumerator EnemyVisionJob()
    {
        yield return null;
        while (BotController != null && !_disposed)
        {
            float interval = VisionJobInterval;
            if (_visionJobWait == null || Mathf.Abs(_lastVisionJobInterval - interval) > 0.001f)
            {
                _lastVisionJobInterval = interval;
                _visionJobWait = new WaitForSeconds(interval);
            }
            WaitForSeconds wait = _visionJobWait;
            HashSet<BotComponent> bots = BotController.BotSpawnController?.SAINBots;
            if (bots != null && bots.Count > 0)
            {
                FindEnemies(bots, _enemies);
                int enemyCount = _enemies.Count;
                if (enemyCount > 0)
                {
                    int partCount = _enemies[0].Vision.EnemyParts.PartsArray.Length;
                    int raycastChecks = MaxRaycastChecks;
                    int totalRaycasts = enemyCount * partCount * raycastChecks;

                    NativeArray<RaycastHit> hits = new(totalRaycasts, Allocator.TempJob);
                    NativeArray<RaycastCommand> commands = new(totalRaycasts, Allocator.TempJob);

                    CreateCommands(commands, enemyCount, partCount, raycastChecks);
                    _handle = RaycastCommand.ScheduleBatch(commands, hits, 32);

                    yield return null;

                    _handle.Complete();
                    AnalyzeHits(hits, commands, enemyCount, partCount, raycastChecks);

                    hits.Dispose();
                    commands.Dispose();
                }
            }

            yield return wait;
        }
    }

    private IEnumerator UpdateEFTVision()
    {
        yield return null;
        while (BotController != null && !_disposed)
        {
            var allBots = BotController.BotSpawnController?.SAINBots;
            if (allBots != null && allBots.Count > 0)
            {
                float currentTime = Time.time;
                foreach (var bot in allBots)
                {
                    if (bot != null)
                    {
                        // Skip EFT look sensor for VeryFar/Narnia tier bots
                        if (bot.CurrentAILimit >= AILimitSetting.VeryFar)
                            continue;
                        bot.Vision.BotLook.UpdateLook(currentTime);
                    }
                }
            }
            yield return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_handle.IsCompleted)
        {
            _handle.Complete();
        }

        _enemies.Clear();
        _colliderTypes.Clear();
        _castPoints.Clear();
    }

    private JobHandle _handle;

    private void CreateCommands(NativeArray<RaycastCommand> raycastCommands, int enemyCount, int partCount, int raycastChecks)
    {
        _colliderTypes.Clear();
        _castPoints.Clear();

        int commands = 0;

        const float MinDist = 0.01f;
        const float Padding = 0.05f;

        for (int i = 0; i < enemyCount; i++)
        {
            var enemy = _enemies[i];
            var botTransform = enemy.Bot.Transform;

            // Skip raycasts for VeryFar/Narnia tier enemies (AI vs AI at long range)
            if (enemy.IsAI && enemy.Bot.CurrentAILimit >= AILimitSetting.VeryFar)
                continue;

            // For Far tier enemies, reduce raycast checks
            int effectiveChecks = raycastChecks;
            if (enemy.IsAI && enemy.Bot.CurrentAILimit >= AILimitSetting.Far)
            {
                effectiveChecks = Mathf.Min(raycastChecks, 2);
            }

            // For very distant enemies, check only center mass (single part)
            int effectivePartCount = partCount;
            if (enemy.RealDistance > 150f)
            {
                effectivePartCount = 1;
            }

            Vector3 eyePosition = botTransform.EyePosition;
            Vector3 weaponFirePort = botTransform.WeaponData.FirePort;

            var parts = enemy.Vision.EnemyParts.PartsArray;

            for (int j = 0; j < effectivePartCount; j++)
            {
                var part = parts[j];

                SAINBodyPartRaycast raycastData = part.GetRaycast();
                Vector3 castPoint = raycastData.CastPoint;

                _colliderTypes.Add(raycastData.ColliderType);
                _castPoints.Add(castPoint);

                Vector3 eyeVec = castPoint - eyePosition;
                float eyeMag = eyeVec.magnitude;
                Vector3 eyeDir = eyeMag > 1e-6f ? (eyeVec / eyeMag) : Vector3.forward;
                float eyeDist = Mathf.Max(eyeMag, MinDist);

                // Always do LineOfSight check (1st)
                raycastCommands[commands++] = new RaycastCommand(eyePosition, eyeDir, _losParams, eyeDist + Padding);

                if (effectiveChecks >= 2)
                {
                    // Vision check (2nd)
                    raycastCommands[commands++] = new RaycastCommand(eyePosition, eyeDir, _visParams, eyeDist + Padding);
                }

                if (effectiveChecks >= 3)
                {
                    // Shoot check (3rd)
                    Vector3 weaponVec = castPoint - weaponFirePort;
                    float weaponMag = weaponVec.magnitude;
                    Vector3 weaponDir = weaponMag > 1e-6f ? (weaponVec / weaponMag) : Vector3.forward;
                    float weaponDist = Mathf.Max(eyeMag, MinDist);
                    raycastCommands[commands++] = new RaycastCommand(weaponFirePort, weaponDir, _shootParams, weaponDist + Padding);
                }
            }
        }
    }

    private void AnalyzeHits(NativeArray<RaycastHit> raycastHits, NativeArray<RaycastCommand> commands, int enemyCount, int partCount, int raycastChecks)
    {
        float time = Time.time;
        int hits = 0;
        int colliderTypeCount = 0;

        for (int i = 0; i < enemyCount; i++)
        {
            var enemy = _enemies[i];

            // Skip analysis for enemies whose raycasts were skipped
            if (enemy.IsAI && enemy.Bot.CurrentAILimit >= AILimitSetting.VeryFar)
                continue;

            // Match the same effective part count used in CreateCommands
            int effectivePartCount = partCount;
            if (enemy.RealDistance > 150f)
            {
                effectivePartCount = 1;
            }

            int effectiveChecks = raycastChecks;
            if (enemy.IsAI && enemy.Bot.CurrentAILimit >= AILimitSetting.Far)
            {
                effectiveChecks = Mathf.Min(raycastChecks, 2);
            }

            var parts = enemy.Vision.EnemyParts.PartsArray;
            for (int j = 0; j < effectivePartCount; j++)
            {
                var part = parts[j];
                EBodyPartColliderType colliderType = _colliderTypes[colliderTypeCount];
                Vector3 castPoint = _castPoints[colliderTypeCount];
                colliderTypeCount++;

                if (SAINPlugin.LoadedPreset.GlobalSettings.General.Debug.Gizmos.DrawLineOfSightGizmos)
                {
                    var cmd = commands[hits];
                    var hit = raycastHits[hits];

                    Vector3 from = cmd.from;

                    if (hit.collider == null)
                    {
                        DebugGizmos.DrawSphere(castPoint, 0.03f, Color.yellow, 0.2f);
                        DebugGizmos.DrawLine(from, castPoint, Color.green, 0.025f, 0.2f);
                    }
                }

                part.SetLineOfSight(castPoint, colliderType, raycastHits[hits++], ERaycastCheck.LineofSight, time);

                if (effectiveChecks >= 2)
                {
                    part.SetLineOfSight(castPoint, colliderType, raycastHits[hits++], ERaycastCheck.Vision, time);
                }

                if (effectiveChecks >= 3)
                {
                    part.SetLineOfSight(castPoint, colliderType, raycastHits[hits++], ERaycastCheck.Shoot, time);
                }
            }
        }
    }

    private static void FindEnemies(HashSet<BotComponent> bots, List<Enemy> enemies)
    {
        float currentTime = Time.time;
        enemies.Clear();
        foreach (BotComponent bot in bots)
        {
            if (bot != null)
            {
                foreach (Enemy enemy in bot.EnemyController.EnemiesArray)
                {
                    if (enemy.ShallCheckLook(currentTime, out _))
                    {
                        enemies.Add(enemy);
                    }
                }
            }
        }
    }
}
