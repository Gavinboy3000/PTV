using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HarmonyLib.Tools;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Video;

namespace PTV {
    [BepInPlugin(modGUID, modName, modVersion)]
    public class PTV : BaseUnityPlugin {
        public const string modGUID = "Gavinboy3000.PTV";
        public const string modName = "PTV";
        public const string modVersion = "1.0.0";

        public static ConfigEntry<bool> configBeginWithIntro;
        public static ConfigEntry<bool> configTVInShop;

        private readonly Harmony harmony = new Harmony(modGUID);
        public static ManualLogSource logger = new ManualLogSource(modGUID);
        public static GameObject networkPrefab;

        void Awake() {
            configBeginWithIntro = Config.Bind("Misc", "Begin With Intro", true, "Puts main intro at the beginning of every shuffle.");
            configTVInShop = Config.Bind("QOL", "TV in Shop", true, "Makes it so the television is always in the shop.");

            NetcodePatcher();

            logger = Logger; // if this line doesn't exist the game crashes

            string assetPath = Path.Combine(Path.GetDirectoryName(Info.Location), "asset");
            AssetBundle bundle = AssetBundle.LoadFromFile(assetPath);

            networkPrefab = bundle.LoadAsset<GameObject>("Assets/NetworkHandler.prefab");
            networkPrefab.AddComponent<NetworkHandler>();
            VideoManager.Load(Path.GetDirectoryName(Info.Location));
            harmony.PatchAll();
            logger.LogInfo($"{modName} version {modVersion} has loaded!");
        }

        private static void NetcodePatcher() {
            var types = Assembly.GetExecutingAssembly().GetTypes();

            foreach (var type in types) {
                var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

                foreach (var method in methods) {
                    var attributes = method.GetCustomAttributes(typeof(RuntimeInitializeOnLoadMethodAttribute), false);

                    if (attributes.Length > 0) method.Invoke(null, null);
                }
            }
        }
    }

    [HarmonyPatch(typeof(TVScript))]
    internal static class TVScriptPatch {
        private static MethodInfo setMatMethod = typeof(TVScript).GetMethod("SetTVScreenMaterial", BindingFlags.NonPublic | BindingFlags.Instance);
        private static MethodInfo onEnableMethod = typeof(TVScript).GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance);

        private static TVScript tv;
        private static RenderTexture renderTexture;
        private static VideoPlayer currentVP, nextVP;
        private static int currentIndex = -1, nextIndex = 0;
        private static double forceTime = -1;

        [HarmonyPrefix, HarmonyPatch("Update")]
        public static bool Update(TVScript __instance) {
            tv = __instance;

            if (currentVP != null) return false;

            currentVP = tv.GetComponent<VideoPlayer>();
            renderTexture = currentVP.targetTexture;

            if (VideoManager.Videos.Count < 1) return false;
            if (!NetworkHandler.Hosting()) return false;
            
            TurnTVOnOff(tv, false);
            VideoManager.ShuffledVideos = new List<string>(VideoManager.Videos);
            VideoManager.Shuffle();
            currentIndex = -1;
            nextIndex = 0;
            PrepareNextVideo(tv);

            return false;
        }

        [HarmonyPrefix, HarmonyPatch("TurnTVOnOff")]
        public static bool TurnTVOnOff(TVScript __instance, bool on) {
            tv = __instance;

            if (VideoManager.Videos.Count < 1) return false;

            tv.tvOn = on;

            if (on) {
                PlayNextVideo();
                tv.tvSFX.PlayOneShot(tv.switchTVOn);
                WalkieTalkie.TransmitOneShotAudio(tv.tvSFX, tv.switchTVOn);
            }
            else {
                tv.video.Stop();
                tv.tvSFX.PlayOneShot(tv.switchTVOff);
                WalkieTalkie.TransmitOneShotAudio(tv.tvSFX, tv.switchTVOff);
            }

            setMatMethod.Invoke(tv, new object[] { on });

            return false;
        }

        [HarmonyPrefix, HarmonyPatch("TVFinishedClip")]
        public static bool TVFinishedClip(TVScript __instance, VideoPlayer source) {
            tv = __instance;

            PlayNextVideo();

            return false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Terminal), nameof(Terminal.RotateShipDecorSelection))]
        public static void AddTV(Terminal __instance) {
            if (!PTV.configTVInShop.Value) return;

            int index = -1;

            for (int i = 0; i < StartOfRound.Instance.unlockablesList.unlockables.Count; i++) {
                if (StartOfRound.Instance.unlockablesList.unlockables[i].unlockableName == "Television") {
                    index = i;
                    break;
                }
            }

            if (index == -1) return;

            TerminalNode itemNode = StartOfRound.Instance.unlockablesList.unlockables[index].shopSelectionNode;

            if (__instance.ShipDecorSelection.Contains(itemNode)) return;

            __instance.ShipDecorSelection.Add(itemNode);
        }

        private static void PrepareNextVideo(TVScript tv) {
            if (nextVP != null && nextVP.gameObject.activeInHierarchy) GameObject.Destroy(nextVP);

            nextVP = tv.gameObject.AddComponent<VideoPlayer>();
            nextVP.playOnAwake = false;
            nextVP.source = VideoSource.Url;
            nextVP.controlledAudioTrackCount = 1;
            nextVP.audioOutputMode = VideoAudioOutputMode.AudioSource;
            nextVP.SetTargetAudioSource(0, tv.tvSFX);
            nextVP.skipOnDrop = true;

            if (NetworkHandler.Hosting()) nextVP.url = $"file://{VideoManager.ShuffledVideos[nextIndex]}";
            else nextVP.url = $"file://{VideoManager.Videos[nextIndex]}";

            nextVP.Prepare();
        }

        private static void PlayNextVideo(int index = -1) {
            if (nextVP == null) return;

            VideoPlayer temp = currentVP;

            tv.video = currentVP = nextVP;
            nextVP = null;
            GameObject.Destroy(temp);
            onEnableMethod.Invoke(tv, new object[] { });
            currentIndex = nextIndex;

            if (NetworkHandler.Hosting()) nextIndex = VideoManager.NextIndex(currentIndex);
            else if (index != -1) nextIndex = index;

            if (NetworkHandler.Hosting()) UpdateInfo();

            tv.video.targetTexture = renderTexture;
            tv.video.Play();
            PrepareNextVideo(tv);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.SetSingleton))]
        public static void Init() {
            NetworkManager.Singleton.OnClientConnectedCallback += ClientConnected;
        }

        static void ClientConnected(ulong id) {
            if (NetworkHandler.Hosting() && id != NetworkManager.ServerClientId) {
                if (tv.tvOn) UpdateInfo(1);
                else UpdateInfo(0);
            }
        }

        private static void ForceTime(VideoPlayer source) {
            if (forceTime == -1) return;

            source.time = forceTime;
            source.prepareCompleted -= ForceTime;
            forceTime = -1;
        }

        public static void RecievedInfo(int current, double time, int next, int tvOn) {
            if (NetworkHandler.Hosting()) return;
            if (tvOn == 0) TurnTVOnOff(tv, false);
            else if (tvOn == 1) {
                nextIndex = current;
                PrepareNextVideo(tv);
                TurnTVOnOff(tv, true);
            }

            if (currentIndex != current) {
                if (tv.tvOn) {
                    nextIndex = current;
                    PrepareNextVideo(tv);
                    PlayNextVideo(next);
                }
                else {
                    currentIndex = current;
                    nextIndex = next;
                    PrepareNextVideo(tv);
                }
            }
            else if (nextIndex != next) {
                nextIndex = next;
                PrepareNextVideo(tv);
            }

            double timeDifference = time - currentVP.time; // made variable in case i want to use it for smoother syncing

            if (Mathf.Abs((float) timeDifference) >= 1) {
                forceTime = time;
                currentVP.prepareCompleted += ForceTime;
            }
        }

        private static void UpdateInfo(int tvOn = -1) {
            NetworkHandler.instance.UpdateInfoClientRpc(VideoManager.GetTrueIndex(currentIndex), currentVP.time, VideoManager.GetTrueIndex(nextIndex), tvOn);
        }
    }

    internal static class VideoManager {
        public static List<string> Videos = new List<string>();
        public static List<string> ShuffledVideos;

        public static void Load(string basePath) {
            string videosPath = Path.Combine(basePath, "Videos");

            if (!Directory.Exists(basePath)) return;

            string[] videoFiles;

            if (!Directory.Exists(videosPath)) {
                videoFiles = Directory.GetFiles(basePath, "*.mp4");

                if (videoFiles.Length < 1) return;

                Directory.CreateDirectory(videosPath);

                for (int i = 0; i < videoFiles.Length; i++) {
                    string destFile = Path.Combine(videosPath, Path.GetFileName(videoFiles[i]));

                    File.Move(videoFiles[i], destFile);
                }
            }

            videoFiles = Directory.GetFiles(videosPath, "*.mp4");
            Videos.AddRange(videoFiles);
            PTV.logger.LogInfo($"Successfully loaded {Videos.Count} videos!");
        }

        public static void Shuffle() {
            int n = ShuffledVideos.Count;

            while (n > 1) {
                n--;

                int k = Random.Range(0, n + 1); // UnityEngine
                string value = ShuffledVideos[k];

                ShuffledVideos[k] = ShuffledVideos[n];
                ShuffledVideos[n] = value;
            }

            if (!PTV.configBeginWithIntro.Value) return;

            string introPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Videos", "MainIntro.mp4");
            int introIndex = ShuffledVideos.IndexOf(introPath);

            if (introIndex == -1) return;

            string temp = ShuffledVideos[0];

            ShuffledVideos[0] = introPath;
            ShuffledVideos[introIndex] = temp;
        }
        public  static int GetTrueIndex(int index) {
            if (index < 0) index = 0;

            int trueIndex = Videos.IndexOf(ShuffledVideos[index]);

            if (trueIndex == -1) return index;

            return trueIndex;
        }


        public static int NextIndex(int index) {
            if (index + 1 >= VideoManager.Videos.Count) Shuffle();

            return (index + 1) % VideoManager.Videos.Count;
        }
    }

    [HarmonyPatch]
    public class NetworkObjectManager {
        [HarmonyPostfix, HarmonyPatch(typeof(GameNetworkManager), "Start")]
        static void Init(GameNetworkManager __instance) {
            __instance.GetComponent<NetworkManager>().AddNetworkPrefab(PTV.networkPrefab);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(StartOfRound), "Awake")]
        static void SpawnNetworkHandler() {
            if (NetworkHandler.Hosting()) {
                GameObject networkHandlerHost = GameObject.Instantiate(PTV.networkPrefab);

                networkHandlerHost.GetComponent<NetworkObject>().Spawn();
            }
        }
    }

    public class NetworkHandler : NetworkBehaviour {
        public static NetworkHandler instance;

        public static bool Hosting() {
            return NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer;
        }

        public override void OnNetworkSpawn() {
            instance = this;
            base.OnNetworkSpawn();
        }

        [ClientRpc]
        public void UpdateInfoClientRpc(int current, double time, int next, int tvOn) {
            TVScriptPatch.RecievedInfo(current, time, next, tvOn);
        }
    }
}
