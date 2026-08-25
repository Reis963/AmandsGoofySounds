using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using EFT.Ballistics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SPT.Reflection.Patching;
using UnityEngine;
using System.Threading.Tasks;

namespace AmandsGoofySounds
{
    [BepInPlugin("com.Amanda.GoofySounds", "GoofySounds", "2.4.0")]
    [BepInDependency("com.SPT.core", "4.1.3")]
    public class AmandsGoofySoundsPlugin : BaseUnityPlugin
    {
        internal const string DefaultSoundPackName = "Default";
        public static AmandsGoofySoundsClass AmandsGoofySoundsClassComponent;
        internal static ManualLogSource PluginLog { get; private set; }
        public static ConfigEntry<bool> DebugLogs { get; set; }
        public static ConfigEntry<bool> EnableSounds { get; set; }
        public static ConfigEntry<bool> EnableRandomSounds { get; set; }
        public static ConfigEntry<bool> EnableHitSounds { get; set; }
        public static ConfigEntry<bool> EnableDeathSounds { get; set; }
        public static ConfigEntry<bool> EnableSpottedSounds { get; set; }
        public static ConfigEntry<string> SoundPack { get; set; }
        public static ConfigEntry<EAudioRouting> AudioRouting { get; set; }
        public static ConfigEntry<float> Distance { get; set; }
        public static ConfigEntry<int> Rolloff { get; set; }
        public static ConfigEntry<float> Volume { get; set; }
        public static ConfigEntry<float> RandomVolume { get; set; }
        public static ConfigEntry<float> HitVolume { get; set; }
        public static ConfigEntry<float> DeathVolume { get; set; }
        public static ConfigEntry<float> SpottedVolume { get; set; }
        public static ConfigEntry<int> MaxSimultaneousSounds { get; set; }
        public static ConfigEntry<float> RandomChance { get; set; }
        public static ConfigEntry<float> MinRandom { get; set; }
        public static ConfigEntry<float> MaxRandom { get; set; }
        public static ConfigEntry<float> HitChance { get; set; }
        public static ConfigEntry<float> HitCooldown { get; set; }
        public static ConfigEntry<float> DeathChance { get; set; }
        public static ConfigEntry<float> DeathCooldown { get; set; }
        public static ConfigEntry<float> SpottedChance { get; set; }
        public static ConfigEntry<float> SpottedCooldown { get; set; }
        private void Awake()
        {
            PluginLog = Logger;
            PluginLog.LogInfo("Initializing GoofySounds 2.4.0 for SPT 4.1.3.");
        }
        private void Start()
        {
            DebugLogs = Config.Bind("AmandsGoofySounds", "DebugLogs", false, new ConfigDescription("Write detailed lifecycle, event, loading, selection, cooldown, and playback diagnostics to the BepInEx log.", null, new ConfigurationManagerAttributes { Order = 400 }));
            EnableSounds = Config.Bind("AmandsGoofySounds", "EnableSounds", true, new ConfigDescription("Master switch for every GoofySounds category. Audio files are still loaded while this is disabled.", null, new ConfigurationManagerAttributes { Order = 390 }));
            EnableRandomSounds = Config.Bind("AmandsGoofySounds", "EnableRandomSounds", true, new ConfigDescription("Allow ambient Random sounds from nearby non-local characters.", null, new ConfigurationManagerAttributes { Order = 380 }));
            EnableHitSounds = Config.Bind("AmandsGoofySounds", "EnableHitSounds", true, new ConfigDescription("Allow Hit sounds when a non-local character takes damage.", null, new ConfigurationManagerAttributes { Order = 370 }));
            EnableDeathSounds = Config.Bind("AmandsGoofySounds", "EnableDeathSounds", true, new ConfigDescription("Allow Death sounds when a non-local character dies.", null, new ConfigurationManagerAttributes { Order = 360 }));
            EnableSpottedSounds = Config.Bind("AmandsGoofySounds", "EnableSpottedSounds", true, new ConfigDescription("Allow Spotted sounds when a visible bot targets the local player.", null, new ConfigurationManagerAttributes { Order = 350 }));
            string[] availableSoundPacks = DiscoverSoundPackNames();
            SoundPack = Config.Bind("AmandsGoofySounds", "SoundPack", DefaultSoundPackName, new ConfigDescription("Select the sound pack loaded for the next raid. Default uses the Random, Hit, Death, and Spotted folders beside the plugin. Additional packs are discovered at game startup under GoofySounds/Packs. Changes made during a raid apply to the next raid.", new AcceptableValueList<string>(availableSoundPacks), new ConfigurationManagerAttributes { Order = 348 }));
            AudioRouting = Config.Bind("AmandsGoofySounds", "AudioRouting", EAudioRouting.Character, new ConfigDescription("Select the Tarkov audio route used for new sounds. Character preserves the original behavior. VoipMixer uses Tarkov's VOIP mixer and VOIP audio group without transmitting voice data; it falls back to Character if the VOIP mixer is unavailable.", null, new ConfigurationManagerAttributes { Order = 345 }));
            Distance = Config.Bind("AmandsGoofySounds", "Distance", 99f, new ConfigDescription("Maximum listener distance in metres for starting a sound and the range passed to Tarkov's spatial audio system.", new AcceptableValueRange<float>(0.1f, 1000f), new ConfigurationManagerAttributes { Order = 340 }));
            Rolloff = Config.Bind("AmandsGoofySounds", "Rolloff", 100, new ConfigDescription("Attenuation value passed directly to Tarkov's BetterAudio spatial playback method.", null, new ConfigurationManagerAttributes { Order = 330 }));
            Volume = Config.Bind("AmandsGoofySounds", "Volume", 1.0f, new ConfigDescription("Master volume multiplier applied to every GoofySounds category.", new AcceptableValueRange<float>(0.01f, 4f), new ConfigurationManagerAttributes { Order = 320 }));
            RandomVolume = Config.Bind("AmandsGoofySounds", "RandomVolume", 1.0f, new ConfigDescription("Additional volume multiplier for Random sounds. This is multiplied by Volume, with the final value capped at 4.", new AcceptableValueRange<float>(0.01f, 4f), new ConfigurationManagerAttributes { Order = 310 }));
            HitVolume = Config.Bind("AmandsGoofySounds", "HitVolume", 1.0f, new ConfigDescription("Additional volume multiplier for Hit sounds. This is multiplied by Volume, with the final value capped at 4.", new AcceptableValueRange<float>(0.01f, 4f), new ConfigurationManagerAttributes { Order = 300 }));
            DeathVolume = Config.Bind("AmandsGoofySounds", "DeathVolume", 1.0f, new ConfigDescription("Additional volume multiplier for Death sounds. This is multiplied by Volume, with the final value capped at 4.", new AcceptableValueRange<float>(0.01f, 4f), new ConfigurationManagerAttributes { Order = 290 }));
            SpottedVolume = Config.Bind("AmandsGoofySounds", "SpottedVolume", 1.0f, new ConfigDescription("Additional volume multiplier for Spotted sounds. This is multiplied by Volume, with the final value capped at 4.", new AcceptableValueRange<float>(0.01f, 4f), new ConfigurationManagerAttributes { Order = 280 }));
            MaxSimultaneousSounds = Config.Bind("AmandsGoofySounds", "MaxSimultaneousSounds", 0, new ConfigDescription("Maximum number of GoofySounds clips that may be active across all characters. New requests are discarded at the limit. Set to 0 for unlimited.", new AcceptableValueRange<int>(0, 32), new ConfigurationManagerAttributes { Order = 270 }));
            RandomChance = Config.Bind("AmandsGoofySounds", "RandomChance", 0.69f, new ConfigDescription("Probability from 0 to 1 that an eligible Random timer event starts a sound.", new AcceptableValueRange<float>(0.0f, 1f), new ConfigurationManagerAttributes { Order = 260, ShowRangeAsPercent = true }));
            MinRandom = Config.Bind("AmandsGoofySounds", "MinRandomTimer", 10f, new ConfigDescription("Minimum number of seconds between Random event checks. If greater than MaxRandomTimer, the two values are swapped at runtime.", new AcceptableValueRange<float>(0.1f, 3600f), new ConfigurationManagerAttributes { Order = 250 }));
            MaxRandom = Config.Bind("AmandsGoofySounds", "MaxRandomTimer", 60f, new ConfigDescription("Maximum number of seconds between Random event checks. If lower than MinRandomTimer, the two values are swapped at runtime.", new AcceptableValueRange<float>(0.1f, 3600f), new ConfigurationManagerAttributes { Order = 240 }));
            HitChance = Config.Bind("AmandsGoofySounds", "HitChance", 0.69f, new ConfigDescription("Probability from 0 to 1 that eligible damage to a non-local character requests a Hit sound.", new AcceptableValueRange<float>(0.0f, 1f), new ConfigurationManagerAttributes { Order = 230, ShowRangeAsPercent = true }));
            HitCooldown = Config.Bind("AmandsGoofySounds", "HitCooldown", 0f, new ConfigDescription("Additional seconds after a Hit clip ends before another Hit sound from the same character may start. Other categories remain eligible after the clip ends.", new AcceptableValueRange<float>(0f, 3600f), new ConfigurationManagerAttributes { Order = 220 }));
            DeathChance = Config.Bind("AmandsGoofySounds", "DeathChance", 0.69f, new ConfigDescription("Probability from 0 to 1 that a non-local character death requests a Death sound.", new AcceptableValueRange<float>(0.0f, 1f), new ConfigurationManagerAttributes { Order = 210, ShowRangeAsPercent = true }));
            DeathCooldown = Config.Bind("AmandsGoofySounds", "DeathCooldown", 0f, new ConfigDescription("Additional global seconds after a Death clip ends before another Death sound may start.", new AcceptableValueRange<float>(0f, 3600f), new ConfigurationManagerAttributes { Order = 200 }));
            SpottedChance = Config.Bind("AmandsGoofySounds", "SpottedChance", 0.69f, new ConfigDescription("Probability from 0 to 1 that an eligible visible-bot event requests a Spotted sound.", new AcceptableValueRange<float>(0.0f, 1f), new ConfigurationManagerAttributes { Order = 190, ShowRangeAsPercent = true }));
            SpottedCooldown = Config.Bind("AmandsGoofySounds", "SpottedCooldown", 30.0f, new ConfigDescription("Global number of seconds between eligible Spotted event rolls across all bots.", new AcceptableValueRange<float>(0f, 3600f), new ConfigurationManagerAttributes { Order = 180 }));

            Config.SettingChanged += OnSettingChanged;
            LogDebug($"Configuration bound: EnableSounds={EnableSounds.Value}, Categories Random={EnableRandomSounds.Value}, Hit={EnableHitSounds.Value}, Death={EnableDeathSounds.Value}, Spotted={EnableSpottedSounds.Value}, SoundPack={SoundPack.Value}, AvailableSoundPacks=[{string.Join(", ", availableSoundPacks)}], AudioRouting={AudioRouting.Value}, Distance={Distance.Value}, Rolloff={Rolloff.Value}, Volume={Volume.Value}, CategoryVolumes Random={RandomVolume.Value}, Hit={HitVolume.Value}, Death={DeathVolume.Value}, Spotted={SpottedVolume.Value}, MaxSimultaneous={MaxSimultaneousSounds.Value}, RandomChance={RandomChance.Value}, RandomTimer={MinRandom.Value}-{MaxRandom.Value}, HitChance={HitChance.Value}, HitCooldown={HitCooldown.Value}, DeathChance={DeathChance.Value}, DeathCooldown={DeathCooldown.Value}, SpottedChance={SpottedChance.Value}, SpottedCooldown={SpottedCooldown.Value}.");

            AmandsGoofySoundsClassComponent = gameObject.AddComponent<AmandsGoofySoundsClass>();
            AmandsGoofySoundsClassComponent.Initialize();

            new AmandsLocalPlayerPatch().Enable();
            new AmandsGoofySoundsKillPatch().Enable();
            new AmandsGoofySoundsDamagePatch().Enable();
            new AmandsGoofySoundsGoalPatch().Enable();
            LogDebug("Enabled LocalPlayer, death, damage, and bot-goal patches.");
        }
        private void OnDestroy()
        {
            Config.SettingChanged -= OnSettingChanged;
            AmandsGoofySoundsClassComponent?.Shutdown();
            LogDebug("Plugin destroyed.");
        }
        private static void OnSettingChanged(object sender, SettingChangedEventArgs eventArgs)
        {
            if (DebugLogs.Value || eventArgs.ChangedSetting == DebugLogs)
            {
                PluginLog.LogInfo($"[Debug] Configuration changed: {eventArgs.ChangedSetting.Definition.Section}.{eventArgs.ChangedSetting.Definition.Key}={eventArgs.ChangedSetting.BoxedValue}.");
                if (eventArgs.ChangedSetting == SoundPack)
                {
                    PluginLog.LogInfo("[Debug] SoundPack changes apply on the next raid audio load; active clips are not reloaded during the current raid.");
                }
            }
        }
        internal static void LogDebug(string message)
        {
            if (DebugLogs?.Value == true)
            {
                PluginLog.LogInfo($"[Debug] {message}");
            }
        }
        internal static bool IsDebugLoggingEnabled => DebugLogs?.Value == true;
        internal static string GetPluginAudioRoot()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "plugins", "GoofySounds"));
        }
        internal static bool HasSoundPackCategoryDirectory(string soundPackRoot)
        {
            return Directory.Exists(Path.Combine(soundPackRoot, "Random")) ||
                Directory.Exists(Path.Combine(soundPackRoot, "Hit")) ||
                Directory.Exists(Path.Combine(soundPackRoot, "Death")) ||
                Directory.Exists(Path.Combine(soundPackRoot, "Spotted"));
        }
        private static string[] DiscoverSoundPackNames()
        {
            List<string> soundPackNames = new List<string> { DefaultSoundPackName };
            string packsRoot = Path.Combine(GetPluginAudioRoot(), "Packs");
            if (!Directory.Exists(packsRoot))
            {
                LogDebug($"Sound pack directory is not present; only Default is available: path={packsRoot}.");
                return soundPackNames.ToArray();
            }

            try
            {
                DirectoryInfo[] directories = new DirectoryInfo(packsRoot).GetDirectories();
                Array.Sort(directories, (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
                foreach (DirectoryInfo directory in directories)
                {
                    if (string.Equals(directory.Name, DefaultSoundPackName, StringComparison.OrdinalIgnoreCase))
                    {
                        PluginLog.LogWarning($"Sound pack directory ignored because '{DefaultSoundPackName}' is reserved for the root audio folders: path={directory.FullName}.");
                        continue;
                    }
                    if (!HasSoundPackCategoryDirectory(directory.FullName))
                    {
                        PluginLog.LogWarning($"Sound pack directory ignored because it contains no Random, Hit, Death, or Spotted category directory: path={directory.FullName}.");
                        continue;
                    }
                    soundPackNames.Add(directory.Name);
                }
            }
            catch (Exception exception)
            {
                PluginLog.LogWarning($"Sound pack discovery failed; only Default will be available: path={packsRoot}, exception={exception}.");
                return new[] { DefaultSoundPackName };
            }

            LogDebug($"Sound pack discovery completed: root={packsRoot}, available=[{string.Join(", ", soundPackNames)}].");
            return soundPackNames.ToArray();
        }
        internal static bool IsSoundTypeEnabled(ESoundType soundType)
        {
            if (EnableSounds?.Value != true) return false;
            switch (soundType)
            {
                case ESoundType.Random: return EnableRandomSounds.Value;
                case ESoundType.Hit: return EnableHitSounds.Value;
                case ESoundType.Death: return EnableDeathSounds.Value;
                case ESoundType.Spotted: return EnableSpottedSounds.Value;
                default: return false;
            }
        }
        internal static float GetSoundVolume(ESoundType soundType)
        {
            float categoryVolume;
            switch (soundType)
            {
                case ESoundType.Random: categoryVolume = RandomVolume.Value; break;
                case ESoundType.Hit: categoryVolume = HitVolume.Value; break;
                case ESoundType.Death: categoryVolume = DeathVolume.Value; break;
                case ESoundType.Spotted: categoryVolume = SpottedVolume.Value; break;
                default: categoryVolume = 1f; break;
            }
            return Mathf.Clamp(Volume.Value * categoryVolume, 0.01f, 4f);
        }
        internal static bool RollChance(float chance, out float roll)
        {
            roll = UnityEngine.Random.value;
            if (chance <= 0f) return false;
            if (chance >= 1f) return true;
            return roll < chance;
        }
    }
    public class AmandsLocalPlayerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(LocalPlayer).GetMethod("Create", BindingFlags.Static | BindingFlags.Public);
        }
        [PatchPostfix]
        private static void PatchPostFix(ref Task<LocalPlayer> __result)
        {
            AmandsGoofySoundsPlugin.LogDebug($"LocalPlayer.Create postfix invoked; task status={__result.Status}.");
            LocalPlayer localPlayer = __result.Result;
            if (localPlayer != null && localPlayer.IsYourPlayer)
            {
                AmandsGoofySoundsPlugin.LogDebug($"Captured local player profile={localPlayer.ProfileId}; waiting for GameWorld.AfterGameStarted before loading audio files.");
                AmandsGoofySoundsPlugin.AmandsGoofySoundsClassComponent.RegisterLocalPlayer(localPlayer);
            }
            else
            {
                AmandsGoofySoundsPlugin.LogDebug($"LocalPlayer.Create result ignored: null={localPlayer == null}, isYourPlayer={localPlayer?.IsYourPlayer}.");
            }
        }
    }
    public class AmandsGoofySoundsKillPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(Player).GetMethod("OnBeenKilledByAggressor", BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(IPlayer), typeof(DamageInfo), typeof(EBodyPart), typeof(EDamageType) }, null);
        }
        [PatchPostfix]
        private static void PatchPostFix(ref Player __instance, IPlayer aggressor, DamageInfo damageInfo, EBodyPart bodyPart, EDamageType lethalDamageType)
        {
            bool enabled = AmandsGoofySoundsPlugin.IsSoundTypeEnabled(ESoundType.Death);
            float roll = -1f;
            bool shouldPlay = enabled && !__instance.IsYourPlayer && AmandsGoofySoundsPlugin.RollChance(AmandsGoofySoundsPlugin.DeathChance.Value, out roll);
            AmandsGoofySoundsPlugin.LogDebug($"Death event: victim={__instance.ProfileId}, isLocal={__instance.IsYourPlayer}, aggressor={aggressor?.ProfileId}, bodyPart={bodyPart}, damageType={lethalDamageType}, enabled={enabled}, roll={roll:F4}, chance={AmandsGoofySoundsPlugin.DeathChance.Value:F4}, play={shouldPlay}.");
            if (shouldPlay) AmandsGoofySoundsPlugin.AmandsGoofySoundsClassComponent.PlaySoundDeath(__instance.ProfileId, damageInfo.HitPoint);
        }
    }
    public class AmandsGoofySoundsDamagePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(Player).GetMethod("ApplyDamageInfo", BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(DamageInfo), typeof(EBodyPart), typeof(EBodyPartColliderType), typeof(float) }, null);
        }
        [PatchPostfix]
        private static void PatchPostFix(ref Player __instance, DamageInfo damageInfo, EBodyPart bodyPartType)
        {
            bool enabled = AmandsGoofySoundsPlugin.IsSoundTypeEnabled(ESoundType.Hit);
            float roll = -1f;
            bool shouldPlay = enabled && !__instance.IsYourPlayer && AmandsGoofySoundsPlugin.RollChance(AmandsGoofySoundsPlugin.HitChance.Value, out roll);
            AmandsGoofySoundsPlugin.LogDebug($"Damage event: target={__instance.ProfileId}, isLocal={__instance.IsYourPlayer}, bodyPart={bodyPartType}, damage={damageInfo.Damage:F2}, enabled={enabled}, roll={roll:F4}, chance={AmandsGoofySoundsPlugin.HitChance.Value:F4}, play={shouldPlay}.");
            if (shouldPlay) AmandsGoofySoundsPlugin.AmandsGoofySoundsClassComponent.PlayAmandsGoofySounds(ESoundType.Hit,__instance.ProfileId,__instance.Position,__instance.Transform.Original);
        }
    }
    public class AmandsGoofySoundsGoalPatch : ModulePatch
    {
        private static float _nextSpottedAllowedTime;
        private static Type _memoryType;
        private static PropertyInfo _goalEnemyProperty;
        private static Type _goalEnemyType;
        private static PropertyInfo _personProperty;
        private static PropertyInfo _isVisibleProperty;
        private static bool _reflectionFailureLogged;
        protected override MethodBase GetTargetMethod()
        {
            return typeof(BotsGroup).GetMethod("CalcGoalForBot", new[] { typeof(BotOwner) });
        }
        internal static void ResetRuntimeState()
        {
            _nextSpottedAllowedTime = 0f;
            AmandsGoofySoundsPlugin.LogDebug("Reset Spotted cooldown state for the new audio generation.");
        }
        [PatchPostfix]
        public static void PatchPostfix(BotOwner bot)
        {
            if (bot == null || !AmandsGoofySoundsPlugin.IsSoundTypeEnabled(ESoundType.Spotted)) return;
            if (!TryReadGoalEnemy(bot, out IPlayer person, out bool isVisible)) return;

            if (AmandsGoofySoundsPlugin.IsDebugLoggingEnabled)
                AmandsGoofySoundsPlugin.LogDebug($"Bot goal evaluated: bot={bot.GetPlayer.ProfileId}, target={person?.ProfileId}, targetIsLocal={person?.IsYourPlayer}, visible={isVisible}, cooldownUntil={_nextSpottedAllowedTime:F2}, time={Time.time:F2}.");
            if (person?.IsYourPlayer == true && isVisible)
            {
                if (_nextSpottedAllowedTime <= Time.time)
                {
                    _nextSpottedAllowedTime = Time.time + AmandsGoofySoundsPlugin.SpottedCooldown.Value;
                    bool shouldPlay = AmandsGoofySoundsPlugin.RollChance(AmandsGoofySoundsPlugin.SpottedChance.Value, out float roll);
                    AmandsGoofySoundsPlugin.LogDebug($"Spotted event eligible: bot={bot.GetPlayer.ProfileId}, roll={roll:F4}, chance={AmandsGoofySoundsPlugin.SpottedChance.Value:F4}, nextCooldown={_nextSpottedAllowedTime:F2}, play={shouldPlay}.");
                    if (shouldPlay) AmandsGoofySoundsPlugin.AmandsGoofySoundsClassComponent.PlayAmandsGoofySounds(ESoundType.Spotted, bot.GetPlayer.ProfileId, bot.GetPlayer.Position, bot.GetPlayer.Transform.Original);
                }
                else if (AmandsGoofySoundsPlugin.IsDebugLoggingEnabled)
                    AmandsGoofySoundsPlugin.LogDebug($"Spotted event suppressed by global cooldown for bot={bot.GetPlayer.ProfileId}; remaining={_nextSpottedAllowedTime - Time.time:F2}s.");
            }
        }
        private static bool TryReadGoalEnemy(BotOwner bot, out IPlayer person, out bool isVisible)
        {
            person = null;
            isVisible = false;
            if (_reflectionFailureLogged) return false;
            object memory = bot.Memory;
            if (memory == null) return false;

            try
            {
                Type memoryType = memory.GetType();
                if (_memoryType != memoryType)
                {
                    _memoryType = memoryType;
                    _goalEnemyProperty = memoryType.GetProperty("GoalEnemy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_goalEnemyProperty == null) throw new MissingMemberException(memoryType.FullName, "GoalEnemy");
                    AmandsGoofySoundsPlugin.LogDebug($"Cached Spotted reflection property: memory={memoryType.FullName}, goalEnemy={_goalEnemyProperty.DeclaringType?.FullName}.{_goalEnemyProperty.Name}.");
                }

                object goalEnemy = _goalEnemyProperty.GetValue(memory);
                if (goalEnemy == null)
                {
                    if (AmandsGoofySoundsPlugin.IsDebugLoggingEnabled)
                        AmandsGoofySoundsPlugin.LogDebug($"Bot goal evaluated: bot={bot.GetPlayer.ProfileId}, no GoalEnemy.");
                    return false;
                }

                Type goalEnemyType = goalEnemy.GetType();
                if (_goalEnemyType != goalEnemyType)
                {
                    _goalEnemyType = goalEnemyType;
                    _personProperty = goalEnemyType.GetProperty("Person", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _isVisibleProperty = goalEnemyType.GetProperty("IsVisible", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_personProperty == null) throw new MissingMemberException(goalEnemyType.FullName, "Person");
                    if (_isVisibleProperty == null) throw new MissingMemberException(goalEnemyType.FullName, "IsVisible");
                    AmandsGoofySoundsPlugin.LogDebug($"Cached Spotted reflection properties: goalEnemy={goalEnemyType.FullName}, person={_personProperty.Name}, visible={_isVisibleProperty.Name}.");
                }

                person = _personProperty.GetValue(goalEnemy) as IPlayer;
                object visibleValue = _isVisibleProperty.GetValue(goalEnemy);
                isVisible = visibleValue is bool visible && visible;
                return person != null;
            }
            catch (System.Exception exception)
            {
                if (!_reflectionFailureLogged)
                {
                    _reflectionFailureLogged = true;
                    AmandsGoofySoundsPlugin.PluginLog.LogWarning($"Spotted reflection access failed and Spotted sounds are disabled for this target shape: {exception}");
                }
                return false;
            }
        }
    }
}
