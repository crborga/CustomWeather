using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace CustomWeather
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    internal class CustomWeather : BaseUnityPlugin
    {
        public const string PluginGUID = "local.CustomWeather";
        public const string PluginName = "Custom Weather";
        public const string PluginVersion = "1.0.0";

        public static bool isModded = true;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> RainMultiplier;
        internal static ConfigEntry<float> StormMultiplier;
        internal static ConfigEntry<float> MistMultiplier;
        internal static ConfigEntry<bool> StormAffectsOcean;

        private void Awake()
        {
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
            Config.SettingChanged += (_, __) => NetSync.OnLocalConfigChanged();

            new Harmony(PluginGUID).PatchAll();
        }

        private ConfigEntry<float> BindMultiplier(string key, float value, string description)
        {
            return Config.Bind("Weights", key, value,
                new ConfigDescription(description, new AcceptableValueRange<float>(0f, 5f)));
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
    }

    internal static class WeatherApplier
    {
        // Server-provided settings while connected to a CustomWeather server; null otherwise.
        internal static WeatherSettings? Synced;

        private static WeatherSettings Current => Synced ?? WeatherSettings.FromConfig();

        // Vanilla weights, so weights can be recomputed instead of compounding on every apply.
        private static readonly Dictionary<EnvEntry, float> Original = new Dictionary<EnvEntry, float>();

        [HarmonyPatch(typeof(EnvMan), "InitializeBiomeEnvSetup")]
        private class Patch
        {
            private static void Postfix(BiomeEnvSetup biome) => ApplyBiome(biome, Current);
        }

        internal static void ApplyAll()
        {
            if (EnvMan.instance == null || EnvMan.instance.m_biomes == null) return;
            WeatherSettings s = Current;
            foreach (BiomeEnvSetup biome in EnvMan.instance.m_biomes)
            {
                ApplyBiome(biome, s);
            }
        }

        private static void ApplyBiome(BiomeEnvSetup biome, WeatherSettings s)
        {
            foreach (EnvEntry env in biome.m_environments)
            {
                if (!Original.TryGetValue(env, out float weight))
                {
                    weight = env.m_weight;
                    Original[env] = weight;
                }

                if (s.Enabled)
                {
                    if (env.m_environment.Contains("Rain"))
                    {
                        weight *= s.Rain;
                    }
                    if (env.m_environment.Contains("Storm") && (s.StormAffectsOcean || biome.m_name != "Ocean"))
                    {
                        weight *= s.Storm;
                    }
                    if (env.m_environment == "Misty")
                    {
                        weight *= s.Mist;
                    }
                }

                env.m_weight = weight;
            }
        }
    }

    /// <summary>
    /// Replaces Jotunn's compatibility check and config sync with a small Valheim RPC handshake:
    ///  - both sides exchange their Major.Minor version right after connecting;
    ///  - a peer that has not sent a matching version before its PeerInfo is rejected;
    ///  - the server then sends its weather settings, which override the client's local config.
    /// </summary>
    internal static class NetSync
    {
        private const string RpcVersion = "CustomWeather_Version";
        private const string RpcConfig = "CustomWeather_Config";
        private const int ErrorVersion = 3; // ZNet.ConnectionStatus.ErrorVersion

        private static readonly HashSet<ZRpc> Validated = new HashSet<ZRpc>();

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        private static bool Compatible(string theirs)
        {
            if (!System.Version.TryParse(theirs, out System.Version other)) return false;
            System.Version mine = System.Version.Parse(CustomWeather.PluginVersion);
            return mine.Major == other.Major && mine.Minor == other.Minor;
        }

        private static ZNetPeer FindPeer(ZRpc rpc)
        {
            if (ZNet.instance == null) return null;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.m_rpc == rpc) return peer;
            }
            return null;
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
            private static bool Prefix(ZNet __instance, ZRpc rpc)
            {
                if (Validated.Contains(rpc)) return true;

                if (__instance.IsServer())
                {
                    rpc.Invoke("Error", ErrorVersion);
                }
                else
                {
                    ZNetPeer peer = FindPeer(rpc);
                    if (peer != null) __instance.Disconnect(peer);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private class OnDestroyPatch
        {
            private static void Postfix()
            {
                Validated.Clear();
                if (WeatherApplier.Synced != null)
                {
                    WeatherApplier.Synced = null; // fall back to local config next time
                }
            }
        }

        private static void RPC_Version(ZRpc rpc, string theirVersion)
        {
            if (!Compatible(theirVersion))
            {
                if (IsServer)
                {
                    rpc.Invoke("Error", ErrorVersion);
                }
                else
                {
                    ZNetPeer peer = FindPeer(rpc);
                    if (peer != null) ZNet.instance.Disconnect(peer);
                }
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

            WeatherApplier.Synced = Unpack(pkg);
            WeatherApplier.ApplyAll();
        }

        /// <summary>Called whenever the local .cfg changes (including live edits on the server).</summary>
        internal static void OnLocalConfigChanged()
        {
            WeatherApplier.ApplyAll();

            if (!IsServer) return;

            ZPackage pkg = Pack(WeatherSettings.FromConfig());
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.m_rpc != null && Validated.Contains(peer.m_rpc))
                {
                    peer.m_rpc.Invoke(RpcConfig, new ZPackage(pkg.GetArray()));
                }
            }
        }

        private static ZPackage Pack(WeatherSettings s)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(s.Enabled);
            pkg.Write(s.Rain);
            pkg.Write(s.Storm);
            pkg.Write(s.Mist);
            pkg.Write(s.StormAffectsOcean);
            return pkg;
        }

        private static WeatherSettings Unpack(ZPackage pkg)
        {
            return new WeatherSettings
            {
                Enabled = pkg.ReadBool(),
                Rain = pkg.ReadSingle(),
                Storm = pkg.ReadSingle(),
                Mist = pkg.ReadSingle(),
                StormAffectsOcean = pkg.ReadBool(),
            };
        }
    }
}
