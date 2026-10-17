using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using EFT;
using UnityEngine;

[assembly: System.Reflection.AssemblyMetadata("Author", "ZantetsukenAR3")]

namespace PlopPro
{
    [BepInPlugin("zantetsukenar3.ploppro", "Plop Pro", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<float> SlowPitch, WalkPitch, RunPitch;
        internal static ConfigEntry<float> SlowThreshold;
        internal static ConfigEntry<float> SlowVolumeBoost, WalkVolumeBoost, RunVolumeBoost;
        internal static ConfigEntry<float> SlowMinVolume, WalkMinVolume, RunMinVolume;
        internal static ConfigEntry<bool> VolumeEnabled, Debug;
        internal static ConfigEntry<bool> EnablePuddles, EnableSwamps;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            SlowPitch = Config.Bind("Pitch", "Slow", 0.25f, "Pitch multiplier when not sprinting and ClampedSpeed is below SlowThreshold.");
            WalkPitch = Config.Bind("Pitch", "Walk", 0.35f, "Pitch multiplier for normal walking.");
            RunPitch = Config.Bind("Pitch", "Run", 0.60f, "Pitch multiplier when IsSprintEnabled is true.");
            SlowThreshold = Config.Bind("Movement", "SlowThreshold", 0.30f, "Speed boundary for slow versus normal walking. Sprinting uses IsSprintEnabled instead.");

            EnablePuddles = Config.Bind("Surfaces", "EnablePuddles", true, "Apply footstep adjustments to puddles and water.");
            EnableSwamps = Config.Bind("Surfaces", "EnableSwamps", true, "Apply footstep adjustments to swamp surfaces.");

            VolumeEnabled = Config.Bind("Volume", "Enabled", true, "Enable selected-surface footstep volume adjustment.");
            SlowVolumeBoost = Config.Bind("Volume", "SlowBoost", 1.5f, "Multiplier for slow selected-surface footstep volume.");
            WalkVolumeBoost = Config.Bind("Volume", "WalkBoost", 1.0f, "Multiplier for walking selected-surface footstep volume.");
            RunVolumeBoost = Config.Bind("Volume", "RunBoost", 1.5f, "Multiplier for sprinting selected-surface footstep volume.");
            SlowMinVolume = Config.Bind("Volume", "SlowMinimum", 0.075f, "Minimum volume passed to SoundBank.Play for slow selected-surface steps (0-1).");
            WalkMinVolume = Config.Bind("Volume", "WalkMinimum", 0.10f, "Minimum volume passed to SoundBank.Play for walking selected-surface steps (0-1).");
            RunMinVolume = Config.Bind("Volume", "RunMinimum", 0.10f, "Minimum volume passed to SoundBank.Play for sprinting selected-surface steps (0-1).");
            Debug = Config.Bind("Diagnostics", "LogFootsteps", false, "Log up to 150 local and 150 other selected-surface footsteps with pitch/volume call counts.");

            _harmony = new Harmony("zantetsukenar3.ploppro");
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("Plop Pro v1.0.0 by ZantetsukenAR3 loaded. Puddle and swamp support enabled by default.");
        }

        private void OnDestroy() => _harmony?.UnpatchSelf();
    }

    internal static class SurfaceFilter
    {
        internal static bool Matches(BaseBallistic.ESurfaceSound surface)
        {
            return (Plugin.EnablePuddles.Value && surface == BaseBallistic.ESurfaceSound.Puddle) ||
                   (Plugin.EnableSwamps.Value && surface == BaseBallistic.ESurfaceSound.Swamp);
        }

        internal static string Name(BaseBallistic.ESurfaceSound surface)
        {
            return surface == BaseBallistic.ESurfaceSound.Swamp ? "SWAMP" : "PUDDLE";
        }
    }

    internal static class StepContext
    {
        [ThreadStatic] internal static bool Active;
        [ThreadStatic] internal static int Depth;
        [ThreadStatic] internal static float PitchMultiplier;
        [ThreadStatic] internal static float VolumeBoost;
        [ThreadStatic] internal static float MinVolume;
        [ThreadStatic] internal static int PitchCalls;
        [ThreadStatic] internal static int VolumeCalls;
        [ThreadStatic] internal static float LastOriginalPitch;
        [ThreadStatic] internal static float LastModifiedPitch;
        [ThreadStatic] internal static float LastOriginalVolume;
        [ThreadStatic] internal static float LastModifiedVolume;
        [ThreadStatic] internal static bool LogThisStep;
        [ThreadStatic] internal static float Speed;
        [ThreadStatic] internal static bool SprintEnabled;
        [ThreadStatic] internal static string Band;
        [ThreadStatic] internal static string PlayerType;
        [ThreadStatic] internal static string SurfaceName;
        internal static int LocalCount;
        internal static int OtherCount;
        internal static Player LocalPlayer;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlayStepSound))]
    internal static class StepPatch
    {
        private static void Prefix(Player __instance)
        {
            if (__instance != null && __instance.IsYourPlayer) StepContext.LocalPlayer = __instance;
            StepContext.Depth++;
            if (StepContext.Depth != 1 || __instance == null ||
                !SurfaceFilter.Matches(__instance.CurrentSurface))
                return;

            float speed = __instance.MovementContext.ClampedSpeed;
            bool sprint = __instance.IsSprintEnabled;
            string band;
            if (sprint) band = "RUN";
            else if (speed < Plugin.SlowThreshold.Value) band = "SLOW";
            else band = "WALK";

            StepContext.Active = true;
            StepContext.SurfaceName = SurfaceFilter.Name(__instance.CurrentSurface);
            StepContext.Band = band;
            StepContext.Speed = speed;
            StepContext.SprintEnabled = sprint;
            StepContext.PitchMultiplier = band == "RUN" ? Plugin.RunPitch.Value :
                band == "SLOW" ? Plugin.SlowPitch.Value : Plugin.WalkPitch.Value;
            StepContext.VolumeBoost = band == "RUN" ? Plugin.RunVolumeBoost.Value :
                band == "SLOW" ? Plugin.SlowVolumeBoost.Value : Plugin.WalkVolumeBoost.Value;
            StepContext.MinVolume = band == "RUN" ? Plugin.RunMinVolume.Value :
                band == "SLOW" ? Plugin.SlowMinVolume.Value : Plugin.WalkMinVolume.Value;
            StepContext.PitchCalls = 0;
            StepContext.VolumeCalls = 0;
            bool local = __instance.IsYourPlayer;
            int count = local ? ++StepContext.LocalCount : ++StepContext.OtherCount;
            StepContext.LogThisStep = Plugin.Debug.Value && count <= 150;
            StepContext.PlayerType = local ? "LOCAL" : "OTHER";
        }

        private static void Postfix()
        {
            if (StepContext.Depth != 1 || !StepContext.Active || !StepContext.LogThisStep) return;
            string pitch = StepContext.PitchCalls > 0
                ? $"pitch={StepContext.LastOriginalPitch:F2}->{StepContext.LastModifiedPitch:F2}"
                : "NO PITCH CALL";
            string volume = StepContext.VolumeCalls > 0
                ? $"volume={StepContext.LastOriginalVolume:F3}->{StepContext.LastModifiedVolume:F3}"
                : "NO VOLUME CALL";
            Plugin.Log.LogInfo($"{StepContext.SurfaceName} STEP | {StepContext.PlayerType} | speed={StepContext.Speed:F2} | sprintEnabled={StepContext.SprintEnabled} | band={StepContext.Band} | pitch calls={StepContext.PitchCalls} | {pitch} | volume calls={StepContext.VolumeCalls} | {volume}");
        }

        private static Exception Finalizer(Exception __exception)
        {
            StepContext.Depth--;
            if (StepContext.Depth <= 0)
            {
                StepContext.Depth = 0;
                StepContext.Active = false;
                StepContext.LogThisStep = false;
            }
            return __exception;
        }
    }

    // Sprint is emitted by Player.Sprint(), which directly calls SprintSoundBank.Play().
    // Instead of patching the coroutine (which caused regressions), recognise only
    // the local player's sprint bank when SoundBank.Play is entered.
    [HarmonyPatch(typeof(SoundBank), "Play", typeof(BetterSource), typeof(EnvironmentType),
        typeof(float), typeof(float), typeof(float), typeof(bool), typeof(bool))]
    internal static class SprintBankPatch
    {
        private static int _logs;
        private static bool _warned;
        [ThreadStatic] private static bool _ownsContext;
        [ThreadStatic] private static string _surfaceName;

        [HarmonyPriority(Priority.First)]
        private static void Prefix(SoundBank __instance)
        {
            _ownsContext = false;
            if (StepContext.Active || __instance == null) return;
            Player player = StepContext.LocalPlayer;
            if (player == null || !SurfaceFilter.Matches(player.CurrentSurface) || !player.IsSprintEnabled) return;
            try
            {
                object set = AccessTools.Field(player.GetType(), "_currentSet")?.GetValue(player);
                if (set == null) return;
                Type type = set.GetType();
                FieldInfo field = AccessTools.Field(type, "SprintSoundBank");
                object bank = field != null ? field.GetValue(set) :
                    type.GetProperty("SprintSoundBank", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(set, null);
                if (!ReferenceEquals(bank, __instance)) return;
                _ownsContext = true;
                _surfaceName = SurfaceFilter.Name(player.CurrentSurface);
                StepContext.Active = true;
                StepContext.PitchMultiplier = Plugin.RunPitch.Value;
                StepContext.VolumeBoost = Plugin.RunVolumeBoost.Value;
                StepContext.MinVolume = Plugin.RunMinVolume.Value;
                StepContext.PitchCalls = 0;
                StepContext.VolumeCalls = 0;
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning("Sprint sound-bank interception failed: " + ex);
                }
            }
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            if (!_ownsContext) return;
            if (Plugin.Debug.Value && _logs++ < 100)
                Plugin.Log.LogInfo($"{_surfaceName} SPRINT | bank=ORIGINAL SPRINT | pitch calls={StepContext.PitchCalls} | pitch={StepContext.LastOriginalPitch:F2}->{StepContext.LastModifiedPitch:F2} | volume calls={StepContext.VolumeCalls} | volume={StepContext.LastOriginalVolume:F3}->{StepContext.LastModifiedVolume:F3}");
            Clear();
        }

        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null && _ownsContext) Clear();
            return __exception;
        }

        private static void Clear()
        {
            _ownsContext = false;
            StepContext.Active = false;
        }
    }

    [HarmonyPatch(typeof(SoundBank), "get_RandomPitch")]
    internal static class PitchPatch
    {
        private static void Postfix(ref float __result)
        {
            if (!StepContext.Active) return;
            float original = __result;
            __result = Mathf.Clamp(original * StepContext.PitchMultiplier, 0.1f, 3f);
            StepContext.PitchCalls++;
            StepContext.LastOriginalPitch = original;
            StepContext.LastModifiedPitch = __result;
        }
    }

    // Explicit overload: Play(BetterSource, EnvironmentType, float distance,
    //                         float volume, float blendParameter, bool forceStereo, bool oneShot)
    [HarmonyPatch(typeof(SoundBank), "Play", typeof(BetterSource), typeof(EnvironmentType),
        typeof(float), typeof(float), typeof(float), typeof(bool), typeof(bool))]
    internal static class VolumePatch
    {
        private static void Prefix(ref float volume)
        {
            if (!StepContext.Active) return;
            float original = volume;
            if (Plugin.VolumeEnabled.Value)
            {
                float boosted = volume * Mathf.Max(0f, StepContext.VolumeBoost);
                volume = Mathf.Clamp01(Mathf.Max(boosted, StepContext.MinVolume));
            }
            StepContext.VolumeCalls++;
            StepContext.LastOriginalVolume = original;
            StepContext.LastModifiedVolume = volume;
        }
    }
}

