using System;
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
        public ConfigEntry<int> MaxPlayersEntry;

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

            MaxPlayersEntry = Config.Bind(
                "Network",
                "MaxPlayers",
                2,
                "联机人数上限（房主创建联机时生效，范围 2~8）。");

            PlayerIdManager.MaxPlayers = MaxPlayersEntry.Value;

            _harmony = new Harmony(PluginInfo.GUID);
            _harmony.PatchAll();

            // 调大游戏自带的“对等超时”（Peer.DEFAULT_TIMEOUT，默认 8 秒）：
            // 房主加载世界/光影包编译等卡顿时主线程泵消息变慢，8 秒内收不到消息/心跳
            // 就会被游戏自己的看门狗以 Timeout_Messages / Timeout_StayAlive 误踢。
            // 调成 30 秒，加载卡顿和瞬时丢包都能扛过去，不影响正常断线检测。
            try
            {
                System.Reflection.FieldInfo timeoutField = AccessTools.Field(typeof(Funlabs.Peer), "DEFAULT_TIMEOUT");
                if (timeoutField != null)
                {
                    timeoutField.SetValue(null, 30f);
                    Logger.LogInfo("Peer.DEFAULT_TIMEOUT bumped to 30s (was 8s)");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("bump peer timeout failed: " + e.Message);
            }

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
