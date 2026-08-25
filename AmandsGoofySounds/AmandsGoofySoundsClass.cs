using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using EFT;
using Comfort.Common;

namespace AmandsGoofySounds
{
    public class AmandsGoofySoundsClass : MonoBehaviour
    {
        private const int InitialClipCountPerCategory = 5;
        public static LocalPlayer localPlayer;
        private static List<LoadedAudioClip> SoundRandom = new List<LoadedAudioClip>();
        private static List<LoadedAudioClip> SoundHit = new List<LoadedAudioClip>();
        private static List<LoadedAudioClip> SoundDeath = new List<LoadedAudioClip>();
        private static List<LoadedAudioClip> SoundSpotted = new List<LoadedAudioClip>();
        private static Dictionary<string, float> Playing = new Dictionary<string, float>();
        private static readonly ShuffleBagState RandomShuffleBag = new ShuffleBagState();
        private static readonly ShuffleBagState HitShuffleBag = new ShuffleBagState();
        private static readonly ShuffleBagState DeathShuffleBag = new ShuffleBagState();
        private static readonly ShuffleBagState SpottedShuffleBag = new ShuffleBagState();
        private Coroutine _loadCoroutine;
        private Coroutine _deferredReloadCoroutine;
        private Coroutine _randomLoopCoroutine;
        private GameWorld _subscribedGameWorld;
        private readonly Dictionary<string, int> _pendingDeathGenerations = new Dictionary<string, int>();
        private readonly Dictionary<string, float> _hitCooldownUntil = new Dictionary<string, float>();
        private float _nextDeathAllowedTime;
        private int _reloadGeneration;
        private int _randomLoopGeneration;
        private int _playbackStartedCount;
        private int _profileRejectionCount;
        private int _limitRejectionCount;
        private int _deathDelayedCount;
        private int _deathReleasedCount;
        private int _characterRouteStartedCount;
        private int _voipRouteStartedCount;
        private int _voipFallbackCount;
        private bool _voipFallbackWarningLogged;
        private string _activeSoundPackName = AmandsGoofySoundsPlugin.DefaultSoundPackName;
        private bool _isInitialized;
        private bool _isShutdown;

        private sealed class LoadedAudioClip : IDisposable
        {
            public AudioClip Clip { get; private set; }
            public string FileName { get; }

            public LoadedAudioClip(AudioClip clip, string fileName)
            {
                Clip = clip;
                FileName = fileName;
            }

            public void Dispose()
            {
                AudioClip audioClip = Clip;
                Clip = null;
                if (audioClip != null) UnityEngine.Object.Destroy(audioClip);
            }
        }
        private sealed class AudioFileRequest
        {
            public string Path { get; }
            public ESoundType SoundType { get; }

            public AudioFileRequest(string path, ESoundType soundType)
            {
                Path = path;
                SoundType = soundType;
            }
        }
        private sealed class PcmWaveData
        {
            public float[] Samples { get; }
            public int SampleFrames { get; }
            public int Channels { get; }
            public int Frequency { get; }

            public PcmWaveData(float[] samples, int sampleFrames, int channels, int frequency)
            {
                Samples = samples;
                SampleFrames = sampleFrames;
                Channels = channels;
                Frequency = frequency;
            }
        }
        private sealed class ShuffleBagState
        {
            private readonly List<int> _order = new List<int>();
            private int _knownCount = -1;
            private int _position;
            private int _lastIndex = -1;

            public int NextIndex(int count)
            {
                if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
                if (_knownCount != count || _position >= _order.Count) Refill(count);
                int selectedIndex = _order[_position++];
                _lastIndex = selectedIndex;
                return selectedIndex;
            }

            public void Reset()
            {
                _order.Clear();
                _knownCount = -1;
                _position = 0;
                _lastIndex = -1;
            }

            private void Refill(int count)
            {
                _order.Clear();
                for (int index = 0; index < count; index++) _order.Add(index);
                for (int index = count - 1; index > 0; index--)
                {
                    int swapIndex = UnityEngine.Random.Range(0, index + 1);
                    int value = _order[index];
                    _order[index] = _order[swapIndex];
                    _order[swapIndex] = value;
                }

                if (count > 1 && _lastIndex >= 0 && _order[0] == _lastIndex)
                {
                    int swapIndex = UnityEngine.Random.Range(1, count);
                    int value = _order[0];
                    _order[0] = _order[swapIndex];
                    _order[swapIndex] = value;
                }
                _knownCount = count;
                _position = 0;
            }
        }
        public void Initialize()
        {
            if (_isInitialized || _isShutdown) return;
            _isInitialized = true;
            AmandsGoofySoundsPlugin.LogDebug("Audio component initialized on the persistent BepInEx plugin object; waiting for GameWorld.AfterGameStarted before loading files.");
        }
        public void RegisterLocalPlayer(LocalPlayer player)
        {
            if (_isShutdown || player == null) return;

            bool isNewPlayer = localPlayer != player;
            localPlayer = player;
            AmandsGoofySoundsPlugin.LogDebug($"Registered local player profile={player.ProfileId}; newPlayer={isNewPlayer}.");
            if (isNewPlayer) StopRandomLoop("new local player");

            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
            {
                AmandsGoofySoundsPlugin.PluginLog.LogWarning("Cannot schedule audio loading because GameWorld is not available after local-player creation.");
                return;
            }

            UnsubscribeFromGameWorld();
            _subscribedGameWorld = gameWorld;
            _subscribedGameWorld.AfterGameStarted += OnAfterGameStarted;
            AmandsGoofySoundsPlugin.LogDebug("Subscribed to GameWorld.AfterGameStarted for deferred audio loading.");
        }
        private void OnAfterGameStarted()
        {
            AmandsGoofySoundsPlugin.LogDebug("GameWorld.AfterGameStarted received; scheduling audio loading for the next frame.");
            UnsubscribeFromGameWorld();
            if (_isShutdown) return;

            if (_deferredReloadCoroutine != null) StopCoroutine(_deferredReloadCoroutine);
            _deferredReloadCoroutine = StartCoroutine(ReloadAfterGameStarted());
        }
        private IEnumerator ReloadAfterGameStarted()
        {
            yield return null;
            _deferredReloadCoroutine = null;
            if (_isShutdown || localPlayer == null)
            {
                AmandsGoofySoundsPlugin.LogDebug($"Deferred audio loading cancelled: shutdown={_isShutdown}, hasLocalPlayer={localPlayer != null}.");
                yield break;
            }

            AmandsGoofySoundsPlugin.LogDebug("Starting audio loading one frame after GameWorld.AfterGameStarted.");
            StartRandomLoop();
            ReloadFiles();
        }
        private void UnsubscribeFromGameWorld()
        {
            if (_subscribedGameWorld == null) return;
            _subscribedGameWorld.AfterGameStarted -= OnAfterGameStarted;
            _subscribedGameWorld = null;
        }
        public void OnDestroy()
        {
            Shutdown();
        }
        public void Shutdown()
        {
            if (_isShutdown) return;
            _isShutdown = true;
            _reloadGeneration++;
            _randomLoopGeneration++;
            UnsubscribeFromGameWorld();
            if (_deferredReloadCoroutine != null)
            {
                StopCoroutine(_deferredReloadCoroutine);
                _deferredReloadCoroutine = null;
            }
            if (_loadCoroutine != null)
            {
                StopCoroutine(_loadCoroutine);
                _loadCoroutine = null;
            }
            StopRandomLoop("plugin shutdown");
            AmandsGoofySoundsPlugin.LogDebug("Audio component shutting down; invalidating loads and destroying owned audio clips.");
            LogPlaybackSummary("plugin shutdown");
            ClearPendingDeaths("plugin shutdown");
            DisposeLoadedClips();
            Playing.Clear();
            _hitCooldownUntil.Clear();
            _nextDeathAllowedTime = 0f;
            localPlayer = null;
        }
        private void StartRandomLoop()
        {
            StopRandomLoop("replacement random loop");
            int generation = ++_randomLoopGeneration;
            AmandsGoofySoundsPlugin.LogDebug($"Starting random loop generation={generation} after GameWorld.AfterGameStarted.");
            _randomLoopCoroutine = StartCoroutine(PlaySoundRandom(generation));
        }
        private void StopRandomLoop(string reason)
        {
            _randomLoopGeneration++;
            if (_randomLoopCoroutine != null)
            {
                StopCoroutine(_randomLoopCoroutine);
                _randomLoopCoroutine = null;
            }
            AmandsGoofySoundsPlugin.LogDebug($"Random loop invalidated: reason={reason}, currentGeneration={_randomLoopGeneration}.");
        }
        private IEnumerator PlaySoundRandom(int generation)
        {
            while (!_isShutdown && generation == _randomLoopGeneration && localPlayer != null)
            {
                float delay = GetRandomDelay();
                AmandsGoofySoundsPlugin.LogDebug($"Random loop scheduled: generation={generation}, delay={delay:F2}s.");
                yield return new WaitForSeconds(delay);

                if (_isShutdown || generation != _randomLoopGeneration || localPlayer == null) break;
                if (!AmandsGoofySoundsPlugin.IsSoundTypeEnabled(ESoundType.Random))
                {
                    AmandsGoofySoundsPlugin.LogDebug("Random event skipped because Random sounds are disabled.");
                    continue;
                }

                GameWorld gameWorld = Singleton<GameWorld>.Instance;
                if (gameWorld == null)
                {
                    AmandsGoofySoundsPlugin.LogDebug("Random loop stopped because GameWorld is unavailable.");
                    break;
                }

                List<IPlayer> SoundPlayers = new List<IPlayer>();
                foreach (IPlayer AIDetails in gameWorld.RegisteredPlayers)
                {
                    if (AIDetails.IsYourPlayer) continue;
                    if (Vector3.Distance(localPlayer.Position, AIDetails.Position) < AmandsGoofySoundsPlugin.Distance.Value) SoundPlayers.Add(AIDetails);
                }
                AmandsGoofySoundsPlugin.LogDebug($"Random loop woke: registeredPlayers={gameWorld.RegisteredPlayers.Count}, eligiblePlayers={SoundPlayers.Count}, distance={AmandsGoofySoundsPlugin.Distance.Value:F2}.");
                if (SoundPlayers.Count != 0)
                {
                    bool shouldPlay = AmandsGoofySoundsPlugin.RollChance(AmandsGoofySoundsPlugin.RandomChance.Value, out float roll);
                    AmandsGoofySoundsPlugin.LogDebug($"Random event roll={roll:F4}, chance={AmandsGoofySoundsPlugin.RandomChance.Value:F4}, play={shouldPlay}.");
                    if (shouldPlay)
                    {
                        IPlayer AIDetails = SoundPlayers[UnityEngine.Random.Range(0, SoundPlayers.Count)];
                        AmandsGoofySoundsPlugin.LogDebug($"Random event selected profile={AIDetails.ProfileId}.");
                        PlayAmandsGoofySounds(ESoundType.Random,AIDetails.ProfileId,AIDetails.Position,AIDetails.Transform.Original);
                    }
                }
                else AmandsGoofySoundsPlugin.LogDebug("Random event skipped because no non-local player is within range.");
            }
            if (generation == _randomLoopGeneration) _randomLoopCoroutine = null;
            AmandsGoofySoundsPlugin.LogDebug($"Random loop stopped: generation={generation}, currentGeneration={_randomLoopGeneration}, shutdown={_isShutdown}, hasLocalPlayer={localPlayer != null}.");
        }
        private static float GetRandomDelay()
        {
            float minimum = AmandsGoofySoundsPlugin.MinRandom.Value;
            float maximum = AmandsGoofySoundsPlugin.MaxRandom.Value;
            if (minimum > maximum)
            {
                float value = minimum;
                minimum = maximum;
                maximum = value;
                AmandsGoofySoundsPlugin.LogDebug($"Random timer bounds swapped at runtime: minimum={minimum:F2}s, maximum={maximum:F2}s.");
            }
            return UnityEngine.Random.Range(minimum, maximum);
        }
        public void PlayAmandsGoofySounds(ESoundType soundType, string ProfileId, Vector3 Position, Transform Original)
        {
            AmandsGoofySoundsPlugin.LogDebug($"Playback requested: type={soundType}, profile={ProfileId}, position={Position}.");
            if (!AmandsGoofySoundsPlugin.IsSoundTypeEnabled(soundType))
            {
                AmandsGoofySoundsPlugin.LogDebug($"Playback rejected because the category is disabled: type={soundType}, profile={ProfileId}.");
                return;
            }
            if (localPlayer == null)
            {
                AmandsGoofySoundsPlugin.LogDebug($"Playback rejected because no local player is stored: type={soundType}, profile={ProfileId}.");
                return;
            }

            float distance = Vector3.Distance(localPlayer.Position, Position);
            if (distance > AmandsGoofySoundsPlugin.Distance.Value)
            {
                AmandsGoofySoundsPlugin.LogDebug($"Playback rejected by distance: type={soundType}, profile={ProfileId}, distance={distance:F2}, maximum={AmandsGoofySoundsPlugin.Distance.Value:F2}.");
                return;
            }
            if (_pendingDeathGenerations.ContainsKey(ProfileId))
            {
                _profileRejectionCount++;
                AmandsGoofySoundsPlugin.LogDebug($"Playback rejected because Death is pending for profile={ProfileId}: type={soundType}.");
                return;
            }

            PruneExpiredPlaybackEntries();
            if (Playing.TryGetValue(ProfileId, out float activeUntil))
            {
                _profileRejectionCount++;
                AmandsGoofySoundsPlugin.LogDebug($"Playback rejected by per-profile exclusion: type={soundType}, profile={ProfileId}, remaining={activeUntil - Time.time:F2}s.");
                return;
            }
            if (soundType == ESoundType.Hit && _hitCooldownUntil.TryGetValue(ProfileId, out float hitCooldownUntil))
            {
                if (hitCooldownUntil > Time.time)
                {
                    AmandsGoofySoundsPlugin.LogDebug($"Hit playback rejected by per-profile cooldown: profile={ProfileId}, remaining={hitCooldownUntil - Time.time:F2}s.");
                    return;
                }
                _hitCooldownUntil.Remove(ProfileId);
            }
            if (IsPlaybackLimitReached(soundType, ProfileId)) return;

            LoadedAudioClip loadedAudioClip = SelectClip(GetClipList(soundType), soundType);
            if (loadedAudioClip == null) return;

            AudioClip audioClip = loadedAudioClip.Clip;
            float clipLength = audioClip.length;
            float volume = AmandsGoofySoundsPlugin.GetSoundVolume(soundType);
            AmandsGoofySoundsPlugin.LogDebug($"Playback clip validated: type={soundType}, profile={ProfileId}, file={loadedAudioClip.FileName}, volume={volume:F2}, {DescribeClip(audioClip)}.");
            BetterSource source = PlayClipAtPoint(Position, audioClip, volume, soundType, ProfileId, loadedAudioClip.FileName, out EAudioRouting effectiveRouting);
            if (source == null)
            {
                AmandsGoofySoundsPlugin.PluginLog.LogWarning($"BetterAudio did not provide a source: type={soundType}, profile={ProfileId}, file={loadedAudioClip.FileName}, requestedRoute={AmandsGoofySoundsPlugin.AudioRouting.Value}.");
                return;
            }

            source.transform.SetParent(Original);
            float playbackEndsAt = Time.time + clipLength;
            Playing[ProfileId] = playbackEndsAt;
            if (soundType == ESoundType.Hit) _hitCooldownUntil[ProfileId] = playbackEndsAt + AmandsGoofySoundsPlugin.HitCooldown.Value;
            _playbackStartedCount++;
            AmandsGoofySoundsPlugin.LogDebug($"Playback started: type={soundType}, profile={ProfileId}, file={loadedAudioClip.FileName}, route={effectiveRouting}, length={clipLength:F2}s, volume={volume:F2}, distance={distance:F2}, activeCount={Playing.Count}.");
        }
        private static List<LoadedAudioClip> GetClipList(ESoundType soundType)
        {
            switch (soundType)
            {
                case ESoundType.Random: return SoundRandom;
                case ESoundType.Hit: return SoundHit;
                case ESoundType.Death: return SoundDeath;
                case ESoundType.Spotted: return SoundSpotted;
                default: throw new ArgumentOutOfRangeException(nameof(soundType), soundType, null);
            }
        }
        private static ShuffleBagState GetShuffleBag(ESoundType soundType)
        {
            switch (soundType)
            {
                case ESoundType.Random: return RandomShuffleBag;
                case ESoundType.Hit: return HitShuffleBag;
                case ESoundType.Death: return DeathShuffleBag;
                case ESoundType.Spotted: return SpottedShuffleBag;
                default: throw new ArgumentOutOfRangeException(nameof(soundType), soundType, null);
            }
        }
        private void PruneExpiredPlaybackEntries()
        {
            List<string> expiredProfiles = null;
            foreach (KeyValuePair<string, float> playingEntry in Playing)
            {
                if (playingEntry.Value > Time.time) continue;
                if (expiredProfiles == null) expiredProfiles = new List<string>();
                expiredProfiles.Add(playingEntry.Key);
            }
            if (expiredProfiles == null) return;
            foreach (string profileId in expiredProfiles) Playing.Remove(profileId);
            AmandsGoofySoundsPlugin.LogDebug($"Pruned {expiredProfiles.Count} expired active-playback entries.");
        }
        private bool IsPlaybackLimitReached(ESoundType soundType, string profileId)
        {
            PruneExpiredPlaybackEntries();
            int maximum = AmandsGoofySoundsPlugin.MaxSimultaneousSounds.Value;
            if (maximum <= 0 || Playing.Count < maximum) return false;
            _limitRejectionCount++;
            AmandsGoofySoundsPlugin.LogDebug($"Playback rejected by global simultaneous-sound limit: type={soundType}, profile={profileId}, active={Playing.Count}, maximum={maximum}.");
            return true;
        }
        private BetterSource PlayClipAtPoint(Vector3 position, AudioClip audioClip, float volume, ESoundType soundType, string profileId, string fileName, out EAudioRouting effectiveRouting)
        {
            EAudioRouting requestedRouting = AmandsGoofySoundsPlugin.AudioRouting?.Value ?? EAudioRouting.Character;
            effectiveRouting = requestedRouting;
            BetterAudio betterAudio = Singleton<BetterAudio>.Instance;
            if (betterAudio == null)
            {
                AmandsGoofySoundsPlugin.PluginLog.LogWarning($"Playback routing failed because BetterAudio is unavailable: type={soundType}, profile={profileId}, file={fileName}, requestedRoute={requestedRouting}.");
                return null;
            }

            if (requestedRouting == EAudioRouting.VoipMixer)
            {
                if (betterAudio.VoipMixer != null)
                {
                    BetterSource voipSource = betterAudio.PlayAtPoint(position, audioClip, AmandsGoofySoundsPlugin.Distance.Value, BetterAudio.AudioSourceGroupType.Voip, AmandsGoofySoundsPlugin.Rolloff.Value, volume, EOcclusionTest.Regular, betterAudio.VoipMixer, false);
                    if (voipSource != null)
                    {
                        _voipRouteStartedCount++;
                        AmandsGoofySoundsPlugin.LogDebug($"Playback routed through Tarkov VOIP acoustics: type={soundType}, profile={profileId}, file={fileName}, mixer={betterAudio.VoipMixer.name}.");
                        return voipSource;
                    }

                    return PlayCharacterFallback(betterAudio, position, audioClip, volume, soundType, profileId, fileName, "the VOIP PlayAtPoint call returned no source", out effectiveRouting);
                }

                return PlayCharacterFallback(betterAudio, position, audioClip, volume, soundType, profileId, fileName, "BetterAudio.VoipMixer is unavailable", out effectiveRouting);
            }

            return PlayCharacterRoute(betterAudio, position, audioClip, volume, soundType, profileId, fileName);
        }
        private BetterSource PlayCharacterFallback(BetterAudio betterAudio, Vector3 position, AudioClip audioClip, float volume, ESoundType soundType, string profileId, string fileName, string reason, out EAudioRouting effectiveRouting)
        {
            effectiveRouting = EAudioRouting.Character;
            _voipFallbackCount++;
            if (!_voipFallbackWarningLogged)
            {
                _voipFallbackWarningLogged = true;
                AmandsGoofySoundsPlugin.PluginLog.LogWarning($"VOIP audio routing is unavailable; GoofySounds will fall back to Character routing for affected playbacks. Reason: {reason}.");
            }
            AmandsGoofySoundsPlugin.LogDebug($"VOIP playback fallback requested: type={soundType}, profile={profileId}, file={fileName}, reason={reason}.");
            return PlayCharacterRoute(betterAudio, position, audioClip, volume, soundType, profileId, fileName);
        }
        private BetterSource PlayCharacterRoute(BetterAudio betterAudio, Vector3 position, AudioClip audioClip, float volume, ESoundType soundType, string profileId, string fileName)
        {
            BetterSource source = betterAudio.PlayAtPoint(position, audioClip, AmandsGoofySoundsPlugin.Distance.Value, BetterAudio.AudioSourceGroupType.Character, AmandsGoofySoundsPlugin.Rolloff.Value, volume, EOcclusionTest.Regular);
            if (source != null)
            {
                _characterRouteStartedCount++;
                AmandsGoofySoundsPlugin.LogDebug($"Playback routed through Tarkov Character acoustics: type={soundType}, profile={profileId}, file={fileName}.");
            }
            return source;
        }
        public void PlaySoundDeath(string profileId, Vector3 position)
        {
            AmandsGoofySoundsPlugin.LogDebug($"Death playback requested: profile={profileId}, position={position}.");
            if (string.IsNullOrEmpty(profileId))
            {
                AmandsGoofySoundsPlugin.PluginLog.LogWarning("Death playback rejected because the victim profile is empty.");
                return;
            }

            if (!CanPlayDeath(profileId, position, "request")) return;
            if (_pendingDeathGenerations.ContainsKey(profileId))
            {
                AmandsGoofySoundsPlugin.LogDebug($"Duplicate Death playback rejected because one is already pending for profile={profileId}.");
                return;
            }
            if (_nextDeathAllowedTime > Time.time)
            {
                AmandsGoofySoundsPlugin.LogDebug($"Death playback rejected by global cooldown: profile={profileId}, remaining={_nextDeathAllowedTime - Time.time:F2}s.");
                return;
            }

            PruneExpiredPlaybackEntries();
            if (Playing.TryGetValue(profileId, out float activeUntil) && activeUntil > Time.time)
            {
                int generation = _reloadGeneration;
                float remaining = activeUntil - Time.time;
                _pendingDeathGenerations.Add(profileId, generation);
                _nextDeathAllowedTime = activeUntil;
                _deathDelayedCount++;
                StartCoroutine(PlaySoundDeathAfterDelay(profileId, position, activeUntil, generation));
                AmandsGoofySoundsPlugin.LogDebug($"Death playback delayed until the active sound ends: profile={profileId}, remaining={remaining:F2}s, generation={generation}.");
                return;
            }

            Playing.Remove(profileId);
            PlaySoundDeathNow(profileId, position, false);
        }
        private IEnumerator PlaySoundDeathAfterDelay(string profileId, Vector3 position, float playAt, int generation)
        {
            while (!_isShutdown && generation == _reloadGeneration && Time.time < playAt) yield return null;

            if (!_pendingDeathGenerations.TryGetValue(profileId, out int pendingGeneration) || pendingGeneration != generation) yield break;
            _pendingDeathGenerations.Remove(profileId);
            if (_isShutdown || generation != _reloadGeneration)
            {
                AmandsGoofySoundsPlugin.LogDebug($"Delayed Death playback cancelled by lifecycle change: profile={profileId}, requestedGeneration={generation}, currentGeneration={_reloadGeneration}, shutdown={_isShutdown}.");
                yield break;
            }

            Playing.Remove(profileId);
            _deathReleasedCount++;
            AmandsGoofySoundsPlugin.LogDebug($"Delayed Death playback released after the active sound ended: profile={profileId}, generation={generation}.");
            PlaySoundDeathNow(profileId, position, true);
        }
        private bool CanPlayDeath(string profileId, Vector3 position, string phase)
        {
            if (!AmandsGoofySoundsPlugin.IsSoundTypeEnabled(ESoundType.Death) || localPlayer == null)
            {
                AmandsGoofySoundsPlugin.LogDebug($"Death playback rejected during {phase}: profile={profileId}, enabled={AmandsGoofySoundsPlugin.IsSoundTypeEnabled(ESoundType.Death)}, hasLocalPlayer={localPlayer != null}.");
                return false;
            }

            float distance = Vector3.Distance(localPlayer.Position, position);
            if (distance > AmandsGoofySoundsPlugin.Distance.Value)
            {
                AmandsGoofySoundsPlugin.LogDebug($"Death playback rejected by distance during {phase}: profile={profileId}, distance={distance:F2}, maximum={AmandsGoofySoundsPlugin.Distance.Value:F2}.");
                return false;
            }
            return true;
        }
        private void PlaySoundDeathNow(string profileId, Vector3 position, bool wasDelayed)
        {
            if (!CanPlayDeath(profileId, position, wasDelayed ? "delayed release" : "immediate playback")) return;
            if (_nextDeathAllowedTime > Time.time)
            {
                AmandsGoofySoundsPlugin.LogDebug($"Death playback rejected by global cooldown during {(wasDelayed ? "delayed release" : "immediate playback")}: profile={profileId}, remaining={_nextDeathAllowedTime - Time.time:F2}s.");
                return;
            }
            if (IsPlaybackLimitReached(ESoundType.Death, profileId)) return;

            float distance = Vector3.Distance(localPlayer.Position, position);
            LoadedAudioClip loadedAudioClip = SelectClip(SoundDeath, ESoundType.Death);
            if (loadedAudioClip == null) return;

            AudioClip audioClip = loadedAudioClip.Clip;
            float clipLength = audioClip.length;
            float volume = AmandsGoofySoundsPlugin.GetSoundVolume(ESoundType.Death);
            AmandsGoofySoundsPlugin.LogDebug($"Death playback clip validated: profile={profileId}, delayed={wasDelayed}, file={loadedAudioClip.FileName}, volume={volume:F2}, {DescribeClip(audioClip)}.");
            BetterSource source = PlayClipAtPoint(position, audioClip, volume, ESoundType.Death, profileId, loadedAudioClip.FileName, out EAudioRouting effectiveRouting);
            if (source == null)
            {
                AmandsGoofySoundsPlugin.PluginLog.LogWarning($"BetterAudio did not provide a Death source: profile={profileId}, file={loadedAudioClip.FileName}, requestedRoute={AmandsGoofySoundsPlugin.AudioRouting.Value}.");
                return;
            }

            float playbackEndsAt = Time.time + clipLength;
            Playing[profileId] = playbackEndsAt;
            _nextDeathAllowedTime = playbackEndsAt + AmandsGoofySoundsPlugin.DeathCooldown.Value;
            _playbackStartedCount++;
            AmandsGoofySoundsPlugin.LogDebug($"Death playback started: profile={profileId}, delayed={wasDelayed}, file={loadedAudioClip.FileName}, route={effectiveRouting}, length={clipLength:F2}s, volume={volume:F2}, distance={distance:F2}, nextDeathAllowed={_nextDeathAllowedTime:F2}, activeCount={Playing.Count}.");
        }
        private void ClearPendingDeaths(string reason)
        {
            int pendingCount = _pendingDeathGenerations.Count;
            _pendingDeathGenerations.Clear();
            AmandsGoofySoundsPlugin.LogDebug($"Cleared {pendingCount} pending Death playbacks: reason={reason}.");
        }
        private void LogPlaybackSummary(string reason)
        {
            AmandsGoofySoundsPlugin.LogDebug($"Playback summary: reason={reason}, soundPack={_activeSoundPackName}, started={_playbackStartedCount}, characterRouteStarts={_characterRouteStartedCount}, voipRouteStarts={_voipRouteStartedCount}, voipFallbacks={_voipFallbackCount}, profileRejections={_profileRejectionCount}, limitRejections={_limitRejectionCount}, deathsDelayed={_deathDelayedCount}, deathsReleased={_deathReleasedCount}, activeEntries={Playing.Count}, pendingDeaths={_pendingDeathGenerations.Count}.");
        }
        private void ResetPlaybackSummary()
        {
            _playbackStartedCount = 0;
            _profileRejectionCount = 0;
            _limitRejectionCount = 0;
            _deathDelayedCount = 0;
            _deathReleasedCount = 0;
            _characterRouteStartedCount = 0;
            _voipRouteStartedCount = 0;
            _voipFallbackCount = 0;
            _voipFallbackWarningLogged = false;
        }
        private static LoadedAudioClip SelectClip(List<LoadedAudioClip> clips, ESoundType soundType)
        {
            AmandsGoofySoundsPlugin.LogDebug($"Selecting {soundType} clip from {clips.Count} loaded entries.");
            if (clips.Count == 0)
            {
                AmandsGoofySoundsPlugin.PluginLog.LogWarning($"Playback rejected because no {soundType} clips are loaded.");
                return null;
            }

            ShuffleBagState shuffleBag = GetShuffleBag(soundType);
            LoadedAudioClip loadedAudioClip = clips[shuffleBag.NextIndex(clips.Count)];
            AudioClip audioClip = loadedAudioClip?.Clip;
            if (audioClip == null || audioClip.length <= 0f || audioClip.loadState != AudioDataLoadState.Loaded)
            {
                AmandsGoofySoundsPlugin.PluginLog.LogWarning($"Playback rejected because the {soundType} clip is invalid: file={loadedAudioClip?.FileName}, {DescribeClip(audioClip)}.");
                return null;
            }

            return loadedAudioClip;
        }
        private static string DescribeClip(AudioClip audioClip)
        {
            if (audioClip == null) return "exists=False";
            return $"exists=True, instanceId={audioClip.GetInstanceID()}, length={audioClip.length:F2}s, samples={audioClip.samples}, channels={audioClip.channels}, frequency={audioClip.frequency}, loadState={audioClip.loadState}, hideFlags={audioClip.hideFlags}";
        }
        public void ReloadFiles()
        {
            if (_isShutdown)
            {
                AmandsGoofySoundsPlugin.PluginLog.LogWarning("Audio reload ignored because the component is shutting down.");
                return;
            }

            if (_loadCoroutine != null)
            {
                StopCoroutine(_loadCoroutine);
                _loadCoroutine = null;
            }

            int generation = ++_reloadGeneration;
            AmandsGoofySoundsPlugin.LogDebug($"Reloading audio files with generation={generation}; requestedSoundPack={AmandsGoofySoundsPlugin.SoundPack?.Value ?? AmandsGoofySoundsPlugin.DefaultSoundPackName}, clearing counts Random={SoundRandom.Count}, Hit={SoundHit.Count}, Death={SoundDeath.Count}, Spotted={SoundSpotted.Count}.");
            if (generation > 1) LogPlaybackSummary("audio reload");
            ClearPendingDeaths("audio reload");
            DisposeLoadedClips();
            Playing.Clear();
            _hitCooldownUntil.Clear();
            _nextDeathAllowedTime = 0f;
            ResetPlaybackSummary();
            AmandsGoofySoundsGoalPatch.ResetRuntimeState();
            List<AudioFileRequest> requests = BuildAudioFileQueue(out int initialRequestCount, out string activeSoundPackName);
            _activeSoundPackName = activeSoundPackName;
            _loadCoroutine = StartCoroutine(LoadAudioFiles(requests, initialRequestCount, generation));
        }
        private static List<AudioFileRequest> BuildAudioFileQueue(out int initialRequestCount, out string activeSoundPackName)
        {
            string soundPackRoot = ResolveSoundPackRoot(out activeSoundPackName);
            List<AudioFileRequest>[] categoryRequests =
            {
                GetAudioFiles(soundPackRoot, "Random", ESoundType.Random),
                GetAudioFiles(soundPackRoot, "Hit", ESoundType.Hit),
                GetAudioFiles(soundPackRoot, "Death", ESoundType.Death),
                GetAudioFiles(soundPackRoot, "Spotted", ESoundType.Spotted)
            };

            List<AudioFileRequest> requests = new List<AudioFileRequest>();
            for (int index = 0; index < InitialClipCountPerCategory; index++)
            {
                foreach (List<AudioFileRequest> category in categoryRequests)
                {
                    if (index < category.Count) requests.Add(category[index]);
                }
            }
            initialRequestCount = requests.Count;

            int remainingIndex = InitialClipCountPerCategory;
            bool addedRequest;
            do
            {
                addedRequest = false;
                foreach (List<AudioFileRequest> category in categoryRequests)
                {
                    if (remainingIndex >= category.Count) continue;
                    requests.Add(category[remainingIndex]);
                    addedRequest = true;
                }
                remainingIndex++;
            }
            while (addedRequest);

            AmandsGoofySoundsPlugin.LogDebug($"Audio load plan built: soundPack={activeSoundPackName}, root={soundPackRoot}, initialPerCategory={InitialClipCountPerCategory}, initialRequests={initialRequestCount}, remainingRequests={requests.Count - initialRequestCount}, totalRequests={requests.Count}.");
            return requests;
        }
        private static string ResolveSoundPackRoot(out string activeSoundPackName)
        {
            string defaultRoot = AmandsGoofySoundsPlugin.GetPluginAudioRoot();
            string requestedName = AmandsGoofySoundsPlugin.SoundPack?.Value ?? AmandsGoofySoundsPlugin.DefaultSoundPackName;
            if (string.Equals(requestedName, AmandsGoofySoundsPlugin.DefaultSoundPackName, StringComparison.OrdinalIgnoreCase))
            {
                activeSoundPackName = AmandsGoofySoundsPlugin.DefaultSoundPackName;
                AmandsGoofySoundsPlugin.LogDebug($"Resolved sound pack: requested={requestedName}, active={activeSoundPackName}, root={defaultRoot}.");
                return defaultRoot;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(requestedName) || Path.IsPathRooted(requestedName) ||
                    requestedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    requestedName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                    requestedName.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                    requestedName == "." || requestedName == "..")
                {
                    return FallBackToDefaultSoundPack(requestedName, defaultRoot, "the configured value is not a safe sound-pack directory name", out activeSoundPackName);
                }

                string packsRoot = Path.GetFullPath(Path.Combine(defaultRoot, "Packs"));
                string selectedRoot = Path.GetFullPath(Path.Combine(packsRoot, requestedName));
                if (!string.Equals(Path.GetDirectoryName(selectedRoot), packsRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return FallBackToDefaultSoundPack(requestedName, defaultRoot, "the resolved directory is outside the Packs root", out activeSoundPackName);
                }
                if (!Directory.Exists(selectedRoot))
                {
                    return FallBackToDefaultSoundPack(requestedName, defaultRoot, "the selected pack directory does not exist", out activeSoundPackName);
                }
                if (!AmandsGoofySoundsPlugin.HasSoundPackCategoryDirectory(selectedRoot))
                {
                    return FallBackToDefaultSoundPack(requestedName, defaultRoot, "the selected pack contains no category directory", out activeSoundPackName);
                }

                activeSoundPackName = new DirectoryInfo(selectedRoot).Name;
                AmandsGoofySoundsPlugin.LogDebug($"Resolved sound pack: requested={requestedName}, active={activeSoundPackName}, root={selectedRoot}.");
                return selectedRoot;
            }
            catch (Exception exception)
            {
                return FallBackToDefaultSoundPack(requestedName, defaultRoot, $"path resolution failed: {exception.Message}", out activeSoundPackName);
            }
        }
        private static string FallBackToDefaultSoundPack(string requestedName, string defaultRoot, string reason, out string activeSoundPackName)
        {
            activeSoundPackName = AmandsGoofySoundsPlugin.DefaultSoundPackName;
            AmandsGoofySoundsPlugin.PluginLog.LogWarning($"Sound pack '{requestedName}' is unavailable; falling back to {activeSoundPackName}. Reason: {reason}. DefaultRoot={defaultRoot}.");
            return defaultRoot;
        }
        private static List<AudioFileRequest> GetAudioFiles(string soundPackRoot, string directoryName, ESoundType soundType)
        {
            List<AudioFileRequest> requests = new List<AudioFileRequest>();
            string directoryPath = Path.Combine(soundPackRoot, directoryName);
            if (!Directory.Exists(directoryPath))
            {
                AmandsGoofySoundsPlugin.PluginLog.LogWarning($"Audio directory not found: type={soundType}, path={directoryPath}.");
                return requests;
            }

            FileInfo[] files = new DirectoryInfo(directoryPath).GetFiles();
            Array.Sort(files, (left, right) =>
            {
                int lengthComparison = left.Length.CompareTo(right.Length);
                return lengthComparison != 0 ? lengthComparison : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            });
            int supportedCount = 0;
            foreach (FileInfo fileInfo in files)
            {
                string file = fileInfo.FullName;
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension != ".wav" && extension != ".ogg" && extension != ".mp3")
                {
                    AmandsGoofySoundsPlugin.PluginLog.LogWarning($"Unsupported audio file skipped: type={soundType}, file={Path.GetFileName(file)}, extension={extension}.");
                    continue;
                }

                requests.Add(new AudioFileRequest(file, soundType));
                supportedCount++;
            }
            AmandsGoofySoundsPlugin.LogDebug($"Discovered {supportedCount} supported {soundType} files from {files.Length} directory entries.");
            return requests;
        }
        private IEnumerator LoadAudioFiles(List<AudioFileRequest> requests, int initialRequestCount, int generation)
        {
            AmandsGoofySoundsPlugin.LogDebug($"Staged audio load started: generation={generation}, files={requests.Count}, initialRequests={initialRequestCount}.");
            for (int requestIndex = 0; requestIndex < requests.Count; requestIndex++)
            {
                AudioFileRequest request = requests[requestIndex];
                if (_isShutdown || generation != _reloadGeneration)
                {
                    AmandsGoofySoundsPlugin.LogDebug($"Staged audio load stopped: requestedGeneration={generation}, currentGeneration={_reloadGeneration}, shutdown={_isShutdown}.");
                    yield break;
                }

                string extension = Path.GetExtension(request.Path).ToLowerInvariant();
                if (extension == ".ogg" || extension == ".mp3")
                {
                    AudioType audioType = extension == ".mp3" ? AudioType.MPEG : AudioType.OGGVORBIS;
                    string formatName = extension == ".mp3" ? "MP3" : "OGG";
                    yield return LoadCompressedAudioClip(request, generation, audioType, formatName);
                }
                else
                {
                    try
                    {
                        AmandsGoofySoundsPlugin.LogDebug($"Decoding PCM WAV: generation={generation}, type={request.SoundType}, file={Path.GetFileName(request.Path)}.");
                        LoadedAudioClip loadedAudioClip = LoadPcmWaveClip(request.Path);
                        AddLoadedAudioClip(loadedAudioClip, request.SoundType, generation);
                    }
                    catch (Exception exception)
                    {
                        AmandsGoofySoundsPlugin.PluginLog.LogError($"PCM WAV load failed: generation={generation}, type={request.SoundType}, file={Path.GetFileName(request.Path)}, exception={exception}.");
                    }
                }

                if (requestIndex + 1 == initialRequestCount)
                {
                    AmandsGoofySoundsPlugin.LogDebug($"Initial audio set processed: generation={generation}, targetPerCategory={InitialClipCountPerCategory}, counts Random={SoundRandom.Count}, Hit={SoundHit.Count}, Death={SoundDeath.Count}, Spotted={SoundSpotted.Count}.");
                }

                if (requestIndex + 1 >= initialRequestCount) yield return null;
            }

            if (!_isShutdown && generation == _reloadGeneration)
            {
                _loadCoroutine = null;
                AmandsGoofySoundsPlugin.LogDebug($"Staged audio load completed: generation={generation}, counts Random={SoundRandom.Count}, Hit={SoundHit.Count}, Death={SoundDeath.Count}, Spotted={SoundSpotted.Count}.");
            }
        }
        private void DisposeLoadedClips()
        {
            int retainedCount = SoundRandom.Count + SoundHit.Count + SoundDeath.Count + SoundSpotted.Count;
            foreach (LoadedAudioClip loadedAudioClip in SoundRandom) loadedAudioClip.Dispose();
            foreach (LoadedAudioClip loadedAudioClip in SoundHit) loadedAudioClip.Dispose();
            foreach (LoadedAudioClip loadedAudioClip in SoundDeath) loadedAudioClip.Dispose();
            foreach (LoadedAudioClip loadedAudioClip in SoundSpotted) loadedAudioClip.Dispose();
            SoundRandom.Clear();
            SoundHit.Clear();
            SoundDeath.Clear();
            SoundSpotted.Clear();
            RandomShuffleBag.Reset();
            HitShuffleBag.Reset();
            DeathShuffleBag.Reset();
            SpottedShuffleBag.Reset();
            AmandsGoofySoundsPlugin.LogDebug($"Destroyed {retainedCount} owned audio clips.");
        }
        private static LoadedAudioClip LoadPcmWaveClip(string path)
        {
            PcmWaveData waveData = DecodePcmWave(path);
            string fileName = Path.GetFileName(path);
            AudioClip audioClip = AudioClip.Create(fileName, waveData.SampleFrames, waveData.Channels, waveData.Frequency, false);
            if (audioClip == null) throw new InvalidOperationException("AudioClip.Create returned null.");

            if (!audioClip.SetData(waveData.Samples, 0))
            {
                UnityEngine.Object.Destroy(audioClip);
                throw new InvalidOperationException("AudioClip.SetData returned false.");
            }

            audioClip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            AmandsGoofySoundsPlugin.LogDebug($"PCM WAV decoded into independent clip: file={fileName}, {DescribeClip(audioClip)}.");
            return new LoadedAudioClip(audioClip, fileName);
        }
        private static PcmWaveData DecodePcmWave(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 12 || !HasFourCc(bytes, 0, "RIFF") || !HasFourCc(bytes, 8, "WAVE"))
                throw new InvalidDataException("File is not a RIFF/WAVE stream.");

            int formatTag = -1;
            int channels = 0;
            int frequency = 0;
            int blockAlign = 0;
            int bitsPerSample = 0;
            int dataOffset = -1;
            int dataSize = 0;
            int offset = 12;
            while (offset <= bytes.Length - 8)
            {
                uint rawChunkSize = ReadUInt32LittleEndian(bytes, offset + 4);
                if (rawChunkSize > int.MaxValue) throw new InvalidDataException("WAV chunk is too large.");
                int chunkSize = (int)rawChunkSize;
                int chunkDataOffset = checked(offset + 8);
                if (chunkSize > bytes.Length - chunkDataOffset) throw new InvalidDataException("WAV chunk extends beyond the file.");

                if (HasFourCc(bytes, offset, "fmt "))
                {
                    if (chunkSize < 16) throw new InvalidDataException("WAV fmt chunk is shorter than 16 bytes.");
                    formatTag = ReadUInt16LittleEndian(bytes, chunkDataOffset);
                    channels = ReadUInt16LittleEndian(bytes, chunkDataOffset + 2);
                    frequency = checked((int)ReadUInt32LittleEndian(bytes, chunkDataOffset + 4));
                    blockAlign = ReadUInt16LittleEndian(bytes, chunkDataOffset + 12);
                    bitsPerSample = ReadUInt16LittleEndian(bytes, chunkDataOffset + 14);
                }
                else if (HasFourCc(bytes, offset, "data"))
                {
                    dataOffset = chunkDataOffset;
                    dataSize = chunkSize;
                }

                offset = checked(chunkDataOffset + chunkSize + (chunkSize & 1));
            }

            if (formatTag != 1) throw new NotSupportedException($"WAV format tag {formatTag} is not PCM format 1.");
            if (bitsPerSample != 16) throw new NotSupportedException($"WAV bit depth {bitsPerSample} is not supported; expected 16-bit PCM.");
            if (channels <= 0 || frequency <= 0) throw new InvalidDataException("WAV channels or sample rate is invalid.");
            if (blockAlign != channels * sizeof(short)) throw new InvalidDataException($"WAV block alignment {blockAlign} does not match {channels} channels of 16-bit PCM.");
            if (dataOffset < 0 || dataSize <= 0) throw new InvalidDataException("WAV data chunk is missing or empty.");
            if (dataSize % blockAlign != 0) throw new InvalidDataException("WAV data size is not aligned to complete sample frames.");

            int sampleFrames = dataSize / blockAlign;
            int totalSamples = checked(sampleFrames * channels);
            float[] samples = new float[totalSamples];
            int byteOffset = dataOffset;
            for (int index = 0; index < totalSamples; index++)
            {
                short sample = unchecked((short)(bytes[byteOffset] | (bytes[byteOffset + 1] << 8)));
                samples[index] = sample / 32768f;
                byteOffset += sizeof(short);
            }

            return new PcmWaveData(samples, sampleFrames, channels, frequency);
        }
        private IEnumerator LoadCompressedAudioClip(AudioFileRequest request, int generation, AudioType audioType, string formatName)
        {
            string fileName = Path.GetFileName(request.Path);
            string uri = new Uri(request.Path).AbsoluteUri;
            AmandsGoofySoundsPlugin.LogDebug($"Loading {formatName} through Unity for independent copy: generation={generation}, type={request.SoundType}, file={fileName}, audioType={audioType}.");
            UnityWebRequest webRequest = UnityWebRequestMultimedia.GetAudioClip(uri, audioType);
            DownloadHandlerAudioClip downloadHandler = webRequest.downloadHandler as DownloadHandlerAudioClip;
            if (downloadHandler != null)
            {
                downloadHandler.compressed = false;
                downloadHandler.streamAudio = false;
            }
            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = webRequest.SendWebRequest();
            }
            catch (Exception exception)
            {
                webRequest.Dispose();
                AmandsGoofySoundsPlugin.PluginLog.LogError($"{formatName} request could not start: generation={generation}, type={request.SoundType}, file={fileName}, exception={exception}.");
                yield break;
            }

            yield return operation;

            LoadedAudioClip loadedAudioClip = null;
            try
            {
                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    AmandsGoofySoundsPlugin.PluginLog.LogWarning($"{formatName} request failed: generation={generation}, type={request.SoundType}, file={fileName}, result={webRequest.result}, error={webRequest.error}.");
                }
                else
                {
                    AudioClip sourceClip = DownloadHandlerAudioClip.GetContent(webRequest);
                    loadedAudioClip = CreateIndependentCopy(sourceClip, fileName);
                    AmandsGoofySoundsPlugin.LogDebug($"{formatName} decoded into independent clip: file={fileName}, {DescribeClip(loadedAudioClip.Clip)}.");
                }
            }
            catch (Exception exception)
            {
                loadedAudioClip?.Dispose();
                loadedAudioClip = null;
                AmandsGoofySoundsPlugin.PluginLog.LogError($"{formatName} independent-copy load failed: generation={generation}, type={request.SoundType}, file={fileName}, exception={exception}.");
            }
            finally
            {
                webRequest.Dispose();
            }

            if (loadedAudioClip != null) AddLoadedAudioClip(loadedAudioClip, request.SoundType, generation);
        }
        private static LoadedAudioClip CreateIndependentCopy(AudioClip sourceClip, string fileName)
        {
            if (sourceClip == null) throw new InvalidDataException("Decoded source AudioClip is null.");
            if (sourceClip.samples <= 0 || sourceClip.channels <= 0 || sourceClip.frequency <= 0)
                throw new InvalidDataException($"Decoded source AudioClip has invalid metadata: {DescribeClip(sourceClip)}.");

            float[] samples = new float[checked(sourceClip.samples * sourceClip.channels)];
            if (!sourceClip.GetData(samples, 0)) throw new InvalidDataException("AudioClip.GetData returned false.");

            AudioClip independentClip = AudioClip.Create(fileName, sourceClip.samples, sourceClip.channels, sourceClip.frequency, false);
            if (independentClip == null) throw new InvalidOperationException("AudioClip.Create returned null for the independent copy.");
            if (!independentClip.SetData(samples, 0))
            {
                UnityEngine.Object.Destroy(independentClip);
                throw new InvalidOperationException("AudioClip.SetData returned false for the independent copy.");
            }

            independentClip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return new LoadedAudioClip(independentClip, fileName);
        }
        private void AddLoadedAudioClip(LoadedAudioClip loadedAudioClip, ESoundType soundType, int generation)
        {
            if (_isShutdown || generation != _reloadGeneration)
            {
                AmandsGoofySoundsPlugin.LogDebug($"Discarding stale independent audio clip: requestedGeneration={generation}, currentGeneration={_reloadGeneration}, type={soundType}, file={loadedAudioClip.FileName}.");
                loadedAudioClip.Dispose();
                return;
            }

            switch (soundType)
            {
                case ESoundType.Random:
                    SoundRandom.Add(loadedAudioClip);
                    break;
                case ESoundType.Hit:
                    SoundHit.Add(loadedAudioClip);
                    break;
                case ESoundType.Death:
                    SoundDeath.Add(loadedAudioClip);
                    break;
                case ESoundType.Spotted:
                    SoundSpotted.Add(loadedAudioClip);
                    break;
            }
            AmandsGoofySoundsPlugin.LogDebug($"Independent audio load completed: generation={generation}, type={soundType}, file={loadedAudioClip.FileName}, {DescribeClip(loadedAudioClip.Clip)}, counts Random={SoundRandom.Count}, Hit={SoundHit.Count}, Death={SoundDeath.Count}, Spotted={SoundSpotted.Count}.");
        }
        private static bool HasFourCc(byte[] bytes, int offset, string value)
        {
            return offset >= 0 && offset <= bytes.Length - 4 && value.Length == 4 &&
                bytes[offset] == value[0] && bytes[offset + 1] == value[1] && bytes[offset + 2] == value[2] && bytes[offset + 3] == value[3];
        }
        private static ushort ReadUInt16LittleEndian(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }
        private static uint ReadUInt32LittleEndian(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
        }
    }
    public enum EAudioRouting
    {
        Character,
        VoipMixer
    }
    public enum ESoundType
    {
        Random,
        Hit,
        Death,
        Spotted
    }
}
