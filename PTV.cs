using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Video;

namespace PTV {
    [BepInPlugin(modGUID, modName, modVersion)]
    public class PTV : BaseUnityPlugin {
        public const string modGUID = "Gavinboy3000.PTV";
        public const string modName = "PTV";
        public const string modVersion = "1.0.0.0";

        private readonly Harmony harmony = new Harmony(modGUID);
        public static ManualLogSource logger = new ManualLogSource(modGUID);

        void Awake() {
            logger = Logger; // if this line doesn't exist the game crashes

            VideoManager.Load();
            harmony.PatchAll();
            logger.LogInfo($"{modName} version {modVersion} has loaded!");
        }
    }

    [HarmonyPatch(typeof(TVScript))]
    internal static class TVScriptPatch {
        private static FieldInfo currentClipProperty = typeof(TVScript).GetField("currentClip", BindingFlags.NonPublic | BindingFlags.Instance);
        private static FieldInfo currentTimeProperty = typeof(TVScript).GetField("currentClipTime", BindingFlags.NonPublic | BindingFlags.Instance);
        private static MethodInfo setMatMethod = typeof(TVScript).GetMethod("SetTVScreenMaterial", BindingFlags.NonPublic | BindingFlags.Instance);
        private static MethodInfo onEnableMethod = typeof(TVScript).GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance);

        private static bool firstPlay = true;
        private static RenderTexture renderTexture;
        private static VideoPlayer currentVP;
        private static VideoPlayer nextVP;

        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        public static bool Update(TVScript __instance) {
            if (currentVP != null) return false;

            currentVP = __instance.GetComponent<VideoPlayer>();
            renderTexture = currentVP.targetTexture;

            if (VideoManager.Videos.Count < 1) return false;

            VideoManager.Videos.Shuffle();
            PrepareVideo(__instance, 0);

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch("TurnTVOnOff")]
        public static bool TurnTVOnOff(TVScript __instance, bool on) {
            if (VideoManager.Videos.Count < 1) return false;
            if (on && !firstPlay) currentClipProperty.SetValue(__instance, NextIndex((int)currentClipProperty.GetValue(__instance)));

            __instance.tvOn = on;

            if (on) {
                PlayVideo(__instance);
                __instance.tvSFX.PlayOneShot(__instance.switchTVOn);
                WalkieTalkie.TransmitOneShotAudio(__instance.tvSFX, __instance.switchTVOn);
            }
            else {
                __instance.video.Stop();
                __instance.tvSFX.PlayOneShot(__instance.switchTVOff);
                WalkieTalkie.TransmitOneShotAudio(__instance.tvSFX, __instance.switchTVOff);
            }

            setMatMethod.Invoke(__instance, new object[] { on });
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch("TVFinishedClip")]
        public static bool TVFinishedClip(TVScript __instance, VideoPlayer source) {
            currentTimeProperty.SetValue(__instance, 0f);
            currentClipProperty.SetValue(__instance, NextIndex((int)currentClipProperty.GetValue(__instance)));
            PlayVideo(__instance);

            return false;
        }

        private static void PrepareVideo(TVScript tv, int index = -1) {
            if (index == -1) index = NextIndex((int)currentClipProperty.GetValue(tv));
            if (nextVP != null && nextVP.gameObject.activeInHierarchy) GameObject.Destroy(nextVP);

            nextVP = tv.gameObject.AddComponent<VideoPlayer>();
            nextVP.playOnAwake = false;
            //nextVP.isLooping = true;
            nextVP.source = VideoSource.Url;
            nextVP.controlledAudioTrackCount = 1;
            nextVP.audioOutputMode = VideoAudioOutputMode.AudioSource;
            nextVP.SetTargetAudioSource(0, tv.tvSFX);
            nextVP.url = $"file://{VideoManager.Videos[index]}";
            nextVP.skipOnDrop = true;
            nextVP.Prepare();
        }

        private static void PlayVideo(TVScript tv) {
            firstPlay = false;

            if (VideoManager.Videos.Count < 1) return;

            if (nextVP != null) {
                var temp = currentVP;

                tv.video = currentVP = nextVP;
                nextVP = null;
                GameObject.Destroy(temp);
                onEnableMethod.Invoke(tv, new object[] { });
            }

            currentTimeProperty.SetValue(tv, 0f);
            tv.video.targetTexture = renderTexture;
            tv.video.Play();
            PrepareVideo(tv);
            PTV.logger.LogInfo("Playing video");
        }

        private static int NextIndex(int index) {
            if (index + 1 >= VideoManager.Videos.Count) VideoManager.Videos.Shuffle();

            return (index + 1) % VideoManager.Videos.Count;
        }

        private static void Shuffle<T>(this IList<T> list) {
            int n = list.Count;

            while (n > 1) {
                n--;

                int k = Random.Range(0, n + 1);
                T value = list[k];

                list[k] = list[n];
                list[n] = value;
            }
        }
    }

    internal static class VideoManager {
        public static List<string> Videos = new List<string>();

        public static void Load() {
            string myPath = Path.Combine(Paths.PluginPath, "Gavinboy3000-PTV");

            if (!Directory.Exists(myPath)) return;

            var videoFiles = Directory.GetFiles(myPath, "*.mp4");

            Videos.AddRange(videoFiles);
            PTV.logger.LogInfo($"Successfully loaded {Videos.Count} videos!");
        }
    }
}
