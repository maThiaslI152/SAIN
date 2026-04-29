# SAIN Bot Performance Optimization Guide

> **Purpose:** Comprehensive reference document for AI agents and developers to optimize in-game bot performance for the SAIN (SoloAI Ingress) mod for SPT (Single Player Tarkov).
>
> **Estimated total impact:** 40-70% reduction in bot-related CPU usage on large maps.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Performance Hotspots](#2-performance-hotspots)
3. [Settings-Only Tuning (No Code)](#3-settings-only-tuning-no-code)
4. [Phase 1: Code Optimizations](#4-phase-1-code-optimizations)
  - [4A. Vision Raycast Throttling](#4a-vision-raycast-throttling)
  - [4B. Look Sensor Throttling](#4b-look-sensor-throttling)
  - [4C. Cover Finder Throttling](#4c-cover-finder-throttling)
  - [4D. Decision Manager Throttling](#4d-decision-manager-throttling)
  - [4E. Expanded Performance Settings](#4e-expanded-performance-settings)
  - [4F. Hearing System Optimizations](#4f-hearing-system-optimizations)
  - [4G. Runaway Coroutine Fixes](#4g-runaway-coroutine-fixes)
  - [4H. Memory Leak & Bug Fixes](#4h-memory-leak--bug-fixes)
  - [4I. Thread Safety & Static Arrays](#4i-thread-safety--static-arrays)
  - [4J. GC Pressure Reduction](#4j-gc-pressure-reduction)
  - [4K. O(N²) → O(1) Enemy Lookup Optimization](#4k-on%C2%B2--o1-enemy-lookup-optimization)
5. [Phase 2: Missed Bottleneck Throttles](#5-phase-2-missed-bottleneck-throttles)
  - [5A. DirectionDataJob — Every-Frame O(P²) Job](#5a-directiondatajob--every-frame-op%C2%B2-job)
  - [5B. EnemyPlaceRaycastJob — Untamed Every-Frame Job](#5b-enemyplaceraycastjob--untamed-every-frame-job)
  - [5C. SAINBotUnstuckClass — Per-Bot Every-Frame Coroutine](#5c-sainbotunstuckclass--per-bot-every-frame-coroutine)
  - [5D. BotComponent.TickClassGroup — Bypassed ShallTick() Infrastructure](#5d-botcomponenttickclassgroup--bypassed-shalltick-infrastructure)
  - [5E. Enemy.ShallCheckLook / ShallCheckLoS — Redundant Timers & Validations](#5e-enemyshallchecklook--shallchecklos--redundant-timers--validations)
  - [5F. SAINEnemyController.UpdateEnemies — Every-Frame Full Iteration](#5f-sainenemycontrollerupdateenemies--every-frame-full-iteration)
  - [5G. BotSquadClass.checkVisibleMembers — Blocking Physics.Raycast per Squad Member](#5g-botsquadclasscheckvisiblemembers--blocking-physicsraycast-per-squad-member)
6. [Phase 3: Remaining Optimizations + Bug Fixes](#6-phase-3-remaining-performance-optimizations--bug-fixes)
  - [6A. Bug Fixes](#6a-bug-fixes-critical)
  - [6B. GC & Code Improvements](#6b-gc--code-improvements)
7. [AI Limit Tie-In System](#7-ai-limit-tie-in-system)
8. [Mod Compatibility](#8-mod-compatibility)
9. [Testing & Validation](#9-testing--validation)
10. [File Reference](#10-file-reference)
11. [Custom Preset Creation](#11-custom-preset-creation)

---

## 1. Architecture Overview

### How Bot Ticking Works

```
GameWorld.DoWorldTick() (EFT game tick)
  ↓
WorldTickPatch (Harmony postfix)
  ↓
GameWorldComponent.WorldTick(dt)
  ├── ManualUpdate(time, dt) → extract finder, doors, location, all PlayerComponents
  ├── BotManagerComponent.ManualUpdate(time, dt)
  │   ├── BotSpawnController.Update
  │   ├── TimeVision.Update
  │   ├── WeatherVision.Update
  │   ├── BotSquads.Update
  │   └── ForEach BotComponent → BotComponent.ManualUpdate(time, dt)
  │       ├── TickClassGroup(_alwaysTickClasses)
  │       ├── TickClassGroup(_tickWhenActiveClasses) [if BotActive]
  │       ├── TickClassGroup(_tickWhenNoSleepClasses) [if !BotStandBy]
  │       ├── HandleDumbShit() [if !StandBy]
  │       └── TickClassGroup(_tickWhenCombatClasses) [if in combat]
  └── TickSoundCaches() → sound propagation at 30Hz/15Hz
```

### Bot Tick Groups (BotComponent.cs)


| Group                     | Classes                                                                                              | When                       |
| ------------------------- | ---------------------------------------------------------------------------------------------------- | -------------------------- |
| `_alwaysTickClasses`      | SAINActivationClass, SAINAILimit, CurrentTargetClass, SAINEnemyController, SAINDecisionClass         | Every frame                |
| `_tickWhenActiveClasses`  | SAINBotUnstuckClass                                                                                  | When bot is active         |
| `_tickWhenNoSleepClasses` | ~18 classes: Vision, Hearing, Mover, Medical, Info, Cover, Steering, Talk, Memory, Suppression, etc. | When bot is not in standby |
| `_tickWhenCombatClasses`  | SAINShootData, AimDownSightsController, SAINFriendlyFireClass                                        | When bot is in combat      |


### Global Vision Job (Coroutine-based, ~30Hz)

```
VisionRaycastJob (started in BotManagerComponent)
  ├── EnemyVisionJob() - 30Hz coroutine
  │   └── ForEach enemy → 3 raycasts per body part (LoS, Vision, Shoot)
  │       via Unity Job System (RaycastCommand.ScheduleBatch)
  └── UpdateEFTVision() - 30Hz coroutine (split into Group1/Group2)
      └── ForEach bot → bot.Vision.BotLook.UpdateLook(currentTime)
          └── ForEach enemy → enemy.EnemyInfo.CheckLookEnemy()
```

---

## 2. Performance Hotspots

### 🔴 P1: Vision Raycast Job (~30Hz per bot) — VisionRaycastJob.cs

**Location:** `SAIN\Classes\BotManager\Jobs\VisionRaycastJob.cs`

**The math:** `TotalRaycasts = Bots × Enemies × BodyParts × RAYCAST_CHECKS(3)`

- 30 bots × 3 enemies × ~10 body parts × 3 checks = **2,700+ raycasts per frame**
- Runs at 30Hz = 81,000 raycasts per second
- In high-density scenarios: easily 5,000+ raycasts/frame

**Key constants (lines 19-21):**

```csharp
private const float VISION_UPDATE_INTERVAL = 1f / 30f;   // 33ms
private const float VISION_JOB_INTERVAL = 1f / 30f;      // 33ms  
private const int RAYCAST_CHECKS = 3;                     // LoS, Vision, Shoot
```

### 🟡 P2: EFT Look Sensor (~30Hz per bot) — SAINBotLookClass.cs

**Location:** `SAIN\Classes\Bot\Sense\SAINBotLookClass.cs`

- Runs on main thread at 30Hz, split into two groups with WaitForFixedUpdate between them
- Calls `enemy.EnemyInfo.CheckLookEnemy()` — this is EFT's internal look sensor which is very heavy
- Two sync points (`WaitForFixedUpdate`) delay execution

### 🟡 P3: Cover Finder (~10Hz per bot in combat) — CoverFinderComponent.cs

**Location:** `SAIN\Components\CoverFinderComponent.cs`

- Physics.OverlapBox(35×5×35m) every 4 seconds
- Iterates all valid colliders every 1/10th second
- Analyzes each collider for cover suitability (raycasts, path checks)
- Covers sorted by path length every tick

### 🟡 P4: Decision Manager (~10Hz per bot) — BotDecisionManager.cs

**Location:** `SAIN\Classes\Bot\Decision\BotDecisionManager.cs`

- `DECISION_FREQUENCY = 1f / 10f` (hardcoded)
- Chains through: SelfAction → Dogfight → SquadDecisions → EnemyDecisions
- `ChooseEnemy()` called every tick (iterates all known enemies)

### ⚪ P5: Hearing System Sound Propagation — GameWorldComponent.cs

**Location:** `SAIN\Components\GameWorldComponent.cs`

- `_Sounds_PlayerCache_Interval = 1f / 30f` — iterates all players every 33ms
- `_Sounds_BotCache_Interval = 1f / 15f` — processes all bot sound caches every 66ms
- Each player's sound events pushed to every other player's bot hearing system

### 🔴 P6: DirectionDataJob — O(P²) Every Frame — DirectionDataJob.cs

**Location:** `SAIN\Classes\BotManager\Jobs\DirectionDataJob.cs`

- Nested loop over all players: O(P²) complexity
- Runs every frame with `yield return null;` — no throttling whatsoever
- For 30 players: ~900 iterations per frame × 60fps = 54,000 checks per second

### 🔴 P7: EnemyPlaceRaycastJob — Untamed Every-Frame — EnemyPlaceRaycastJob.cs

**Location:** `SAIN\Classes\BotManager\Jobs\EnemyPlaceRaycastJob.cs`

- Original `while(true)` with `yield return null;` — no throttle
- Was also a memory leak (NativeArrays not disposed on exception)
- Every bot's enemy position verified every frame

### 🟡 P8: SAINBotUnstuckClass — Per-Bot Every-Frame — SAINBotUnstuckClass.cs

**Location:** `SAIN\Classes\Bot\SAINBotUnstuckClass.cs`

- Runs as `_tickWhenActiveClass` so it fires while bot is spawned
- `BotUnstuck()` coroutine uses bare `yield return null;` — no interval between checks
- Even if not stuck, the coroutine iterates every frame
- 30 bots × 60fps = 1,800 iterations/sec doing nothing useful

### 🟡 P9: BotComponent.TickClassGroup — Bypassed Throttling — BotComponent.cs

**Location:** `SAIN\Components\BotComponent.cs`

- `TickClassGroup()` calls `botClass.ManualUpdate()` unconditionally in a loop
- Ignores the `IBotClass.ShallTick()` method entirely
- All classes in `_alwaysTickClasses` fire every frame (SAINEnemyController, SAINDecisionClass, etc.)

### 🟡 P10: Enemy.ShallCheckLook / ShallCheckLoS — Redundant Timers — Enemy.cs

**Location:** `SAIN\Classes\Bot\EnemyClasses\Enemy.cs`

- `ShallCheckLook()` and `ShallCheckLoS()` have separate timers (`_nextCheckLookTime` and `_nextCheckLoSTime`)
- Each calls `CheckValid()` internally — redundant validation
- Look and LoS are always requested together in practice, so two timers waste cycles

### 🟡 P11: SAINEnemyController.UpdateEnemies — Every-Frame Iteration — SAINEnemyController.cs

**Location:** `SAIN\Classes\Bot\EnemyControllers\SAINEnemyController.cs`

- `UpdateEnemies()` runs every frame via `_alwaysTickClasses`
- Iterates all known enemies, updates distances, checks validity
- No time gate or interval check

### ⚪ P12: BotSquadClass.checkVisibleMembers — Blocking Raycasts — BotSquadClass.cs

**Location:** `SAIN\Classes\Bot\Info\BotSquadClass.cs`

- `checkVisibleMembers()` fires every ~1 second
- For each squad member, issues blocking `Physics.Raycast()` on the main thread
- Worst case for large squads: 5-10 blocking raycasts per bot, per second
- 30 bots × 10 members = 300 blocking raycasts/sec on main thread

---

## 3. Settings-Only Tuning (No Code)

Access via **F6 in-game menu** → Global Settings.

### 3.1 One-Click: Performance Mode


| Menu Path                      | Setting              | Recommended Value |
| ------------------------------ | -------------------- | ----------------- |
| Global → General → Performance | **Performance Mode** | ON                |


**Effect:** Reduces cover finder frequency, limits some raycasts. Biggest single setting toggle.

### 3.2 AI Limit System (Critical for AI-vs-AI)


| Menu Path                   | Setting                            | Recommended Value |
| --------------------------- | ---------------------------------- | ----------------- |
| Global → General → AI Limit | **Limit AI vs AI - Global Toggle** | ON                |
| Same                        | **AILimitUpdateFrequency**         | 3.0 (default)     |
| Same → AILimitRanges        | **Far**                            | 150m              |
| Same → AILimitRanges        | **VeryFar**                        | 250m              |
| Same → AILimitRanges        | **Narnia**                         | 400m              |
| Same                        | **Limit AI vs AI Vision**          | ON                |
| Same → MaxVisionRanges      | **Far**                            | 200m              |
| Same → MaxVisionRanges      | **VeryFar**                        | 100m              |
| Same → MaxVisionRanges      | **Narnia**                         | 50m               |
| Same                        | **Limit AI vs AI Hearing**         | ON                |
| Same → MaxHearingRanges     | **Far**                            | 100m              |
| Same → MaxHearingRanges     | **VeryFar**                        | 60m               |
| Same → MaxHearingRanges     | **Narnia**                         | 25m               |


### 3.3 Cover System


| Menu Path                | Setting                          | Recommended Value |
| ------------------------ | -------------------------------- | ----------------- |
| Global → General → Cover | **CoverMinHeight**               | 1.0-1.2m          |
| Same                     | **CoverMinEnemyDistance**        | 10-15m            |
| Same                     | **MaxCoverPathLength**           | 40-50m            |
| Same                     | **ShiftCoverChangeDecisionTime** | 6s                |
| Same                     | **ShiftCoverTimeSinceSeen**      | 30s               |


### 3.4 Visual/Perception (Reduce Raycast Needs)


| Menu Path                                 | Setting                                  | Recommended Value |
| ----------------------------------------- | ---------------------------------------- | ----------------- |
| Global → Look → Vision Distance           | **Movement Distance Modifier**           | 1.5 (default)     |
| Global → Look → Vision Speed → Movement   | **Movement Vision Multiplier**           | 0.5 (default)     |
| Global → Look → Vision Speed → Parts      | **PARTS_VISIBLE_MAX_COEF**               | 2.0 (default)     |
| Global → Look → Vision Speed → Peripheral | **PERIPHERAL_VISION_START_ANGLE**        | 30 (default)      |
| Global → Look → Vision Speed → Peripheral | **PERIPHERAL_VISION_MAX_REDUCTION_COEF** | 2.0 (default)     |
| Global → Look → Time                      | **VISION_WEATHER_MIN_DIST_METERS**       | 30m               |


### 3.5 Mind/Suppression (Extra CPU)


| Menu Path                   | Setting                      | Recommended Value                    |
| --------------------------- | ---------------------------- | ------------------------------------ |
| Global → Mind → Suppression | **Enemy Suppression Toggle** | OFF (disables all suppression calcs) |
| Global → Mind → Personality | **Global Aggression**        | 0.8-1.0                              |


### 3.6 Disable Debug Features


| Menu Path                         | Setting                   | Recommended Value |
| --------------------------------- | ------------------------- | ----------------- |
| Global → General → Debug          | **All Debug Gizmos**      | OFF               |
| Global → General → Debug → Gizmos | **DrawLineOfSightGizmos** | OFF               |


---

## 4. Phase 1: Code Optimizations

### 4A. Vision Raycast Throttling

**File:** `SAIN\Classes\BotManager\Jobs\VisionRaycastJob.cs`

#### Change A1: Add AI-Limit-Based Throttling

**Problem:** All bots raycast at 30Hz regardless of distance from human players. AI vs AI fights at 300m don't need 30Hz raycasts.

```csharp
// In VisionRaycastJob.cs, before scheduling raycasts:
private static bool ShouldSkipRaycastForLimit(Enemy enemy, AILimitSetting aiLimit)
{
    return aiLimit switch
    {
        AILimitSetting.Narnia => true,      // Skip entirely for >400m
        AILimitSetting.VeryFar => true,     // Skip entirely for >250m
        AILimitSetting.Far => !enemy.IsCurrentEnemy,  // Skip non-priority enemies
        _ => false                          // No limit = run normally
    };
}
```

**Usage:** In `FindEnemies()`, filter out enemies that shouldn't be checked at this limit tier.

#### Change A2: Dynamic Interval Based on Bot Count

**Problem:** Raycast interval is fixed at 30Hz. On high-density maps, this creates massive spikes.

```csharp
private IEnumerator EnemyVisionJob()
{
    while (BotController != null && !_disposed)
    {
        float jobInterval = GetDynamicVisionInterval();
        WaitForSeconds wait = new(jobInterval);
        
        // ... existing raycast logic ...
        
        yield return wait;
    }
}

private float GetDynamicVisionInterval()
{
    int botCount = BotController.BotSpawnController.SAINBots.Count;
    // Scale interval inversely with bot count
    // 10 bots = 30Hz (0.033s), 30 bots = 15Hz (0.066s), 50 bots = 10Hz (0.1s)
    float baseInterval = 1f / 30f;
    float scaled = baseInterval * (botCount / 10f);
    return Mathf.Clamp(scaled, 0.033f, 0.1f);  // Clamp between 30Hz and 10Hz
}
```

#### Change A3: Reduce RAYCAST_CHECKS for AI-Vs-AI

```csharp
// Instead of always 3, dynamically choose:
int checksPerPart = RAYCAST_CHECKS;
if (enemy.IsAI && aiLimit != AILimitSetting.None)
{
    checksPerPart = 1; // Only LoS check for limited AI-vs-AI
}
```

#### Change A4: Reduce Part Count for Distant Enemies

```csharp
// For very distant enemies, check only the center mass (single raycast)
int partCount = enemy.RealDistance > 150f ? 1 : enemy.Vision.EnemyParts.PartsArray.Length;
```

---

### 4B. Look Sensor Throttling

**File:** `SAIN\Classes\Bot\Sense\SAINBotLookClass.cs` and `VisionRaycastJob.cs` (UpdateEFTVision coroutine)

#### Change B1: Consolidate Bot Groups

**Problem:** `UpdateEFTVision()` splits bots into Group1 and Group2 with a `WaitForFixedUpdate` between them — adds latency and frame sync overhead.

```csharp
// Replace the two-group split with a single loop
private IEnumerator UpdateEFTVision()
{
    WaitForSeconds wait = new(VISION_UPDATE_INTERVAL);
    yield return wait;
    
    while (BotController != null)
    {
        var allBots = BotController.BotSpawnController.SAINBots;
        if (allBots.Count > 0)
        {
            float currentTime = Time.time;
            foreach (var bot in allBots)
            {
                // Skip bots in very far / narnia AI limit
                if (bot.CurrentAILimit >= AILimitSetting.VeryFar)
                    continue;
                    
                bot.Vision.BotLook.UpdateLook(currentTime);
            }
        }
        yield return null;  // Single yield
    }
}
```

#### Change B2: Throttle Look Updates per AI Limit

**In `SAINBotLookClass.UpdateLookForEnemies()`:**

```csharp
private static int UpdateLookForEnemies(LookAllData lookAll, float currentTime, BotComponent bot)
{
    // Skip EFT look sensor entirely for limited bots
    if (bot.CurrentAILimit >= AILimitSetting.VeryFar)
        return 0;
    
    // Throttle update interval for far bots
    bool isLimited = bot.CurrentAILimit >= AILimitSetting.Far;
    if (isLimited && !ShouldUpdateNow(bot))
        return 0;
    
    // ... existing logic ...
}
```

---

### 4C. Cover Finder Throttling

**File:** `SAIN\Components\CoverFinderComponent.cs` and `SAIN\Classes\Coverfinder\CoverAnalyzer.cs`

#### Change C1: Reduce Overlap Box Size Based on AI Limit

**In `FindCoverLoop()`:**

```csharp
private Vector3 GetOverlapExtents()
{
    AILimitSetting limit = Bot.CurrentAILimit;
    return limit switch
    {
        AILimitSetting.None => new Vector3(35, 5, 35),  // Full size
        AILimitSetting.Far => new Vector3(20, 5, 20),    // Reduced
        _ => new Vector3(10, 5, 10),                     // Minimal
    };
}
```

#### Change C2: Increase Cover Finder Interval for Limited Bots

```csharp
private IEnumerator FindCoverLoop()
{
    while (Bot != null)
    {
        float interval = FIND_COVER_INTERVAL;
        AILimitSetting limit = Bot.CurrentAILimit;
        if (limit == AILimitSetting.Far)
            interval = 1f / 5f;       // 5Hz instead of 10Hz
        else if (limit >= AILimitSetting.VeryFar)
            interval = 1f / 2f;       // 2Hz
            
        WaitForSeconds wait = new(interval);
        
        // ... existing cover logic ...
        yield return wait;
    }
}
```

#### Change C3: Skip Cover Finder for VeryFar/Narnia Bots

```csharp
// Skip cover finding for very distant bots
if (Bot.CurrentAILimit >= AILimitSetting.VeryFar)
{
    yield return wait;
    continue;
}
```

#### Change C4: Reduce Cover Point Limit

```csharp
int max = Bot.CurrentAILimit >= AILimitSetting.Far ? 3 : 5;
```

---

### 4D. Decision Manager Throttling

**File:** `SAIN\Classes\Bot\Decision\BotDecisionManager.cs`

**Implemented Changes:**

1. Added `GetDecisionFrequency()` method to dynamically calculate the decision interval based on `PerformanceSettings` and `Bot.CurrentAILimit`:
  - Base: ~10Hz
  - `Far` bots: 1.5x interval multiplier when `PerformanceMode` is active
  - `VeryFar` bots: 3x interval multiplier when `PerformanceMode` is active
2. Modified `ManualUpdate()` to use `GetDecisionFrequency()` instead of the hardcoded `DECISION_FREQUENCY`
3. Added `using SAIN.Plugin;` and `using SAIN.Preset.GlobalSettings;` for access to `PerformanceSettings`

```csharp
private float GetDecisionFrequency()
{
    AILimitSetting limit = Bot.CurrentAILimit;
    return limit switch
    {
        AILimitSetting.None => 1f / 10f,      // 10Hz - full speed
        AILimitSetting.Far => 1f / 5f,         // 5Hz - half speed
        AILimitSetting.VeryFar => 1f / 3f,     // 3Hz
        AILimitSetting.Narnia => 1f / 2f,      // 2Hz - minimal
        _ => 1f / 10f
    };
}
```

```csharp
if (_nextGetDecisionTime < Time.time)
{
    _nextGetDecisionTime = Time.time + GetDecisionFrequency();
    getDecision();
}
```

---

### 4E. Expanded Performance Settings

**File:** `SAIN\Preset\GlobalSettings\Categories\General\PerformanceSettings.cs`

**Replace PerformanceSettings.cs:**

```csharp
using SAIN.Attributes;

namespace SAIN.Preset.GlobalSettings;

public class PerformanceSettings : SAINSettingsBase<PerformanceSettings>, ISAINSettings
{
    [Name("Performance Mode")]
    [Description(
        "Master toggle for all performance optimizations. When ON, all sub-settings below are active. "
        + "When OFF, bot AI runs at full quality regardless of distance or bot count."
    )]
    public bool PerformanceMode = false;

    [Name("Vision Raycast Frequency")]
    [Description("How often vision raycasts update per second. Lower = less CPU, slower bot reactions. Range: 10-30 Hz")]
    [MinMax(10f, 30f, 1f)]
    [Advanced]
    public float VisionRaycastFrequency = 30f;

    [Name("Look Sensor Frequency")]
    [Description("How often the EFT look sensor updates per second. Lower = less CPU, slower target acquisition. Range: 10-30 Hz")]
    [MinMax(10f, 30f, 1f)]
    [Advanced]
    public float LookUpdateFrequency = 30f;

    [Name("Cover Finder Frequency")]
    [Description("How often bots search for cover per second when in combat. Lower = less CPU, slower cover finding. Range: 2-10 Hz")]
    [MinMax(2f, 10f, 1f)]
    [Advanced]
    public float CoverFindFrequency = 10f;

    [Name("Max Raycasts Per Body Part")]
    [Description("Number of raycast types per body part: 1=LoS only, 2=LoS+Vision, 3=LoS+Vision+Shoot. Lower = faster but may miss some checks.")]
    [MinMax(1, 3, 1)]
    [Advanced]
    public int MaxRaycastsPerEnemy = 3;

    [Name("Far Bot Vision Reduction")]
    [Description("Multiplier applied to vision-related CPU cost for bots in the 'Far' AI limit tier. 0.5 = half cost.")]
    [MinMax(0.1f, 1f, 100f)]
    [Advanced]
    public float FarBotCpuReduction = 0.5f;

    [Name("Very Far Bot Vision Reduction")]
    [Description("Multiplier applied to vision-related CPU cost for bots in the 'VeryFar' AI limit tier.")]
    [MinMax(0.1f, 1f, 100f)]
    [Advanced]
    public float VeryFarBotCpuReduction = 0.25f;

    [Name("Narnia Bot Vision Reduction")]
    [Description("Multiplier applied to vision-related CPU cost for bots in the 'Narnia' AI limit tier. Near-zero means almost no vision cost.")]
    [MinMax(0f, 1f, 100f)]
    [Advanced]
    public float NarniaBotCpuReduction = 0f;
}
```

**Wire Settings into VisionRaycastJob.cs:**

```csharp
private static PerformanceSettings PerfSettings => 
    SAINPlugin.LoadedPreset?.GlobalSettings?.General?.Performance;

// Replace constants with:
private float VisionJobInterval => 
    1f / (PerfSettings?.VisionRaycastFrequency ?? 30f);
private int RaycastChecks => 
    PerfSettings?.MaxRaycastsPerEnemy ?? 3;
```

---

### 4F. Hearing System Optimizations

**File:** `SAIN\Components\GameWorldComponent.cs`

**Implemented Changes:**

1. Introduced `GetBotCacheInterval()` to scale `_Sounds_BotCache_Interval` based on total bot count:
  - `> 15` bots: 1.5x interval
  - `> 30` bots: 2x interval
2. Modified `TickSoundCaches()` to use `GetBotCacheInterval()` instead of the fixed interval
3. Added `using SAIN.Plugin;` and `using SAIN.Preset.GlobalSettings;`

```csharp
private float GetBotCacheInterval()
{
    int botCount = 0;
    foreach (var pc in PlayerComponents)
    {
        if (pc?.BotComponent != null) botCount++;
    }
    
    float interval = _Sounds_BotCache_Interval;
    if (botCount > 30)
        interval *= 2f;
    else if (botCount > 15)
        interval *= 1.5f;
    
    return interval;
}
```

---

### 4G. Runaway Coroutine Fixes

**Files:** `VisionRaycastJob.cs`, `EnemyPlaceRaycastJob.cs`

#### VisionRaycastJob.cs — Runaway `UpdateEFTVision()` Loop

**Problem:** `while (BotController != null)` loops without a `_disposed` guard. When `BotController` reference is stale but non-null, the loop runs forever.

**Fix:**

```csharp
while (BotController != null && !_disposed)
```

#### EnemyPlaceRaycastJob.cs — Infinite `while(true)` Loop

**Problem:** Original code used `while (true)` which has no exit condition. Could loop forever if the spawn controller or bot manager is destroyed without cleaning up the job.

**Fix:**

```csharp
private bool _disposed;
// ...
while (!_disposed)
```

---

### 4H. Memory Leak & Bug Fixes

#### EnemyPlaceRaycastJob.cs — NativeArray Not Disposed on Exception

**Problem:** `NativeArray` allocations in `EnemyPlaceJobLoop()` are not wrapped in `try-finally`. If any exception occurs mid-method, the `NativeArray` is never disposed, causing a memory leak that accumulates over time (Unity safety system will eventually throw).

**Fix:** Wrapped all `NativeArray` allocations inside a `try-finally` block:

```csharp
while (!_disposed)
{
    NativeArray<RaycastCommand> commands = new(botCount, Allocator.TempJob);
    NativeArray<RaycastHit> results = new(botCount, Allocator.TempJob);
    try
    {
        // ... existing logic ...
    }
    finally
    {
        if (commands.IsCreated)
            commands.Dispose();
        if (results.IsCreated)
            results.Dispose();
    }
    yield return null;
}
```

#### EnemyPlaceRaycastJob.cs — Unnormalized Raycast Direction

**Problem:** An inline `RaycastCommand` used a non-normalized direction and a fixed distance of `1f`, causing incorrect raycast behavior.

**Fix:**

```csharp
var dir = randomPoint - eyePos;
commands[ownerIndex] = new RaycastCommand(eyePos, dir.normalized, dir.magnitude);
```

#### BotSpawnController.cs — Double-Dispose Bug in `RemoveBot()`

**File:** `SAIN\Classes\BotManager\BotSpawnController.cs`

**Problem:** `RemoveBot()` had two independent `if` blocks. If a bot's component component was null, the first `if` failed, but the second `if` could still call Dispose() on it. Additionally, `BotDictionary.Remove()` was called outside the disposal block.

**Fix:** Changed two `if` statements to `if/else if` structure, ensuring a bot component is disposed only once. Moved `BotDictionary.Remove()` inside the block that performs the disposal:

```csharp
if (botOwner.TryGetComponent(out var component) && component != null)
{
    component.Dispose();
    BotDictionary.Remove(botOwner.ProfileId);
}
else if (BotDictionary.ContainsKey(botOwner.ProfileId))
{
    BotDictionary.Remove(botOwner.ProfileId);
}
```

#### Enemy.cs — Unreachable Code in `IsEnemyValid()`

**File:** `SAIN\Classes\Bot\EnemyClasses\Enemy.cs`

**Problem:** The original code checked `enemyBotOwner == null` inside a block that already means `enemyBotOwner != null`, making the check always true and the code inside it unreachable.

**Fix:** Inverted the condition:

```csharp
if (enemyBotOwner == null)
{
    return false;
}
```

#### Enemy.cs — Empty Catch Block

**Problem:** `catch {}` catches all exceptions silently, hiding bugs and making debugging nearly impossible.

**Fix:**

```csharp
catch (NullReferenceException) { }
```

#### SAINEnemyController.cs — Empty Catch Block

**File:** `SAIN\Classes\Bot\EnemyControllers\SAINEnemyController.cs`

**Fix:**

```csharp
catch (NullReferenceException) { }
```

#### RaycastJob.cs — Unnormalized Directions

**File:** `SAIN\Types\Jobs\RaycastJob.cs`

**Problem:** In `CreateCommands(int Count, List<Vector3> Points, ...)`, the `Direction` field was not normalized and distance was fixed. Same issue in `PathVisionJob` constructors.

**Fix:**

```csharp
// In CreateCommands:
commands[i] = new RaycastCommand(Origin, Direction.normalized, Mathf.Max(Direction.magnitude, 0.01f), ...);

// Same fix for both PathVisionJob constructors (BotVisiblePathNode[] and List<BotVisiblePathNode>)
```

---

### 4I. Thread Safety & Static Arrays

**File:** `SAIN\Classes\Coverfinder\CoverAnalyzer.cs`

**Problem:** `private static Collider[] _playerColliderArray` is shared across threads for `Physics.OverlapSphereNonAlloc`. Unity's Burst-compiled job system may access this from multiple threads, causing race conditions and corrupted data.

**Fix:** Added `[ThreadStatic]` attribute and null-check initialization:

```csharp
[ThreadStatic]
private static Collider[] _playerColliderArray;

private void checkIfPlayerCollidersNear()
{
    if (_playerColliderArray == null)
        _playerColliderArray = new Collider[5];
    // ... existing logic using _playerColliderArray ...
}
```

---

### 4J. GC Pressure Reduction

#### Logger.cs — Unconditional Boxing in Release Builds

**File:** `SAIN\Logger.cs`

**Problem:** `LogError` and `LogWarning` unconditionally called `Log()` which boxes string arguments via `string.Format()`. In Release builds, this happens even though no actual logging occurs.

**Fix:** Added `#if DEBUG` guards around the `Log()` calls within `LogError()` and `LogWarning()`, matching the existing pattern in `LogInfo()` and `LogDebug()`:

```csharp
public static void LogError(String message)
{
#if DEBUG
    Log(message, LogLevel.Error);
#endif
}
```

#### HearingInputClass.cs — List.TrimExcess() After Every Batch

**File:** `SAIN\Classes\Bot\Sense\Hearing\HearingInputClass.cs`

**Problem:** `SoundDataToReactTo.TrimExcess()` was called after every sound batch processing, which reallocates the internal array to match the current count. With hundreds of sounds per frame, this causes frequent allocations and GC pressure.

**Fix:** Removed the `SoundDataToReactTo.TrimExcess()` call. The list naturally grows to accommodate peak sound counts and maintains capacity between frames. Additionally removed the now-unused `bool SoundRemoved` variable and its empty `if (SoundRemoved){}` block.

#### HearingDispersionClass.cs — New NavMeshPath per Iteration

**File:** `SAIN\Classes\Bot\Sense\Hearing\HearingDispersionClass.cs`

**Problem:** `NavMesh.CalculatePath` was creating new `NavMeshPath` instances inside a loop, generating unnecessary allocations for each sound dispersal calculation.

**Fix:** Declared a static `_randomPathBox` `NavMeshPath` instance and reused it by calling `path.ClearCorners()`:

```csharp
private static NavMeshPath _randomPathBox = new();

private Vector3 GetRandomReachablePointInBoxAroundPlayer()
{
    NavMeshPath path = _randomPathBox;
    path.ClearCorners();
    // ... use path with NavMesh.CalculatePath ...
}
```

---

### 4K. O(N²) → O(1) Enemy Lookup Optimization

**File:** `SAIN\Classes\Bot\EnemyControllers\SAINEnemyController.cs`

**Problem:** In `UpdateEnemies()`, the code iterated over `List<IPlayer> Allies` using `Allies.Contains()` in a loop, which is O(N²) as each `Contains()` scans the entire list.

**Fix:** Converted the ally list lookup to a `HashSet<string> _alliesHash` for O(1) lookups:

```csharp
private HashSet<string> _alliesHash = new();

// In UpdateEnemies, replace:
// if (Allies.Contains(player))  // O(N)
// With:
if (_alliesHash.Contains(player.ProfileId))  // O(1)
```

---

## 5. Phase 2: Missed Bottleneck Throttles

After implementing Phase 1, a second code review identified 7 additional high-impact bottlenecks that were missed. These focus on the deepest nested loops and the systems that bypassed existing throttling infrastructure.

### 5A. DirectionDataJob — Every-Frame O(P²) Job

**File:** `SAIN\Classes\BotManager\Jobs\DirectionDataJob.cs`

**Problem:**

- The `DirectionDataJobLoop()` coroutine uses bare `yield return null;` — runs every frame with no throttling
- Nested loop over all players: O(P²) complexity per frame
- For 30 players: ~900 iterations/frame = 54,000 checks/sec at 60fps
- This is the single most expensive per-frame system in SAIN

**Fix:**

- Added `private WaitForSeconds _directionDataWait;` field
- Implemented `GetDirectionDataWait()` returning `1f/30f` (base ~30Hz) or `1f/15f` (15Hz) if `PerformanceMode` is active
- Replaced all `yield return null;` instances with `yield return _directionDataWait;`

```csharp
private static WaitForSeconds GetDirectionDataWait()
{
    var perfSettings = SAINPlugin.LoadedPreset?.GlobalSettings?.General?.Performance;
    bool perfMode = perfSettings != null && perfSettings.PerformanceMode;
    return perfMode ? new WaitForSeconds(1f / 15f) : new WaitForSeconds(1f / 30f);
}
```

**Expected impact:** Reduces 1,800 checks/sec → 900 checks/sec (50% reduction) at 30Hz, or 540 checks/sec at 15Hz.

### 5B. EnemyPlaceRaycastJob — Untamed Every-Frame Job

**File:** `SAIN\Classes\BotManager\Jobs\EnemyPlaceRaycastJob.cs`

**Problem:**

- `EnemyPlaceJobLoop()` coroutine originally had `while (true)` (infinite loop) with bare `yield return null;` every iteration
- Every bot's enemy positions re-verified every single frame
- Phase 1 fixed the `while(true)` → `while(!_disposed)` but did NOT add any throttling interval

**Fix:**

- Added `private WaitForSeconds _enemyPlaceWait;` and `private float _lastEnemyPlaceInterval;` fields
- Implemented `GetEnemyPlaceWait()` returning `0.033f` (base ~30Hz) or `0.1f` (10Hz) if `PerformanceMode` is active
- Added `yield return GetEnemyPlaceWait();` at the end of the loop body (after the `finally` block)
- Recreates the `WaitForSeconds` object only when the interval changes to avoid allocation

```csharp
private WaitForSeconds GetEnemyPlaceWait()
{
    var perfSettings = SAINPlugin.LoadedPreset?.GlobalSettings?.General?.Performance;
    bool perfMode = perfSettings != null && perfSettings.PerformanceMode;
    float interval = perfMode ? 0.1f : 0.033f;
    if (Mathf.Abs(_lastEnemyPlaceInterval - interval) > 0.001f)
    {
        _lastEnemyPlaceInterval = interval;
        _enemyPlaceWait = new WaitForSeconds(interval);
    }
    return _enemyPlaceWait;
}
```

**Expected impact:** From 60Hz to 30Hz (50% reduction) or 10Hz (83% reduction in PerformanceMode).

### 5C. SAINBotUnstuckClass — Per-Bot Every-Frame Coroutine

**File:** `SAIN\Classes\Bot\SAINBotUnstuckClass.cs`

**Problem:**

- Runs as `_tickWhenActiveClass` per bot
- `BotUnstuck()` coroutine uses bare `yield return null;` — no interval
- Even when the bot is not stuck, the coroutine iterates every frame
- 30 bots × 60fps = 1,800 iterations/sec doing essentially nothing

**Fix:** Replaced `yield return null;` with a 0.25s `WaitForSeconds` to throttle to 4Hz:

```csharp
private IEnumerator BotUnstuck()
{
    WaitForSeconds wait = new(0.25f);
    while (true)
    {
        yield return wait;
    }
}
```

**Expected impact:** From 60Hz to 4Hz = 93% reduction in iterations per bot. For 30 bots: 1,800/sec → 120/sec.

### 5D. BotComponent.TickClassGroup — Bypassed ShallTick() Infrastructure

**File:** `SAIN\Components\BotComponent.cs`

**Problem:**

- `BotComponent.ManualUpdate()` calls `TickClassGroup()` for each tick group
- `TickClassGroup()` calls `botClass.ManualUpdate()` unconditionally in a loop
- The `IBotClass.ShallTick()` method exists but is never checked in `TickClassGroup()`
- Classes in `_alwaysTickClasses` (SAINEnemyController, SAINDecisionClass, etc.) fire every single frame

**Fix:** Added `ShallTick()` guard before calling `ManualUpdate()`:

```csharp
private static void TickClassGroup(List<IBotClass> List, float CurrentTime)
{
    for (int i = 0; i < List.Count; i++)
    {
        var botClass = List[i];
        if (botClass?.ShallTick(CurrentTime) == true)
        {
            botClass.ManualUpdate();
        }
    }
}
```

**Expected impact:** Classes that implement `ShallTick()` now respect their own time gating. Classes without `ShallTick()` (or returning `true`) remain unchanged.

### 5E. Enemy.ShallCheckLook / ShallCheckLoS — Redundant Timers & Validations

**File:** `SAIN\Classes\Bot\EnemyClasses\Enemy.cs`

**Problem:**

- `ShallCheckLook()` and `ShallCheckLoS()` have separate timers (`_nextCheckLookTime` and `_nextCheckLoSTime`)
- Each calls `CheckValid()` internally — redundant when both are called together
- In practice, look and LoS checks are always requested together, so two timers waste cycles

**Fix:**

- Merged both methods: `ShallCheckLoS()` now delegates to `ShallCheckLook()`
- Removed the unused `private float _nextCheckLoSTime` field
- Both now share `_nextCheckLookTime` and a single `CheckValid()` call

```csharp
public bool ShallCheckLook(float currentTime, out float deltaTime)
{
    if (!CheckValid())
    {
        deltaTime = 0f;
        return false;
    }
    if (_nextCheckLookTime < currentTime)
    {
        deltaTime = currentTime - _nextCheckLookTime;
        _nextCheckLookTime = currentTime + LookUpdateInterval();
        return true;
    }
    deltaTime = 0f;
    return false;
}

public bool ShallCheckLoS(float currentTime)
{
    return ShallCheckLook(currentTime, out _);
}
```

**Expected impact:** Eliminates redundant `CheckValid()` calls (every enemy, every frame both were called). Removes one timer field + update logic. Simplifies call sites.

### 5F. SAINEnemyController.UpdateEnemies — Every-Frame Full Iteration

**File:** `SAIN\Classes\Bot\EnemyControllers\SAINEnemyController.cs`

**Problem:**

- `UpdateEnemies()` runs via `_alwaysTickClasses` — every frame
- Iterates all known enemies, updates distances, checks validity
- No time gate or interval check at the method level

**Fix:** Added a time gate with dynamic interval (20Hz base, 10Hz in PerformanceMode):

```csharp
private float _nextEnemyUpdateTime;

public void UpdateEnemies(BotComponent bot, float currentTime)
{
    if (_nextEnemyUpdateTime > currentTime)
        return;
    
    var perfSettings = SAINPlugin.LoadedPreset?.GlobalSettings?.General?.Performance;
    _nextEnemyUpdateTime = currentTime + ((perfSettings != null && perfSettings.PerformanceMode) ? 0.1f : 0.05f);
    
    // ... existing iteration logic ...
}
```

**Expected impact:** From 60Hz full iteration to 20Hz (67% reduction) or 10Hz (83% reduction in PerformanceMode).

### 5G. BotSquadClass.checkVisibleMembers — Blocking Physics.Raycast per Squad Member

**File:** `SAIN\Classes\Bot\Info\BotSquadClass.cs`

**Problem:**

- `checkVisibleMembers()` fires every ~1 second
- For each squad member, issues blocking `Physics.Raycast()` on the main thread
- Worst case for large squads: 5-10 blocking raycasts per bot
- 30 bots × 10 members = 300 blocking raycasts/sec on main thread

**Fix:** Replaced blocking `Physics.Raycast()` calls with Unity Job System (`RaycastCommand.ScheduleBatch`):

```csharp
private void checkVisibleMembers()
{
    VisibleMembers.Clear();
    Vector3 eyePos = Bot.Transform.EyePosition;
    List<BotComponent> candidates = new();
    foreach (var member in Members.Values)
    {
        if (member != null && member.GetDistanceToPlayer(Bot.ProfileId) <= CHECK_VISIBLE_MEMBERS_DISTANCE)
        {
            candidates.Add(member);
        }
    }
    if (candidates.Count == 0)
        return;
    int count = candidates.Count;
    NativeArray<RaycastCommand> commands = new(count, Allocator.TempJob);
    NativeArray<RaycastHit> hits = new(count, Allocator.TempJob);
    try
    {
        for (int i = 0; i < count; i++)
        {
            Vector3 direction = candidates[i].Transform.BodyPosition - eyePos;
            float distance = direction.magnitude;
            Vector3 directionNormal = distance > 0f ? direction / distance : Vector3.forward;
            commands[i] = new RaycastCommand(
                eyePos,
                directionNormal,
                new QueryParameters { layerMask = LayerMaskClass.HighPolyWithTerrainMask },
                Mathf.Max(distance, 0.01f)
            );
        }
        var handle = RaycastCommand.ScheduleBatch(commands, hits, 32);
        handle.Complete();
        for (int i = 0; i < count; i++)
        {
            if (hits[i].collider == null)
            {
                VisibleMembers.Add(candidates[i]);
            }
        }
    }
    finally
    {
        if (commands.IsCreated) commands.Dispose();
        if (hits.IsCreated) hits.Dispose();
    }
}
```

**Expected impact:** Moves raycast computations to worker threads. Reduces main thread hitches. Up to 300 blocking raycasts/sec removed from main thread for 30 bots.

---

## 6. Phase 3: Remaining Performance Optimizations + Bug Fixes

> **Summary:** 16 items: 8 real bug fixes (4 HIGH severity, 4 MEDIUM) and 8 GC/code improvements. Builds on Phases 1 & 2 CPU-cycle reductions with memory safety and allocation fixes.

### 6A. Bug Fixes (CRITICAL)

---

#### Bug 1: Duplicate Dead-Code `for` Loop — SAINEnemyController.cs

**File:** `SAIN\Classes\Bot\EnemyControllers\SAINEnemyController.cs`

**Problem:** `SelectVisibleEnemy()` had a duplicated `for` loop block that was **identical** to the explicit logic already performed above. The duplicate loop iterated `VisibleEnemies` and checked each against `_goalEnemy` with distance comparisons — dead code that still executed every time an enemy was selected.

**Fix:** Removed the entire duplicate `for` loop block (lines 472-491). The method now returns `closestVisibleEnemy` directly after the shooter checks.

**Severity:** HIGH — wasted CPU cycles per bot per enemy selection.

---

#### Bug 2: NativeArray Partial Allocation Leak — EnemyPlaceRaycastJob.cs

**File:** `SAIN\Classes\BotManager\Jobs\EnemyPlaceRaycastJob.cs`

**Problem:** `NativeArray` variables (`PlacePositions`, `BotPositions`, `EnemyPositions`, `PlaceDistancesToBot`, `PlaceDistancesToEnemy`) were declared **outside** the `try` block as `default` but assigned **inside** it. If one allocation succeeded but the next threw, previously-allocated arrays leaked because `EnemyPlaceJob` was never assigned from its default, so `EnemyPlaceJob.Dispose()` in the `finally` block saw `default` struct fields.

**Fix:** Moved all 5 NativeArray allocations into the `CalcEnemyPlaceJob` object initializer inside the `try` block. If any allocation throws, the struct's fields for previously-allocated arrays are already assigned, and `EnemyPlaceJob.Dispose()` properly disposes them via `IsCreated` checks.

**Severity:** HIGH — NativeArray memory leak that accumulates over long sessions.

---

#### Bug 3: NativeArray Not Disposed on Exception — RaycastJob.cs

**File:** `SAIN\Types\Jobs\RaycastJob.cs`

**Problem:** All 4 overloads of static `CreateCommands()` allocated a `NativeArray<RaycastCommand>` (`Allocator.TempJob`) and filled it in a loop. If the loop threw, the array was never disposed — no `try-finally` existed.

**Fix:** Wrapped each allocation + loop in `try-catch` that disposes the NativeArray on exception and re-throws.

**Severity:** HIGH — NativeArray leak on any exception in these frequently-called methods.

---

#### Bug 4: Static Instance Set Before Full Init — BotSpawnController.cs

**File:** `SAIN\Classes\BotManager\BotSpawnController.cs`

**Problem:** `Instance = this` was set before the constructor's event subscription completed.

**Fix:** Moved `Instance = this;` to the end of the constructor, after all initialization.

**Severity:** MEDIUM — NRE risk from partial init.

---

#### Bug 5: Empty `catch {}` — SAINMemoryClass.cs

**File:** `SAIN\Classes\Bot\Memory\SAINMemoryClass.cs`

**Problem:** Empty `catch {}` swallowed all exceptions from `SetUnderFire()`. Code outside the `try` block then accessed `enemy.EnemyPlayer` again without protection, causing cascading NRE.

**Fix:** Replaced empty catch with a null guard on `enemy?.EnemyPlayer`.

**Severity:** MEDIUM — hid real bugs, caused cascading failures.

---

#### Bug 6: Empty `catch {}` — VisionPatches.cs

**File:** `SAIN\Patches\VisionPatches.cs`

**Problem:** Two empty `catch {}` blocks swallowed `MissingReferenceException` from destroyed Unity objects.

**Fix:** Narrowed to `NullReferenceException` and `MissingReferenceException` only.

**Severity:** MEDIUM — hid bugs indicating improper lifecycle management.

---

#### Bug 7: `Enemy.IsAI` / `IsZombie` NRE — Enemy.cs

**File:** `SAIN\Classes\Bot\EnemyClasses\Enemy.cs`

**Problem:** `IsAI` and `IsZombie` accessed `EnemyPlayer` without null checks. Called from dozens of sites.

**Fix:** Added `?.` / `??` null-conditional access. Also fixed `IsShooter()`.

**Severity:** HIGH — NRE thrown from many call sites when enemy player is disposed.

---

#### Bug 8: Static Event Subscription Leak — SAINEnableClass.cs

**File:** `SAIN\Plugin\SAINEnableClass.cs`

**Problem:** `OnIPlayerDeadOrUnspawn` subscriptions leaked when bots extracted without dying. No tracking of subscribed players.

**Fix:** Added `_subscribedPlayers` tracking set. `Clear()` now unsubscribes all tracked handlers. `ClearBot()` removes from tracking set.

**Severity:** HIGH — event subscription leak per raid restart.

---

### 6B. GC & Code Improvements

---

#### A1. DirectionDataJob — Cache `WaitForSeconds`

**File:** `SAIN\Classes\BotManager\Jobs\DirectionDataJob.cs`

**Problem:** `GetDirectionDataWait()` created a new `WaitForSeconds` every iteration (~30Hz).

**Fix:** Added `_lastDirectionDataInterval` tracking with interval-change detection. Only allocates when the interval changes (e.g., performance mode toggle).

**Impact:** Eliminates ~30 allocations/second.

---

#### A2. CoverFinderComponent — Cache `WaitForSeconds`

**File:** `SAIN\Components\CoverFinderComponent.cs`

**Problem:** `WaitForSeconds` allocated inside the while loop on every cover search cycle.

**Fix:** Added `_coverFindWait` + `_lastCoverFindInterval` with change detection.

**Impact:** Eliminates per-cycle allocation for every bot's cover finder.

---

#### A3. VisionRaycastJob — Cache `WaitForSeconds`

**File:** `SAIN\Classes\BotManager\Jobs\VisionRaycastJob.cs`

**Problem:** `WaitForSeconds` allocated every `EnemyVisionJob()` iteration. Also removed a dead allocation in `UpdateEFTVision()` (created but never yielded).

**Fix:** Added `_visionJobWait` + `_lastVisionJobInterval` with change detection.

**Impact:** Eliminates ~30 allocations/second from the global vision job.

---

#### A4. SAINBotUnstuckClass — Static Readonly `WaitForSeconds`

**File:** `SAIN\Classes\Bot\SAINBotUnstuckClass.cs`

**Fix:** `private static readonly WaitForSeconds _unstuckWait = new(0.25f);`

**Impact:** Eliminates per-bot coroutine restart allocation.

---

#### A5. BotHearingClass — Static Readonly `WaitForSeconds`

**File:** `SAIN\Classes\BotManager\BotHearingClass.cs`

**Fix:** `private static readonly WaitForSeconds _soundDelay = new(0.1f);`

**Impact:** Eliminates per-sound-event allocation.

---

#### C2. GrenadeController — Reuse List Buffer

**File:** `SAIN\Components\GrenadeController.cs`

**Fix:** Added `_relevantPlayersBuffer` member field, cleared and reused per grenade throw instead of allocating a new `List<>`.

**Impact:** Zero allocations per grenade throw instead of one list + internal array.

---

#### B1-B4. HashSet Enumeration Allocations — Snapshot List Pattern

**Files:**
- B1: `GameWorldComponent.cs` (ManualUpdate loop over `AlivePlayerArray`)
- B2: `BotManagerComponent.cs` (ManualUpdate loop over `SAINBots`)
- B3: `DirectionDataJob.cs` (DirectionDataJobLoop over `AlivePlayerArray`)
- B4: `GameWorldComponent.cs` (TickSoundCaches over `AlivePlayerArray`)

**Problem:** `foreach` over `HashSet<T>` in hot paths (every frame / every coroutine iteration) allocates an enumerator each time.

**Fix:** Added per-component snapshot `List<T>` fields lazily synced when the HashSet count changes. Iteration uses `for` loops, eliminating all enumeration allocations.

```csharp
// Pattern: snapshot list lazily synced on count change
private readonly List<PlayerComponent> _playerListSnapshot = [];
private int _lastPlayerSnapshotCount = -1;

if (players.Count != _lastPlayerSnapshotCount)
{
    _playerListSnapshot.Clear();
    _playerListSnapshot.AddRange(players);
    _lastPlayerSnapshotCount = players.Count;
}
for (int i = 0; i < _playerListSnapshot.Count; i++) { ... }
```

Applied to `GameWorldComponent` (`_playerListSnapshot`), `BotManagerComponent` (`_botListSnapshot`), and `DirectionDataJob` (`_directionDataPlayers`).

**Impact:** Zero allocation per frame for these critical iteration paths. Up to ~100+ allocations/second eliminated.

---

## 7. AI Limit Tie-In System

The AI limit system (`SAINAILimit.cs`) is the foundation for all these optimizations. It determines a bot's limit tier based on distance from the nearest human player:


| Tier      | Default Distance | Optimization Level                 |
| --------- | ---------------- | ---------------------------------- |
| `None`    | < 150m           | Full AI, all systems               |
| `Far`     | 150-250m         | Reduced vision, lower update rates |
| `VeryFar` | 250-400m         | Minimal vision, no cover finding   |
| `Narnia`  | > 400m           | Bare minimum AI                    |


**The key integration point:** Each optimization should check `Bot.CurrentAILimit` (or `enemy.Bot.CurrentAILimit` for enemy-scoped checks) and adjust its behavior accordingly.

**In `SAINAILimit.cs`, the tiers are stored as:**

```csharp
public enum AILimitSetting
{
    None,      // Full processing
    Far,       // Reduced
    VeryFar,   // Minimal
    Narnia,    // Bare minimum
}
```

---

## 8. Mod Compatibility

All changes across both phases are **fully compatible** with common SPT AI mods (BigBrain, LootingBot, QuestingBot, etc.):

### BigBrain Compatibility


| SAIN Change                                                       | BigBrain Impact | Reasoning                                                                                    |
| ----------------------------------------------------------------- | --------------- | -------------------------------------------------------------------------------------------- |
| Coroutine throttling (Vision, DirectionData, EnemyPlace, Unstuck) | None            | BigBrain manages layer execution order; SAIN coroutines are internal timing mechanisms.      |
| TickClassGroup ShallTick() wiring                                 | None            | BigBrain's `BotLayerPriority` and `CustomBotLayer` decisions are unaffected.                 |
| ShallCheckLook/LoS merge                                          | None            | BigBrain calls `SAINBotSearchData` which uses SAIN's enemy info. Method signature unchanged. |
| Job System batching (Squad Raycasts)                              | None            | `RaycastCommand.ScheduleBatch` is Unity-side. BigBrain layers see the same squad data.       |
| PerformanceSettings additions                                     | None            | Settings are opt-in, defaulted to original values. No breaking changes.                      |


### LootingBot & QuestingBot Compatibility


| SAIN Change        | LootingBot/QuestingBot Impact | Reasoning                                                                                            |
| ------------------ | ----------------------------- | ---------------------------------------------------------------------------------------------------- |
| All timing changes | None                          | LootingBot and QuestingBot are separate Harmony-patched systems with their own coroutines.           |
| Enemy data changes | None                          | `Enemy.ShallCheckLook` / `ShallCheckLoS` maintain the same public API.                               |
| Hearing changes    | None                          | `HearingInputClass` and `HearingDispersionClass` are internal to SAIN. Sound events still propagate. |


### General Compatibility Principles

1. **No public API changes** — All modified methods maintain their original signatures
2. **No removed functionality** — All throttling is opt-in via `PerformanceSettings` defaults
3. **No Harmony patch changes** — All modifications are in SAIN's internal systems
4. **Settings backwards compatible** — New settings have defaults matching original behavior

---

## 9. Testing & Validation

### Test Scenarios

1. **Factory (small map):** ~10 bots, tight quarters — test for micro-stutters
2. **Customs (medium map):** ~20-30 bots — test for general framerate
3. **Streets/Lighthouse (large map):** ~30-50 bots — test for major CPU savings
4. **Test with AI vs AI:** Spectate a fight between distant bots — verify they still behave reasonably

### Performance Measurement

- Use Unity Profiler (if available): Window → Analysis → Profiler
- Check `Time.deltaTime` / frame time before and after changes
- Count active bots via F6 debug overlay
- Monitor `RaycastCommand.ScheduleBatch` duration in profiler

### Behavioral Validation

After changes, verify bots still:

- React to enemies within expected timeframes
- Take cover when shot at
- Search for enemies they've seen
- Communicate with squad members
- Use suppression fire

---

## 11. Custom Preset Creation

### Overview

A custom preset **"My Tuned Preset"** was created based on **"Default with Harder PMCs"** (`SAINDifficulty.harderpmcs`) and then tuned to match **"I Like Pain" (veryhard)** difficulty with a reduced hearing range of 50% to prevent bots from pre-aiming through walls.

### The Problems Addressed

**1. Bots walk too close before shooting:** The engagement distance per weapon class was too low (especially SMG 70m, Pistol 50m, Shotgun 50m), causing bots to walk right up to the player before deciding to fire.

**2. Bots pre-aim through walls:** Even with 9x39 subsonic ammo and careful movement, bots would hear footsteps/gunshots, track the player through walls, and pre-aim before exiting cover or entering doorways. This is caused by `HearingDistanceCoef = 1.0` (full hearing range).

**3. Raiders (Labs) sneak excessively:** Raiders share the PMC brain but need their own engagement distance tuning.

### Preset Tuning Changes

#### Global Settings (from veryhard / "I Like Pain")

| Setting | Default (harderpmcs) | My Tuned Preset |
|---|---|---|
| `BOT_RECOIL_COEF` | 1.0 | **0.75** |
| `ScatteringCoef` | 0.75 | **0.55** |
| `AimCenterMassGlobal` | true | **false** |
| `VisibleDistCoef` | 1.0 | **1.25** |
| `GainSightCoef` | 1.0 | **1.25** |
| `PRECISION_SPEED_COEF` | 1.0 | **1.25** |
| `ACCURACY_SPEED_COEF` | 0.8 | **0.6** |
| `HearingDistanceCoef` | 1.0 | **0.5** (half hearing range) |

#### Per-Bot Overrides (ALL bot types)

| Setting | Default | My Tuned Preset |
|---|---|---|
| `VisibleAngle` | varies (120-170) | **170 for all** |
| `FireratMulti` | 1.0 | **1.5** |
| `BurstMulti` | 1.0 | **2.0** |
| `AimCenterMass` | varies | **false for all** |
| `DifficultyModifier` | varies | **x1.33** (clamped to 2.0) |

#### Strafe Speeds

Bosses, PMCs, Raiders, Rogues, Arena Fighters, Followers:

| Difficulty | Default | My Tuned Preset |
|---|---|---|
| easy | 0.55 | **0.75** |
| normal | 0.75 | **0.85** |
| hard | 0.80 | **0.90** |
| impossible | 1.0 | **1.0** |

Scavs (assault/assaultGroup):

| Difficulty | Default | My Tuned Preset |
|---|---|---|
| easy | 0.50 | **0.65** |
| normal | 0.55 | **0.70** |
| hard | 0.60 | **0.75** |
| impossible | 0.65 | **0.90** |

#### PMC-Specific (from ApplyHarderPMCs)

| Setting | Default (harderpmcs) | My Tuned Preset |
|---|---|---|
| `WeaponProficiency` | 0.75 | same |
| `ScatteringCoef` | 0.6 | same |
| `PRECISION_SPEED_COEF` | 1.33 | same |
| `ACCURACY_SPEED_COEF` | 0.6 | same |
| `GainSightCoef` | 1.25 | same |
| `VisibleDistCoef` | 1.25 | same |
| `AggressionCoef` | 1.2 | same |
| `AimCenterMass` | false | same |
| `MAX_AIM_TIME` (easy) | 1.5s | same |
| `MAX_AIM_TIME` (normal) | 1.35s | same |
| `MAX_AIM_TIME` (hard) | 1.15s | same |
| `MAX_AIM_TIME` (impossible) | 1.0s | same |

#### Extended Engagement Distances

| Weapon Class | Default Value | My Tuned Preset |
|---|---|---|
| Default | 125m | **200m** |
| Assault Carbine | 125m | **200m** |
| Assault Rifle | 150m | **250m** |
| Machinegun | 125m | **200m** |
| SMG | 70m | **150m** |
| Pistol | 50m | **125m** |
| Marksman Rifle | 175m | **250m** |
| Sniper Rifle | 300m | **250m** (capped) |
| Shotgun | 50m | **125m** |
| Grenade Launcher | 100m | **175m** |
| Special Weapon | 100m | **175m** |

#### Raider Engagement Distance

Raiders (`pmcBot`) get a 2x multiplier via `RaiderEngagementDistanceMultiplier`, implemented in `BotWeaponInfoClass.EffectiveWeaponDistance`. This means a Raider with an SMG engages at 300m (150m x 2) instead of 150m.

### Key Design Decisions

- **`HearingDistanceCoef = 0.5`** cuts all hearing detection distances in half. Footsteps, gunshots, reloads, etc. are detected at 50% range. Combined with the existing 0.6 footstep/suppressed-shot occlusion modifiers, footsteps through walls are very hard to hear.
- **All bot types** get the veryhard treatment — bosses, raiders, scavs, PMCs all upgraded equally.
- **Base difficulty preserved** — the preset still has `BaseSAINDifficulty = harderpmcs` with `IsCustom = true`.
- **One-time creation** — runs only on first launch; detected via `PresetHandler.CustomPresetOptions`.

### Implementation Code

**File:** `SAIN\SAINPlugin.cs` — method `TryCreateCustomPreset()`:

```csharp
private static void TryCreateCustomPreset()
{
    const string presetName = "My Tuned Preset";
    if (PresetHandler.CustomPresetOptions.Exists(p => p.Name == presetName))
        return;

    var baseDef = SAINDifficultyClass.DefaultPresetDefinitions[SAINDifficulty.harderpmcs];
    var newDef = baseDef.Clone();
    newDef.Name = presetName;
    newDef.Description = "Custom preset based on Harder PMCs with veryhard difficulty tuning. Hearing reduced to 50% to prevent wall pre-aiming.";
    newDef.Creator = "user";

    PresetHandler.SavePresetDefinition(newDef);
    PresetHandler.InitPresetFromDefinition(newDef, true);

    var global = PresetHandler.LoadedPreset.GlobalSettings;

    // === Apply veryhard-level global settings ===
    global.Shoot.BOT_RECOIL_COEF = 0.75f;
    global.Difficulty.ScatteringCoef = 0.55f;
    global.Aiming.AimCenterMassGlobal = false;
    global.Difficulty.VisibleDistCoef = 1.25f;
    global.Difficulty.GainSightCoef = 1.25f;
    global.Difficulty.PRECISION_SPEED_COEF = 1.25f;
    global.Difficulty.ACCURACY_SPEED_COEF = 0.6f;

    // === Reduce hearing to prevent wall pre-aiming ===
    global.Difficulty.HearingDistanceCoef = 0.5f;

    // === Apply Harder PMCs bonuses ===
    var botSettings = PresetHandler.LoadedPreset.BotSettings;
    foreach (var botsetting in botSettings.SAINSettings)
    {
        if (botsetting.Key == WildSpawnType.pmcUSEC || botsetting.Key == WildSpawnType.pmcBEAR)
        {
            foreach (var diff in botsetting.Value.Settings.Values)
            {
                diff.Mind.WeaponProficiency = 0.75f;
                diff.Difficulty.ScatteringCoef = 0.6f;
                diff.Difficulty.PRECISION_SPEED_COEF = 1.33f;
                diff.Difficulty.ACCURACY_SPEED_COEF = 0.6f;
                diff.Difficulty.GainSightCoef = 1.25f;
                diff.Difficulty.VisibleDistCoef = 1.25f;
                diff.Difficulty.AggressionCoef = 1.2f;
                diff.Aiming.AimCenterMass = false;
            }
            var pmcSettings = botsetting.Value.Settings;
            pmcSettings[BotDifficulty.easy].Aiming.MAX_AIM_TIME = 1.5f;
            pmcSettings[BotDifficulty.normal].Aiming.MAX_AIM_TIME = 1.35f;
            pmcSettings[BotDifficulty.hard].Aiming.MAX_AIM_TIME = 1.15f;
            pmcSettings[BotDifficulty.impossible].Aiming.MAX_AIM_TIME = 1.0f;
        }
    }

    // === Apply veryhard-level per-bot overrides ===
    foreach (var botsetting in botSettings.SAINSettings)
    {
        botsetting.Value.DifficultyModifier = Mathf.Clamp(botsetting.Value.DifficultyModifier * 1.33f, 0.01f, 2f);
        foreach (var setting in botsetting.Value.Settings)
        {
            setting.Value.Core.VisibleAngle = 170f;
            setting.Value.Shoot.FireratMulti = 1.5f;
            setting.Value.Shoot.BurstMulti = 2f;
            setting.Value.Aiming.AimCenterMass = false;
        }
    }

    // === Apply veryhard-level strafe speeds ===
    foreach (var botsetting in botSettings.SAINSettings)
    {
        var settings = botsetting.Value.Settings;
        if (botsetting.Key.IsBossOrFollower()
            || botsetting.Key.IsPmcBot()
            || botsetting.Key == WildSpawnType.exUsec
            || botsetting.Key == WildSpawnType.pmcBot
            || botsetting.Key == WildSpawnType.arenaFighter
            || botsetting.Key == WildSpawnType.arenaFighterEvent)
        {
            settings[BotDifficulty.easy].Move.STRAFE_SPEED = 0.75f;
            settings[BotDifficulty.normal].Move.STRAFE_SPEED = 0.85f;
            settings[BotDifficulty.hard].Move.STRAFE_SPEED = 0.9f;
            settings[BotDifficulty.impossible].Move.STRAFE_SPEED = 1.0f;
        }
        else if (botsetting.Key == WildSpawnType.assault || botsetting.Key == WildSpawnType.assaultGroup)
        {
            settings[BotDifficulty.easy].Move.STRAFE_SPEED = 0.65f;
            settings[BotDifficulty.normal].Move.STRAFE_SPEED = 0.7f;
            settings[BotDifficulty.hard].Move.STRAFE_SPEED = 0.75f;
            settings[BotDifficulty.impossible].Move.STRAFE_SPEED = 0.9f;
        }
    }

    // === Extended engagement distances ===
    var engagement = global.Shoot.EngagementDistance;
    engagement[EWeaponClass.Default] = 200f;
    engagement[EWeaponClass.assaultCarbine] = 200f;
    engagement[EWeaponClass.assaultRifle] = 250f;
    engagement[EWeaponClass.machinegun] = 200f;
    engagement[EWeaponClass.smg] = 150f;
    engagement[EWeaponClass.pistol] = 125f;
    engagement[EWeaponClass.marksmanRifle] = 250f;
    engagement[EWeaponClass.sniperRifle] = 250f;
    engagement[EWeaponClass.shotgun] = 125f;
    engagement[EWeaponClass.grenadeLauncher] = 175f;
    engagement[EWeaponClass.specialWeapon] = 175f;

    // === Raider-specific distance multiplier ===
    global.Shoot.RaiderEngagementDistanceMultiplier = 2f;

    SAINPresetClass.ExportAll(PresetHandler.LoadedPreset);
}
```

### Files Affected

| File | Change |
|---|---|
| `SAIN\SAINPlugin.cs` | Rewrote `TryCreateCustomPreset()` with veryhard tuning + `HearingDistanceCoef = 0.5` + `using EFT;` |
| `SAIN\SAIN\Preset\GlobalSettings\Categories\ShootSettings.cs` | Added `RaiderEngagementDistanceMultiplier` field (`[MinMax(1f, 5f, 1f)]`, `[Advanced]`) |
| `SAIN\SAIN\Classes\Bot\Info\BotWeaponInfoClass.cs` | Modified `EffectiveWeaponDistance` to apply multiplier for `pmcBot` raiders; added `using EFT;` |
| `Presets/My Tuned Preset/GlobalSettings.json` | Generated on disk with all modified settings |

---






| File                                                                   | Purpose                                                 | Priority         |
| ---------------------------------------------------------------------- | ------------------------------------------------------- | ---------------- |
| `SAIN\Classes\BotManager\Jobs\VisionRaycastJob.cs`                     | Central vision raycast system + WFS cache (Phase 3)     | **P1**           |
| `SAIN\Classes\BotManager\Jobs\DirectionDataJob.cs`                     | O(P²) player direction calc + WFS cache (Phase 3)      | **P1** (P3)      |
| `SAIN\Classes\BotManager\Jobs\EnemyPlaceRaycastJob.cs`                 | Enemy position raycast + NativeArray leak fix (P3)      | **P2** (P3)      |
| `SAIN\Classes\Bot\Sense\SAINBotLookClass.cs`                           | EFT look sensor bridge (main thread heavy)              | **P2**           |
| `SAIN\Components\CoverFinderComponent.cs`                              | Per-bot cover finding + WFS cache (Phase 3)             | **P3**           |
| `SAIN\Classes\Bot\Decision\BotDecisionManager.cs`                      | Decision frequency controller                           | **P4**           |
| `SAIN\Preset\GlobalSettings\Categories\General\PerformanceSettings.cs` | Performance config (expanded both phases)                | **P4**           |
| `SAIN\Components\GameWorldComponent.cs`                                | Sound prop, global tick, HashSet snapshot (Phase 3)     | P5               |
| `SAIN\Components\BotManagerComponent.cs`                               | Bot tick orchestrator + bot snapshot list (Phase 3)     | Reference        |
| `SAIN\Components\BotComponent.cs`                                      | Tick groups, ShallTick wiring (Phase 2)                 | **P2** (Phase 2) |
| `SAIN\Classes\Bot\BotBase.cs`                                          | Base tick system (ShallTick, TickInterval)              | Reference        |
| `SAIN\Classes\Bot\SAINAILimit.cs`                                      | AI limit tier system                                    | **Key enabler**  |
| `SAIN\Classes\Bot\SAINActivationClass.cs`                              | Bot active/standby states                               | Reference        |
| `SAIN\Classes\Bot\EnemyClasses\Enemy.cs`                               | Per-enemy data + null-guard IsAI/IsZombie (Phase 3)     | **P2** (P3)      |
| `SAIN\Classes\Bot\EnemyControllers\SAINEnemyController.cs`             | Enemy list mgmt + duplicate loop removal (Phase 3)      | **P2** (P3)      |
| `SAIN\Classes\Bot\Info\BotSquadClass.cs`                               | Squad visibility with job-batched raycasts (Phase 2)    | **P2** (Phase 2) |
| `SAIN\Classes\Bot\SAINBotUnstuckClass.cs`                              | Bot unstuck coroutine + static WFS (Phase 3)            | **P2** (P3)      |
| `SAIN\Classes\BotManager\BotSpawnController.cs`                        | Bot spawn lifecycle + safe static init (Phase 3)        | Reference        |
| `SAIN\Types\Jobs\RaycastJob.cs`                                        | Raycast commands + try-finally NativeArray (Phase 3)    | Reference        |
| `SAIN\Classes\Coverfinder\CoverAnalyzer.cs`                            | Cover collider analysis (thread safety fix)             | Reference        |
| `SAIN\Logger.cs`                                                       | Logging system (GC pressure fix)                        | Reference        |
| `SAIN\Classes\Bot\Sense\Hearing\HearingInputClass.cs`                  | Sound input processing (TrimExcess removal)             | Reference        |
| `SAIN\Classes\Bot\Sense\Hearing\HearingDispersionClass.cs`             | Sound dispersion/pathing (NavMeshPath reuse)            | Reference        |
| `SAIN\Preset\GlobalSettings\Categories\General\AILimitSettings.cs`     | AI limit config values                                  | Reference        |
| `SAIN\Preset\GlobalSettings\Categories\General\CoverSettings.cs`       | Cover system config                                     | Reference        |
| `SAIN\Preset\GlobalSettings\GlobalSettingsClass.cs`                    | Global settings container                               | Reference        |
| `SAIN\Components\BotComponentBase.cs`                                  | MonoBehaviour base class                                | Reference        |
| `SAIN\Patches\GameWorld\WorldTickPatch.cs`                             | Hook into EFT game tick                                 | Reference        |
| `SAIN\Classes\Bot\Memory\SAINMemoryClass.cs`                           | Memory class + empty catch fix (Phase 3)                | Reference        |
| `SAIN\Patches\VisionPatches.cs`                                        | Vision patches + narrowed catch (Phase 3)               | Reference        |
| `SAIN\Plugin\SAINEnableClass.cs`                                       | SAIN enable/disable + event leak fix (Phase 3)          | Reference        |
| `SAIN\Components\GrenadeController.cs`                                 | Grenade handling + reused list buffer (Phase 3)         | Reference        |
| `SAIN\SAINPlugin.cs`                                                    | Veryhard preset creation + hearing reduction + engagement tuning      | Reference        |
| `SAIN\SAIN\Preset\GlobalSettings\Categories\ShootSettings.cs`           | Raider engagement distance multiplier config setting      | Reference        |
| `SAIN\SAIN\Classes\Bot\Info\BotWeaponInfoClass.cs`                     | EffectiveWeaponDistance raider multiplier logic           | Reference        |
| `SAIN\SAIN\Preset\GlobalSettings\Categories\DifficultySettings.cs`     | Global HearingDistanceCoef configuration                  | Reference        |


