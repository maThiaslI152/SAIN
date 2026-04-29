global using EFTMath = GClass856;
using BepInEx;
using BepInEx.Configuration;
using EFT;
using SAIN.Editor;
using SAIN.Helpers;
using SAIN.Plugin;
using SAIN.Preset;
using SAIN.Preset.GlobalSettings;
using SPT.Reflection.Patching;
using UnityEngine;
using static SAIN.AssemblyInfoClass;

namespace SAIN;

[BepInPlugin(SAINGUID, SAINName, SAINVersion)]
[BepInDependency(BigBrainGUID, BigBrainVersion)]
//[BepInDependency(SPTGUID, SPTVersion)]
[BepInProcess(EscapeFromTarkov)]
[BepInIncompatibility("com.dvize.BushNoESP")]
[BepInIncompatibility("com.dvize.NoGrenadeESP")]
public class SAINPlugin : BaseUnityPlugin
{
    private PatchManager _patchManager;

    public static DebugSettings DebugSettings
    {
        get { return LoadedPreset.GlobalSettings.General.Debug; }
    }

    public static bool DebugMode
    {
        get { return DebugSettings.Logs.GlobalDebugMode; }
    }

    public static bool ProfilingMode
    {
        get { return DebugSettings.Logs.GlobalProfilingToggle; }
    }

    public static bool DrawDebugGizmos
    {
        get { return DebugSettings.Gizmos.DrawDebugGizmos; }
    }

    public static PresetEditorDefaults EditorDefaults
    {
        get { return PresetHandler.EditorDefaults; }
    }

    public static ECombatDecision ForceSoloDecision = ECombatDecision.None;

    public static ESquadDecision ForceSquadDecision = ESquadDecision.None;

    public static ESelfActionType ForceSelfDecision = ESelfActionType.None;

    public void Awake()
    {
        _patchManager = new(this, true);

        PresetHandler.Init();
        TryCreateCustomPreset();
        BindConfigs();
        _patchManager.EnablePatches();
        BigBrainHandler.Init();
    }

    private static void TryCreateCustomPreset()
    {
        const string presetName = "My Tuned Preset";
        if (PresetHandler.CustomPresetOptions.Exists(p => p.Name == presetName))
        {
            return;
        }

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

        // === Apply Harder PMCs bonuses (ApplyHarderPMCs equivalent) ===
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

    private void BindConfigs()
    {
        string category = "SAIN Editor";
        OpenEditorButton = Config.Bind(category, "Open Editor", false, "Opens the Editor on press");
        OpenEditorConfigEntry = Config.Bind(
            category,
            "Open Editor Shortcut",
            new KeyboardShortcut(KeyCode.F6),
            "The keyboard shortcut that toggles editor"
        );
    }

    public static ConfigEntry<bool> OpenEditorButton { get; private set; }

    public static ConfigEntry<KeyboardShortcut> OpenEditorConfigEntry { get; private set; }

    public static SAINPresetClass LoadedPreset
    {
        get { return PresetHandler.LoadedPreset; }
    }

    public void Update()
    {
        ModDetection.ManualUpdate();
        SAINEditor.ManualUpdate();
        DebugGizmos.ManualUpdate();
    }

    public void Start()
    {
        SAINEditor.Init();
    }

    public void LateUpdate()
    {
        SAINEditor.LateUpdate();
    }

    public void OnGUI()
    {
        SAINEditor.OnGUI();
    }
}
