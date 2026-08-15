using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MultiplayerPlus
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
    public class MultiplayerPlusPlugin : BaseUnityPlugin
    {
        public static MultiplayerPlusPlugin Instance { get; private set; }

        public ConfigEntry<int> ListenPort;
        public ConfigEntry<string> ConnectAddress;
        public ConfigEntry<float> ChatScale;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;

            ListenPort = Config.Bind(
                "Network",
                "ListenPort",
                27000,
                "房主（创建联机）时监听的 UDP 端口，客户端按此端口连接。");

            ConnectAddress = Config.Bind(
                "Network",
                "ConnectAddress",
                "127.0.0.1:27000",
                "加入联机弹窗里预填的 IP:端口（可每次手动修改）。");

            ChatScale = Config.Bind(
                "Chat",
                "Scale",
                1.0f,
                "聊天框缩放倍数（1.0 = 默认大小，可调大看更多/更大字）。");

            _harmony = new Harmony(PluginInfo.GUID);
            _harmony.PatchAll();

            GameObject go = new GameObject("MultiplayerPlus");
            DontDestroyOnLoad(go);
            go.AddComponent<MultiplayerPlusUI>();

            Logger.LogInfo("MultiplayerPlus loaded (by Chengxidaxia)");
        }
    }

    internal static class PluginInfo
    {
        public const string GUID = "com.chengxidaxia.multiplayerplus";
        public const string Name = "MultiplayerPlus";
        public const string Version = "1.0.0";
    }
}
