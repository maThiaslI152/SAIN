using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Game.Spawning;
using EFT.InventoryLogic;
using SAIN.Components.CoverFinder;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Helpers;
using SAIN.Plugin;
using SAIN.Preset.GlobalSettings;
using UnityEngine;

namespace SAIN.Components;

public class GameWorldComponent : MonoBehaviour
{
    public static bool TryGetPlayerComponent(IPlayer Player, out PlayerComponent PlayerComponent)
    {
        if (Player == null)
        {
            //Logger.LogError("Player Null");
            PlayerComponent = null;
            return false;
        }
        PlayerSpawnTracker PlayerTracker = Instance?.PlayerTracker;
        if (PlayerTracker == null)
        {
            //Logger.LogError("GameWorld Component Null, can't get Player Component");
            PlayerComponent = null;
            return false;
        }
        PlayerComponent = PlayerTracker.GetPlayerComponent(Player);
        return PlayerComponent != null;
    }

    public void RegisterShot(Player Player, EftBulletClass Bullet, Item Weapon)
    {
        if (TryGetPlayerComponent(Player, out PlayerComponent PlayerComponent))
        {
            StartCoroutine(TrackBullet(PlayerComponent, Bullet));
            //var OtherPlayerData = PlayerComponent.OtherPlayersData.DataHashSet;
            //Vector3 PlayerLookDir = PlayerComponent.LookDirection;
            //
            //// Add any other AI Controlled players that are in the direction this shot is going
            //_tempOtherPlayerCache.AddRange(from Data in OtherPlayerData
            //                               let OtherPlayerDirNormal = Data.DistanceData.DirectionNormal
            //                               let BotPlayerComponent = Data.PlayerComponent
            //                               where BotPlayerComponent != null && BotPlayerComponent.IsAI && BotPlayerComponent.IsActive && Vector3.Dot(OtherPlayerDirNormal, PlayerLookDir) > 0.75f
            //                               select Data);
            //
            //if (_tempOtherPlayerCache.Count > 0)
            //{
            //    List<OtherPlayerData> RelevantPlayers = [];
            //    RelevantPlayers.AddRange(_tempOtherPlayerCache);
            //    _tempOtherPlayerCache.Clear();
            //    ActiveBullets.Add(new(Bullet, PlayerComponent, RelevantPlayers));
            //}
        }
    }

    private IEnumerator TrackBullet(PlayerComponent Player, EftBulletClass Bullet)
    {
        //Vector3 LastPosition = Bullet.StartPosition;
        var OtherPlayerData = Player.OtherPlayersData.DataDictionary;
        Vector3 PlayerLookDir = Player.LookDirection;

        List<OtherPlayerData> PlayersToCheck = [];
        PlayersToCheck.AddRange(
            from Data in OtherPlayerData
            let OtherPlayerDirNormal = Data.Value.DistanceData.DirectionNormal
            let PlayerComponent = Data.Value.OtherPlayerComponent
            where PlayerComponent?.IsAI == true && PlayerComponent.IsActive && Vector3.Dot(OtherPlayerDirNormal, PlayerLookDir) > 0.75f
            select Data.Value
        );

        const float MaxFlyByDistSqr = 10 * 10;

        if (PlayersToCheck.Count > 0)
        {
            while (!Bullet.IsShotFinished && Player?.IsActive == true && PlayersToCheck.Count > 0)
            {
                Vector3 BulletPosition = Bullet.CurrentPosition;
                //DebugGizmos.Sphere(BulletPosition, 0.3f, Color.red, true, 3.0f);
                //DebugGizmos.Line(BulletPosition, LastPosition, Color.red, 0.05f, true, 3, true);

                for (int i = PlayersToCheck.Count - 1; i >= 0; i--)
                {
                    OtherPlayerData Data = PlayersToCheck[i];
                    if (Data?.OtherPlayerComponent?.IsActive == false)
                    {
                        PlayersToCheck.RemoveAt(i);
                        continue;
                    }
                    Vector3 PlayerPosition = Data.DistanceData.Position;
                    float BulletDistSqr = (PlayerPosition - BulletPosition).sqrMagnitude;
                    if (BulletDistSqr < MaxFlyByDistSqr)
                    {
                        Data.OtherPlayerComponent.RegisterFlyBy(Player, Bullet);
                        PlayersToCheck.RemoveAt(i);
                        continue;
                    }
                }
                //LastPosition = BulletPosition;
                yield return null;
            }
        }
    }

    public static GameWorldComponent Instance { get; private set; }
    public GameWorld GameWorld { get; private set; }
    public PlayerSpawnTracker PlayerTracker { get; private set; }
    public BotManagerComponent SAINBotController { get; private set; }
    public Extract.ExtractFinderComponent ExtractFinder { get; private set; }
    public DoorHandler Doors { get; private set; }
    public LocationClass Location { get; private set; }
    public SpawnPointMarker[] SpawnPointMarkers { get; private set; }
    public JobManager JobManager { get; private set; }

    public void WorldTick(float deltaTime)
    {
        float currentTime = Time.time;
        ManualUpdate(currentTime, deltaTime);
        SAINBotController.ManualUpdate(currentTime, deltaTime);
    }

    protected void ManualUpdate(float CurrentTime, float DeltaTime)
    {
        if (_activated)
        {
            ExtractFinder.ManualUpdate(CurrentTime, DeltaTime);
            Doors.ManualUpdate(CurrentTime, DeltaTime);
            Location.ManualUpdate(CurrentTime, DeltaTime);
            findSpawnPointMarkers();
            HashSet<PlayerComponent> players = PlayerTracker?.AlivePlayerArray;
            if (players != null && players.Count > 0)
            {
                // Sync snapshot list when HashSet size changes to avoid foreach allocation
                if (players.Count != _lastPlayerSnapshotCount)
                {
                    _playerListSnapshot.Clear();
                    _playerListSnapshot.AddRange(players);
                    _lastPlayerSnapshotCount = players.Count;
                }
                for (int i = 0; i < _playerListSnapshot.Count; i++)
                {
                    var player = _playerListSnapshot[i];
                    if (player != null)
                    {
                        player.ManualUpdate(CurrentTime, DeltaTime);
                    }
                }

                TickSoundCaches(_playerListSnapshot, CurrentTime);
            }
        }
    }

    private const float _Sounds_PlayerCache_Interval = 1f / 30f;
    private const float _Sounds_BotCache_Interval = 1f / 15f;

    protected void TickSoundCaches(List<PlayerComponent> PlayerComponents, float CurrentTime)
    {
        if (_Sounds_PlayerCache_Time < CurrentTime)
        {
            _Sounds_PlayerCache_Time = CurrentTime + _Sounds_PlayerCache_Interval;

            for (int i = 0; i < PlayerComponents.Count; i++)
            {
                var player = PlayerComponents[i];
                if (player != null)
                {
                    UpdatePlayerSoundCache(player);
                }
            }
        }

        // Scale bot sound cache interval by bot count for performance
        float botCacheInterval = GetBotCacheInterval();
        if (_Sounds_BotCache_Time < CurrentTime)
        {
            _Sounds_BotCache_Time = CurrentTime + botCacheInterval;

            for (int i = 0; i < PlayerComponents.Count; i++)
            {
                BotComponent Bot = PlayerComponents[i]?.BotComponent;
                if (Bot != null && Bot.BotOwner?.BotState == EBotState.Active)
                {
                    Bot.Hearing.SoundInput.ProcessAISoundCache();
                }
            }
        }
    }

    private float GetBotCacheInterval()
    {
        var settings = SAINPlugin.LoadedPreset?.GlobalSettings?.General?.Performance;
        if (settings == null || !settings.PerformanceMode)
            return _Sounds_BotCache_Interval;

        // Scale interval based on total bot count - more bots = less frequent processing
        var bots = SAINBotController?.BotSpawnController?.SAINBots;
        if (bots != null && bots.Count > 30)
            return _Sounds_BotCache_Interval * 2f;
        if (bots != null && bots.Count > 15)
            return _Sounds_BotCache_Interval * 1.5f;

        return _Sounds_BotCache_Interval;
    }

    protected static void UpdatePlayerSoundCache(PlayerComponent Player)
    {
        List<SoundEvent> events = Player.AISoundCachedEvents;
        if (Player.IsActive)
        {
            foreach (OtherPlayerData OtherPlayerData in Player.OtherPlayersData.DataHashSet)
            {
                PlayerComponent OtherPlayer = OtherPlayerData?.OtherPlayerComponent;
                if (OtherPlayer != null && OtherPlayer.IsActive && OtherPlayer.IsSAINBot)
                {
                    BotComponent Bot = OtherPlayer.BotComponent;
                    if (Bot != null && Bot.BotOwner?.BotState == EBotState.Active)
                    {
                        bool InFootstepRadius = OtherPlayerData.IsInHearingRadius_Footsteps;
                        bool InGunfireRadius = OtherPlayerData.IsInHearingRadius_GunFire;
                        float Distance = OtherPlayerData.DistanceData.Distance;
                        foreach (var soundEvent in events)
                        {
                            bool isGunshot = soundEvent.IsGunShot;
                            if (isGunshot && !InGunfireRadius)
                            {
                                continue;
                            }

                            if (!isGunshot && !InFootstepRadius)
                            {
                                continue;
                            }

                            OtherPlayer.BotComponent?.Hearing.SoundInput.CheckAddSoundToCache(soundEvent, Distance);
                        }
                    }
                }
            }
        }
        events.Clear();
    }

    private void findSpawnPointMarkers()
    {
        if ((SpawnPointMarkers != null) || (Camera.main == null))
        {
            return;
        }

        SpawnPointMarkers = UnityEngine.Object.FindObjectsOfType<SpawnPointMarker>();

#if DEBUG
        if (SAINPlugin.DebugMode)
        {
            Logger.LogInfo($"Found {SpawnPointMarkers.Length} spawn point markers");
        }
#endif
    }

    public IEnumerable<Vector3> GetAllSpawnPointPositionsOnNavMesh()
    {
        if (SpawnPointMarkers == null)
        {
            return [];
        }

        List<Vector3> spawnPointPositions = [];
        foreach (SpawnPointMarker spawnPointMarker in SpawnPointMarkers)
        {
            // Try to find a point on the NavMesh nearby the spawn point
            Vector3? spawnPointPosition = NavMeshHelpers.GetNearbyNavMeshPoint(spawnPointMarker.Position, 2);
            if (spawnPointPosition.HasValue && !spawnPointPositions.Contains(spawnPointPosition.Value))
            {
                spawnPointPositions.Add(spawnPointPosition.Value);
            }
        }

        return spawnPointPositions;
    }

    public void Activate(BotsController botsController)
    {
        SAINBotController.DefaultController = botsController;
        SAINBotController.BotSpawner = botsController.BotSpawner;
        _activated = true;
        JobManager.Start();
        // StartCoroutine(CalcPathsJobs());
    }

    private bool _activated = false;
    private readonly List<PlayerComponent> _playerListSnapshot = [];
    private int _lastPlayerSnapshotCount = -1;

    public void Init(GameWorld gameWorld, BotManagerComponent sainBotController)
    {
        Instance = this;
        GameWorld = gameWorld;
        if (GameWorld == null)
        {
#if DEBUG
            Logger.LogWarning("GameWorld Null, cannot Init SAIN Gameworld! Check 2. Disposing Component...");
#endif
            DestroyComponent();
            return;
        }

        SAINBotController = sainBotController;
        PlayerTracker = new PlayerSpawnTracker(this);
        Doors = new DoorHandler(this);
        Location = new LocationClass(this);
        ExtractFinder = this.GetOrAddComponent<Extract.ExtractFinderComponent>();
        JobManager = new JobManager(this);
        GameWorld.OnDispose += DestroyComponent;

        try
        {
            EFTCoreSettings.UpdateCoreSettings();
        }
        catch (Exception e)
        {
            Logger.LogError(e);
        }

        //Logger.LogDebug("SAIN GameWorld Created.");

        Doors.Init();
        Location.Init();
    }

    public void DestroyComponent()
    {
        Instance = null;
        try
        {
            PlayerTracker?.Dispose();
            Doors?.Dispose();
            Location?.Dispose();
            JobManager?.Dispose();
        }
        catch (Exception e)
        {
            Logger.LogError($"Dispose GameWorld Component Class Error: {e}");
        }

        try
        {
            ComponentHelpers.DestroyComponent(SAINBotController);
        }
        catch (Exception e)
        {
            Logger.LogError($"Dispose GameWorld SubComponent Error: {e}");
        }

        StopAllCoroutines();
        Instance = null;
        GameWorld.OnDispose -= DestroyComponent;
        Destroy(this);
        //Logger.LogDebug("SAIN GameWorld Destroyed.");
    }

    private float _Sounds_PlayerCache_Time;
    private float _Sounds_BotCache_Time;
}
