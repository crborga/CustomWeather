using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace CustomWeather
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    internal class CustomWeather : BaseUnityPlugin
    {
        public const string PluginGUID = "local.CustomWeather";
        public const string PluginName = "Custom Weather";
        public const string PluginVersion = "1.0.1"; // keep in sync with <Version> in the .csproj

        public static bool isModded = true;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> RainMultiplier;
        internal static ConfigEntry<float> StormMultiplier;
        internal static ConfigEntry<float> MistMultiplier;
        internal static ConfigEntry<bool> StormAffectsOcean;
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch. When false, vanilla weather weights are left untouched.");
            RainMultiplier = BindMultiplier("RainMultiplier", 0.5f,
                "Multiplier applied to the weight of every environment whose name contains 'Rain'. 1 = vanilla, 0.5 = half as likely, 0 = never.");
            StormMultiplier = BindMultiplier("StormMultiplier", 0.5f,
                "Multiplier applied to the weight of every environment whose name contains 'Storm'.");
            MistMultiplier = BindMultiplier("MistMultiplier", 0.5f,
                "Multiplier applied to the weight of the 'Misty' environment.");
            StormAffectsOcean = Config.Bind("Weights", "StormAffectsOcean", false,
                "If false (original Custom Weather behaviour), storms in the Ocean biome keep their vanilla weight.");

            // Server-side edits are applied live and pushed to connected clients.
            // A reload changes several entries at once, so only flag it here and apply once in Update().
            Config.SettingChanged += (_, args) =>
            {
                Log.LogInfo($"Setting changed: {args.ChangedSetting.Definition.Key} = {args.ChangedSetting.BoxedValue}");
                settingsDirty = true;
            };

            // BepInEx never re-reads the .cfg by itself, so Update() polls it for edits on disk.
            configSignature = ReadConfigSignature(Config.ConfigFilePath);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded, watching {Config.ConfigFilePath}");

            new Harmony(PluginGUID).PatchAll();
        }

        private ConfigEntry<float> BindMultiplier(string key, float value, string description) =>
            Config.Bind("Weights", key, value,
                new ConfigDescription(description, new AcceptableValueRange<float>(0f, 5f)));

        // ---- Live reload of the .cfg file ----
        // FileSystemWatcher is unreliable under Unity's Mono (Linux servers, Docker volumes, Wine),
        // so the file is simply polled. Reload runs on Unity's main thread, where weather and
        // network code must run.

        private const float PollIntervalSeconds = 0.5f;

        private float nextPoll;
        private bool settingsDirty;
        private string configSignature;
        private bool reloadPending;

        private static string ReadConfigSignature(string path)
        {
            FileInfo info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : "";
        }

        private void Update()
        {
            if (settingsDirty)
            {
                settingsDirty = false;
                NetSync.OnLocalConfigChanged();
            }

            float now = UnityEngine.Time.unscaledTime;
            if (now < nextPoll) return;
            nextPoll = now + PollIntervalSeconds;

            string signature = ReadConfigSignature(Config.ConfigFilePath);
            if (signature != configSignature)
            {
                // The file just changed (or is still being written): wait one more poll to let it settle.
                configSignature = signature;
                reloadPending = true;
            }
            else if (reloadPending)
            {
                reloadPending = false;
                Log.LogInfo("Config file changed, reloading.");
                try
                {
                    Config.Reload(); // raises SettingChanged for every value that actually changed
                }
                catch (System.Exception e)
                {
                    Log.LogError($"Config reload failed: {e}");
                }
            }
        }
    }

    /// <summary>The values actually used for weather. Local config, unless a server has overridden it.</summary>
    internal struct WeatherSettings
    {
        public bool Enabled;
        public float Rain, Storm, Mist;
        public bool StormAffectsOcean;

        public static WeatherSettings FromConfig() => new WeatherSettings
        {
            Enabled = CustomWeather.Enabled.Value,
            Rain = CustomWeather.RainMultiplier.Value,
            Storm = CustomWeather.StormMultiplier.Value,
            Mist = CustomWeather.MistMultiplier.Value,
            StormAffectsOcean = CustomWeather.StormAffectsOcean.Value,
        };

        // Wire format. Write and Read must keep the same field order.
        public void Write(ZPackage pkg)
        {
            pkg.Write(Enabled);
            pkg.Write(Rain);
            pkg.Write(Storm);
            pkg.Write(Mist);
            pkg.Write(StormAffectsOcean);
        }

        public static WeatherSettings Read(ZPackage pkg) => new WeatherSettings
        {
            Enabled = pkg.ReadBool(),
            Rain = pkg.ReadSingle(),
            Storm = pkg.ReadSingle(),
            Mist = pkg.ReadSingle(),
            StormAffectsOcean = pkg.ReadBool(),
        };
    }

    internal static class WeatherApplier
    {
        // Server-provided settings while connected to a CustomWeather server; null otherwise.
        internal static WeatherSettings? Synced;

        private static WeatherSettings Current => Synced ?? WeatherSettings.FromConfig();

        // Vanilla weights, so weights are recomputed from the original instead of compounding.
        private static readonly Dictionary<EnvEntry, float> VanillaWeights = new Dictionary<EnvEntry, float>();

        [HarmonyPatch(typeof(EnvMan), "InitializeBiomeEnvSetup")]
        private class Patch
        {
            private static void Postfix(BiomeEnvSetup biome) => ApplyBiome(biome, Current);
        }

        internal static void ApplyAll()
        {
            if (EnvMan.instance?.m_biomes == null) return;

            WeatherSettings settings = Current;
            string source = Synced.HasValue ? "server" : "local config";
            CustomWeather.Log.LogInfo($"Applying weather weights from {source}: enabled={settings.Enabled}, rain={settings.Rain}, storm={settings.Storm}, mist={settings.Mist}, stormAffectsOcean={settings.StormAffectsOcean}");
            foreach (BiomeEnvSetup biome in EnvMan.instance.m_biomes)
            {
                ApplyBiome(biome, settings);
            }

            LogWeights();
            RerollWeather();
        }

        // Shows the weights the game will actually roll with, per biome.
        private static void LogWeights()
        {
            foreach (BiomeEnvSetup biome in EnvMan.instance.m_biomes)
            {
                string weights = string.Join(", ", biome.m_environments.Select(e => $"{e.m_environment}={e.m_weight:0.##}"));
                CustomWeather.Log.LogInfo($"  {biome.m_name}: {weights}");
            }
        }

        // Valheim only picks weather when a new weather period starts, so new weights would not be
        // noticed until then. Rewinding the stored period makes the game roll again right away.
        private static void RerollWeather()
        {
            try
            {
                Traverse.Create(EnvMan.instance).Field("m_environmentPeriod").SetValue(-1L);
            }
            catch (System.Exception e)
            {
                CustomWeather.Log.LogWarning($"Could not re-roll current weather: {e.Message}");
            }
        }

        private static void ApplyBiome(BiomeEnvSetup biome, WeatherSettings settings)
        {
            foreach (EnvEntry env in biome.m_environments)
            {
                if (!VanillaWeights.TryGetValue(env, out float vanilla))
                {
                    vanilla = env.m_weight;
                    VanillaWeights[env] = vanilla;
                }

                env.m_weight = settings.Enabled
                    ? vanilla * Multiplier(env.m_environment, biome.m_name, settings)
                    : vanilla;
            }
        }

        private static float Multiplier(string environment, string biome, WeatherSettings s)
        {
            float multiplier = 1f;

            if (environment.Contains("Rain"))
                multiplier *= s.Rain;

            if (environment.Contains("Storm") && (s.StormAffectsOcean || biome != "Ocean"))
                multiplier *= s.Storm;

            if (environment == "Misty")
                multiplier *= s.Mist;

            return multiplier;
        }
    }

    /// <summary>
    /// Small Valheim RPC handshake (replaces Jotunn's compatibility check and config sync):
    ///  - both sides exchange their Major.Minor version right after connecting;
    ///  - a peer that has not sent a matching version before its PeerInfo is rejected;
    ///  - the server then sends its weather settings, which override the client's local config.
    /// </summary>
    internal static class NetSync
    {
        private const string RpcVersion = "CustomWeather_Version";
        private const string RpcConfig = "CustomWeather_Config";
        private const int ErrorVersion = 3; // ZNet.ConnectionStatus.ErrorVersion

        private static readonly System.Version LocalVersion = System.Version.Parse(CustomWeather.PluginVersion);
        private static readonly HashSet<ZRpc> Validated = new HashSet<ZRpc>();

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        private static bool Compatible(string theirs) =>
            System.Version.TryParse(theirs, out System.Version other)
            && LocalVersion.Major == other.Major
            && LocalVersion.Minor == other.Minor;

        private static ZNetPeer FindPeer(ZRpc rpc)
        {
            if (ZNet.instance == null) return null;

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.m_rpc == rpc) return peer;
            }
            return null;
        }

        /// <summary>Server tells the client it has a version error; a client simply drops the server.</summary>
        private static void Reject(ZRpc rpc)
        {
            if (IsServer)
            {
                rpc.Invoke("Error", ErrorVersion);
                return;
            }

            ZNetPeer peer = FindPeer(rpc);
            if (peer != null) ZNet.instance.Disconnect(peer);
        }

        // ---- Handshake ----

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private class OnNewConnectionPatch
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                peer.m_rpc.Register<string>(RpcVersion, RPC_Version);
                peer.m_rpc.Register<ZPackage>(RpcConfig, RPC_Config);

                // The client speaks first; the server answers once it has validated the client.
                if (!__instance.IsServer())
                {
                    peer.m_rpc.Invoke(RpcVersion, CustomWeather.PluginVersion);
                }
            }
        }

        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        private class PeerInfoPatch
        {
            // Runs on both sides: the server checks clients, a client checks the server.
            private static bool Prefix(ZRpc rpc)
            {
                if (Validated.Contains(rpc)) return true;

                Reject(rpc);
                return false;
            }
        }

        // Forget a peer as soon as it leaves, not only when the whole ZNet is destroyed.
        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        private class DisconnectPatch
        {
            private static void Prefix(ZNetPeer peer)
            {
                if (peer?.m_rpc != null) Validated.Remove(peer.m_rpc);
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private class OnDestroyPatch
        {
            private static void Postfix()
            {
                Validated.Clear();
                WeatherApplier.Synced = null; // fall back to local config next time
            }
        }

        private static void RPC_Version(ZRpc rpc, string theirVersion)
        {
            if (!Compatible(theirVersion))
            {
                Reject(rpc);
                return;
            }

            Validated.Add(rpc);

            if (IsServer)
            {
                rpc.Invoke(RpcVersion, CustomWeather.PluginVersion);
                rpc.Invoke(RpcConfig, Pack(WeatherSettings.FromConfig()));
            }
        }

        // ---- Config sync ----

        private static void RPC_Config(ZRpc rpc, ZPackage pkg)
        {
            // Only trust config from a validated server, never from another client.
            if (IsServer || !Validated.Contains(rpc)) return;

            WeatherApplier.Synced = WeatherSettings.Read(pkg);
            WeatherApplier.ApplyAll();
        }

        /// <summary>Called whenever the local .cfg changes (including live edits on the server).</summary>
        internal static void OnLocalConfigChanged()
        {
            WeatherApplier.ApplyAll();

            if (!IsServer) return;

            WeatherSettings settings = WeatherSettings.FromConfig();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.m_rpc != null && Validated.Contains(peer.m_rpc))
                {
                    // A fresh package per peer: a ZPackage's read position is consumed on send.
                    peer.m_rpc.Invoke(RpcConfig, Pack(settings));
                }
            }
        }

        private static ZPackage Pack(WeatherSettings settings)
        {
            ZPackage pkg = new ZPackage();
            settings.Write(pkg);
            return pkg;
        }
    }
}
