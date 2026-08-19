using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Beam;
using Beam.UI;
using Funlabs;
using HarmonyLib;
using Photon.Bolt;
using Photon.Bolt.Matchmaking;
using UdpKit;
using UdpKit.Platform;
using UnityEngine;
using UnityEngine.Rendering;

namespace MultiplayerPlus
{
    /// <summary>
    /// 所有 Harmony 补丁。通过 Main 里的 PatchAll 自动应用。
    /// </summary>
    public static class Patches
    {
    }

    // 0. 防御性修复：MultiplayerMessageManager.Register() 遍历所有已加载程序集注册联机消息类时，
    //    若某个程序集加载失败（如客户端缺 BepInExConfigManager.Mono.Patcher.dll），会抛 TypeLoadException，
    //    导致整条联机消息系统 0 注册、联机完全不可用（STUN 反复 Reset、客户端连不上）。
    //    这里用安全版替代：逐程序集 try-catch，跳过加载失败的程序集。
    [HarmonyPatch(typeof(MultiplayerMessageManager), "Register")]
    internal static class Patch_MessageRegister
    {
        private static bool Prefix()
        {
            try
            {
                Dictionary<Type, int> ids = AccessTools.StaticFieldRefAccess<Dictionary<Type, int>>(typeof(MultiplayerMessageManager), "_messageIds");
                Dictionary<int, ConstructorInfo> ctors = AccessTools.StaticFieldRefAccess<Dictionary<int, ConstructorInfo>>(typeof(MultiplayerMessageManager), "_messageConstructors");
                if (ids.Count != 0)
                {
                    return false; // 已注册，等价于原方法的提前返回
                }
                int num = 0;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        continue; // 关键：跳过加载失败的程序集，不再让整条注册崩溃
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    IEnumerable<Type> msgs = types
                        .Where(x => typeof(IMultiplayerMessage).IsAssignableFrom(x) && !x.IsAbstract)
                        .OrderBy(x => x.ToString());
                    foreach (Type t in msgs)
                    {
                        ids[t] = num;
                        ctors[num] = t.GetConstructor(Type.EmptyTypes);
                        num++;
                    }
                }
                if (num >= MultiplayerTokens.MessageEnvelope.MAX_MESSAGES_TYPES)
                {
                    throw new Exception(string.Format("Too many replicating messages class. Only {0} are supported", MultiplayerTokens.MessageEnvelope.MAX_MESSAGES_TYPES));
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[MultiplayerPlus] safe message register failed: " + e);
            }
            return false;
        }
    }

    // 1. 平台替换：把 Photon Cloud 平台换成本地 UDP 平台（DotNetPlatform）
    [HarmonyPatch(typeof(BoltLauncher), "SetUdpPlatform")]
    internal static class Patch_SetUdpPlatform
    {
        private static void Prefix(ref UdpPlatform platform)
        {
            if (platform != null && platform.GetType().Name == "PhotonPlatform")
            {
                UdpPlatform dotNet = DirectConnect.GetDotNetPlatform();
                if (dotNet != null)
                {
                    platform = dotNet;
                }
            }
        }
    }

    // 2. 房主固定监听端口（原版用随机端口）+ 调大 Bolt 底层连接超时
    [HarmonyPatch(typeof(BoltLauncher), "StartServer", new Type[] { typeof(BoltConfig), typeof(string) })]
    internal static class Patch_StartServer
    {
        private static bool Prefix(BoltConfig config, string scene)
        {
            BoltTimeoutUtil.BumpBoltTimeouts(config);
            ushort port = (ushort)MultiplayerPlusPlugin.Instance.ListenPort.Value;
            BoltLauncher.StartServer(new UdpEndPoint(UdpIPv4Address.Any, port), config, scene);
            return false;
        }
    }

    // 2b. 客户端启动 Bolt 时同样调大底层连接超时（否则客户端可能比房主更早超时）
    [HarmonyPatch(typeof(BoltLauncher), "StartClient", new Type[] { typeof(BoltConfig) })]
    internal static class Patch_StartClientTimeout
    {
        private static void Prefix(BoltConfig config)
        {
            BoltTimeoutUtil.BumpBoltTimeouts(config);
        }
    }

    /// <summary>把 BoltConfig 的底层连接/心跳超时调大，防加载卡顿/瞬时丢包误判断线。</summary>
    internal static class BoltTimeoutUtil
    {
        internal static void BumpBoltTimeouts(BoltConfig config)
        {
            if (config == null)
            {
                return;
            }
            try
            {
                SetBoltConfigValue(config, "connectionTimeout", 30000);   // 等待消息超时 30s
                SetBoltConfigValue(config, "stayAliveTimeout", 45000);    // 心跳超时 45s
                Debug.Log("[MultiplayerPlus] Bolt timeouts bumped: connection=30s stayAlive=45s");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MultiplayerPlus] bolt timeout bump failed: " + e.Message);
            }
        }

        private static void SetBoltConfigValue(BoltConfig config, string name, int value)
        {
            Traverse field = Traverse.Create(config).Field(name);
            if (field.FieldExists())
            {
                field.SetValue(value);
                return;
            }
            Traverse prop = Traverse.Create(config).Property(name);
            if (prop.PropertyExists())
            {
                prop.SetValue(value);
            }
        }
    }

    // 2c. 游戏在加载区域/主菜单时会把超时重置为 Peer.GetSessionTimeout()
    //     （读 Bolt 配置的 connectionTimeout/1000，可能只有 10 秒），
    //     这里统一返回 30 秒，避免被重置回短超时。
    [HarmonyPatch(typeof(Peer), "GetSessionTimeout")]
    internal static class Patch_GetSessionTimeout
    {
        private static bool Prefix(ref float __result)
        {
            __result = 30f;
            return false;
        }
    }

    // 3. 禁用"连不上服务器就进不去"：跳过 HTTP 连通性检查，直接视为可用
    [HarmonyPatch(typeof(MainMenuPresenter), "CheckOnlineAvailable")]
    internal static class Patch_CheckOnlineAvailable
    {
        private static bool Prefix(Action onAvailable, Action onNotAvailableDialogueDismissed, Action onNotAvailable)
        {
            if (onAvailable != null)
            {
                onAvailable();
            }
            return false;
        }
    }

    // 4. 房主跳过 BoltMatchmaking.CreateSession（不连 Photon Cloud 注册房间）
    [HarmonyPatch(typeof(MultiplayerMng), "ConnectToSession")]
    internal static class Patch_ConnectToSession
    {
        private static bool Prefix(string sessionId, MultiplayerTokens.Session token, Action onSuccess, Action<string> onError)
        {
            if (!Game.Mode.IsMultiplayer())
            {
                if (onSuccess != null)
                {
                    onSuccess();
                }
                return false;
            }

            // 房主：已通过 StartServer 监听，直接视为会话创建成功。
            // 原版在 SessionCreatedOrUpdated 里会创建本地玩家，这里手动补上。
            if (Game.Mode.IsServer())
            {
                PlayerRegistry.CreateLocalPeers();
                LocalSessionToken.Store(token);
                MainMenuPresenter menu = UnityEngine.Object.FindObjectOfType<MainMenuPresenter>();
                Debug.Log("[MultiplayerPlus] ConnectToSession server: ContinuingMultiplayerGame=" + (menu != null ? menu.ContinuingMultiplayerGame.ToString() : "null"));
                // Peer.Attached（设置 ServerPeer）是异步的，需等它就绪后再回调，
                // 否则 OnJoin -> ShowNewGameMenuPresenter 访问 ServerPeer 会 NRE。
                MultiplayerPlusUI.WaitForServerPeerAndCall(delegate
                {
                    if (onSuccess != null)
                    {
                        onSuccess();
                    }
                });
                return false;
            }

            // 客户端：走 IP 直连，不经过这里
            if (onSuccess != null)
            {
                onSuccess();
            }
            return false;
        }
    }

    // 5. 服务器 accept：绕过 BoltMatchmaking.CurrentSession（直连模式下为 null），
    //    并为每个客户端分配递增的玩家 id（解锁人数上限的地基）。
    [HarmonyPatch(typeof(MultiplayerMng), "ConnectRequest")]
    internal static class Patch_ConnectRequest
    {
        private static bool Prefix(UdpEndPoint endpoint, IProtocolToken joinToken)
        {
            if (Game.Mode.IsServer())
            {
                // 读客户端设备标识（Join.Id），作为分配 id 的关联键
                string clientKey = null;
                MultiplayerTokens.Join join = joinToken as MultiplayerTokens.Join;
                if (join != null)
                {
                    clientKey = join.Id;
                }
                PlayerIdManager.AllocateClientId(clientKey);
                BoltNetwork.Accept(endpoint, new MultiplayerTokens.Accept { ServerGameState = Game.State });
            }
            return false;
        }
    }

    // 5b. 扩展 Accept token：房主写入分配的客户端 id（在 ServerGameState 之后追加一个字节）
    [HarmonyPatch(typeof(MultiplayerTokens.Accept), "Write")]
    internal static class Patch_AcceptWrite
    {
        private static void Postfix(UdpPacket packet)
        {
            packet.WriteByte((byte)PlayerIdManager.PendingAcceptId);
        }
    }

    // 5c. 扩展 Accept token：客户端读出房主分配的 id
    [HarmonyPatch(typeof(MultiplayerTokens.Accept), "Read")]
    internal static class Patch_AcceptRead
    {
        private static void Postfix(UdpPacket packet)
        {
            PlayerIdManager.MyPlayerId = packet.ReadByte();
        }
    }

    // 5d. 客户端本地玩家 id 改为房主分配的 id（原版硬编码返回 1）
    [HarmonyPatch(typeof(MultiplayerMng), "GetLocalPeerId")]
    internal static class Patch_GetLocalPeerId
    {
        private static bool Prefix(ref int __result)
        {
            __result = Game.Mode.IsServer() ? 0 : PlayerIdManager.MyPlayerId;
            return false;
        }
    }

    // 5e. 实体 id 分配：客户端自己的实体用分配的 id，房主端用连接映射区分各客户端实体
    [HarmonyPatch(typeof(MultiplayerMng), "GetEntityId")]
    internal static class Patch_GetEntityId
    {
        private static bool Prefix(BoltEntity entity, ref int __result)
        {
            bool isOwner = entity != null && entity.IsOwner;
            if (BoltNetwork.IsServer)
            {
                __result = isOwner ? 0 : PlayerIdManager.GetClientId(entity);
            }
            else
            {
                __result = isOwner ? PlayerIdManager.MyPlayerId : 0;
            }
            return false;
        }
    }

    // 5f. 联机玩家人数返回配置的上限（原版硬编码 2），本地分屏保留原逻辑
    [HarmonyPatch(typeof(MPExtensions), "GetPlayersCount")]
    internal static class Patch_GetPlayersCount
    {
        private static bool Prefix(GameMode mode, ref int __result)
        {
            if (mode == GameMode.Coop_Server || mode == GameMode.Coop_Client)
            {
                __result = PlayerIdManager.MaxPlayers;
                return false;
            }
            return true;
        }
    }

    // 6. 客户端直连成功后的进入游戏流程（原版走 SessionConnected，直连走 Connected）
    [HarmonyPatch(typeof(MultiplayerMng), "Connected")]
    internal static class Patch_Connected
    {
        private static void Postfix(BoltConnection connection)
        {
            if (BoltNetwork.IsServer)
            {
                // 房主端：把连接绑定到已分配的玩家 id（供 GetEntityId 区分多客户端实体）
                PlayerIdManager.BindConnection(connection);
                return;
            }

            // 原版在 SessionConnected 里会创建本地玩家，这里补上
            PlayerRegistry.CreateLocalPeers();

            MultiplayerPlusUI.HideIPDialog();
            MultiplayerPlusUI.LogSystem("已连接到服务器");

            // 客户端把加入前选择的性别发给房主，房主据此渲染客户端模型（设置 ServerPeer.Gender2）
            try
            {
                new GenderSelectMessage { Gender = MultiplayerPlusUI.SelectedGender }.Post();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MultiplayerPlus] send gender failed: " + e.Message);
            }

            MainMenuPresenter menu = UnityEngine.Object.FindObjectOfType<MainMenuPresenter>();
            if (menu == null)
            {
                return;
            }

            if (MultiplayerMng.ServerGameState.IsGame())
            {
                menu.StartGame(GameMode.Coop_Client, GameState.LOAD_GAME, 3);
            }
            else
            {
                // 延迟等 ServerPeer.Continuing 从房主同步，避免"继续游戏"误判成"新游戏"
                MultiplayerPlusUI.WaitAndCall(delegate
                {
                    try
                    {
                        if (PlayerRegistry.ServerPeer != null && PlayerRegistry.ServerPeer.Continuing == 1)
                        {
                            menu.ContinuingMultiplayerGame = true;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[MultiplayerPlus] sync continuing failed: " + e.Message);
                    }
                    menu.ShowNewGameMenuPresenter();
                }, 0.3f);
            }
        }
    }

    // 7. 存档复用：在线联机（房主/客户端）读写单人存档，不再区分单人/多人
    [HarmonyPatch(typeof(FilePath), "GetSaveFileSuffix")]
    internal static class Patch_GetSaveFileSuffix
    {
        private static bool Prefix(GameMode mode, ref string __result)
        {
            if (mode == GameMode.Coop_Server || mode == GameMode.Coop_Client)
            {
                __result = string.Empty;
                return false;
            }
            return true;
        }
    }

    // 8. 加入联机：弹出 IP 输入框，取代原版 Photon 大厅
    [HarmonyPatch(typeof(MainMenuPresenter), "PlayOnlineJoinButton_Click")]
    internal static class Patch_JoinButton
    {
        private static bool Prefix()
        {
            MultiplayerPlusUI.ShowIPDialog();
            return false;
        }
    }

    // 9. 多人开启后，在聊天栏广播端口与协议
    [HarmonyPatch(typeof(MultiplayerMng), "BoltStartDone")]
    internal static class Patch_BoltStartDone
    {
        private static void Postfix()
        {
            // 只在联机模式（房主/客户端）下发系统消息，单人/本地分屏不弹
            if (!Game.Mode.IsMultiplayer())
            {
                return;
            }

            if (BoltNetwork.IsServer)
            {
                ushort port = (ushort)MultiplayerPlusPlugin.Instance.ListenPort.Value;
                MultiplayerPlusUI.LogSystem("联机已开启 | 端口: " + port + " | 协议: UDP");
            }
            else if (BoltNetwork.IsClient)
            {
                MultiplayerPlusUI.LogSystem("客户端已就绪，等待连接...");
            }
        }
    }

    // 10. 把原版"服务器位置（region）"选择改造成"人数上限"选择（复用 RegionOptionButton 的位置）
    [HarmonyPatch(typeof(CreateSessionMenuPresenter), "PopulateControls")]
    internal static class Patch_PlayerCountOption
    {
        private static void Postfix(CreateSessionMenuPresenter __instance)
        {
            try
            {
                CreateSessionMenuViewAdapterBase view = Traverse.Create(__instance).Field("_view").GetValue<CreateSessionMenuViewAdapterBase>();
                if (view == null || view.RegionOptionButton == null)
                {
                    return;
                }

                // 改标题为"人数上限"（原版是"服务器位置"）。
                // 标题 label 上挂有 Localizer 组件，会在 TextChanged 时把文本覆盖回本地化键的翻译（"服务器所在地"），
                // 必须先禁用它，否则标题改不动。
                TMPOptionButtonViewAdapter btn = view.RegionOptionButton as TMPOptionButtonViewAdapter;
                if (btn != null)
                {
                    TMPLabelViewAdapter label = Traverse.Create(btn).Field("_label").GetValue<TMPLabelViewAdapter>();
                    if (label != null)
                    {
                        Beam.Language.Localizer localizer = label.GetComponent<Beam.Language.Localizer>();
                        if (localizer != null)
                        {
                            localizer.enabled = false;
                        }
                    }
                    btn.Text = "人数上限";
                }

                // 重新初始化为人数选项（2~8 人）
                System.Collections.Generic.List<DescriptionAttribute> items = new System.Collections.Generic.List<DescriptionAttribute>();
                for (int i = 2; i <= 8; i++)
                {
                    items.Add(new DescriptionAttribute(i.ToString() + " 人", "联机人数上限: " + i));
                }
                view.RegionOptionButton.Initialize(items);
                view.RegionOptionButton.SelectedIndex = Mathf.Clamp(PlayerIdManager.MaxPlayers - 2, 0, items.Count - 1);
                view.RegionOptionButton.SelectionChanged += OnPlayerCountChanged;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MultiplayerPlus] player count option failed: " + e.Message);
            }
        }

        private static void OnPlayerCountChanged(IOptionButtonViewAdapter sender)
        {
            int count = sender.SelectedIndex + 2;
            PlayerIdManager.MaxPlayers = count;
            try
            {
                MultiplayerPlusPlugin.Instance.MaxPlayersEntry.Value = count; // 写回配置
            }
            catch (Exception)
            {
                // ignore
            }
            Debug.Log("[MultiplayerPlus] MaxPlayers set to " + count);
        }
    }

    // 10b. 每次显示建房菜单时，恢复人数选中值（原版 Show 会把 RegionOptionButton 重置回 region 索引）
    [HarmonyPatch(typeof(CreateSessionMenuPresenter), "Show")]
    internal static class Patch_PlayerCountShow
    {
        private static void Postfix(CreateSessionMenuPresenter __instance)
        {
            try
            {
                CreateSessionMenuViewAdapterBase view = Traverse.Create(__instance).Field("_view").GetValue<CreateSessionMenuViewAdapterBase>();
                if (view != null && view.RegionOptionButton != null && view.RegionOptionButton.Elements != null)
                {
                    int idx = Mathf.Clamp(PlayerIdManager.MaxPlayers - 2, 0, view.RegionOptionButton.Elements.Count - 1);
                    view.RegionOptionButton.SelectedIndex = idx;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MultiplayerPlus] player count show failed: " + e.Message);
            }
        }
    }

    // 11. 直连模式下 BoltMatchmaking.CurrentSession 无效，GetSessionToken() 会返回 null，
    //     导致 NewGameMenuPresenter.Show() 访问 token.Name 时 NRE。这里返回本地会话令牌。
    [HarmonyPatch(typeof(MultiplayerMng), "GetSessionToken")]
    internal static class Patch_GetSessionToken
    {
        private static bool Prefix(ref MultiplayerTokens.Session __result)
        {
            if (Game.Mode.IsMultiplayer())
            {
                __result = LocalSessionToken.Get();
                return false;
            }
            return true;
        }
    }

    // 12. 诊断：房主"继续游戏"时 _continueMPGame 的实际值
    [HarmonyPatch(typeof(NewGameMenuPresenter), "Show")]
    internal static class Patch_NewGameMenuShow
    {
        private static void Postfix(NewGameMenuPresenter __instance)
        {
            try
            {
                bool continueMP = Traverse.Create(__instance).Field("_continueMPGame").GetValue<bool>();
                MainMenuPresenter menu = UnityEngine.Object.FindObjectOfType<MainMenuPresenter>();
                Debug.Log("[MultiplayerPlus] NewGameMenuPresenter.Show: _continueMPGame=" + continueMP + ", ContinuingMultiplayerGame=" + (menu != null ? menu.ContinuingMultiplayerGame.ToString() : "null"));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MultiplayerPlus] NewGameMenuPresenter.Show diag failed: " + e.Message);
            }
        }
    }

    // 13. 诊断：点"开始"按钮时 ActualStart 走哪个分支（继续游戏 LOAD_GAME vs 新游戏 INTRO）
    [HarmonyPatch(typeof(NewGameMenuPresenter), "ActualStart")]
    internal static class Patch_ActualStartDiag
    {
        private static void Prefix(NewGameMenuPresenter __instance)
        {
            try
            {
                bool continueMP = Traverse.Create(__instance).Field("_continueMPGame").GetValue<bool>();
                Debug.Log("[MultiplayerPlus] ActualStart: _continueMPGame=" + continueMP + ", Game.Mode=" + Game.Mode);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MultiplayerPlus] ActualStart diag failed: " + e.Message);
            }
        }
    }

    // 14a. 记录房主的联机意图（继续游戏 vs 新游戏）。
    //     原版靠 MainMenuPresenter.ContinuingMultiplayerGame 字段区分，但该字段在「新游戏」路径下
    //     可能因状态残留/异步时序被误判为 true，导致「新游戏」也被当成「继续游戏」跳过建房页面。
    //     这里在按钮点击时用独立静态标志显式记录，绕开字段污染。
    internal static class HostIntent
    {
        public static bool IsContinuing;
    }

    // 14b. 房主点「继续游戏」→ 记录为继续
    [HarmonyPatch(typeof(MainMenuPresenter), "PlayOnlineContinueButton_Click")]
    internal static class Patch_ContinueButton
    {
        private static void Prefix()
        {
            HostIntent.IsContinuing = true;
        }
    }

    // 14c. 房主点「新游戏」→ 记录为新游戏
    [HarmonyPatch(typeof(MainMenuPresenter), "PlayOnlineNewGameButton_Click")]
    internal static class Patch_NewGameButton
    {
        private static void Prefix()
        {
            HostIntent.IsContinuing = false;
        }
    }

    // 14. 房主"继续游戏"跳过建房页面（NewGameMenu），直接加载存档进入游戏。
    //     服务器已在监听，客户端随时可加入；客户端加入后直接进游戏（Patch_Connected 读 ServerGameState）。
    //     这样同时消除了「点开始弹"你确定要单人游戏"警告」——该警告来自 NewGameMenuPresenter.StartGame
    //     里 !PlayerUtilities.AllPeersPresent() 的检查，跳过建房页面后不再走这条路径。
    [HarmonyPatch(typeof(MainMenuPresenter), "ShowNewGameMenuPresenter")]
    internal static class Patch_SkipNewGameMenu
    {
        private static bool Prefix(MainMenuPresenter __instance)
        {
            // 只在房主端用 HostIntent 判断；客户端端保持原逻辑（依赖 ServerPeer.Continuing 同步）
            if (Game.Mode.IsServer())
            {
                bool isContinuing = HostIntent.IsContinuing;
                // 同步字段，确保 NewGameMenuPresenter.Show() 读到的 _continueMPGame 正确
                __instance.ContinuingMultiplayerGame = isContinuing;

                if (isContinuing)
                {
                    // 修复"人物默认女版"：原版由建房页面的 UpdateServerPeerOptions 把存档设置（含性别）
                    // 同步到 ServerPeer。跳过建房页面后这条路径断了，ServerPeer 停留在 Peer.Attached 时的
                    // 默认值（Gender2 默认 Female）。这里手动补上，保证房主性别 = 存档性别。
                    if (PlayerRegistry.ServerPeer != null)
                    {
                        PlayerRegistry.ServerPeer.Gender = Options.CustomSettings.Gender;
                        PlayerRegistry.ServerPeer.Gender2 = Options.CustomSettings.Gender2;
                        PlayerRegistry.ServerPeer.GameDifficulty = Options.CustomSettings.Difficulty;
                        PlayerRegistry.ServerPeer.World = Options.CustomSettings.World;
                        PlayerRegistry.ServerPeer.Permadeath = Options.CustomSettings.Permadeath;
                        PlayerRegistry.ServerPeer.Wildlife = Options.CustomSettings.Wildlife;
                        PlayerRegistry.ServerPeer.StartingCrate = Options.CustomSettings.StartingCrate;
                    }
                    __instance.ReplicatedStartGame(Game.Mode, GameState.LOAD_GAME, 3);
                    return false;
                }
            }
            // 新游戏（需要世界配置）或客户端：保留原逻辑
            return true;
        }
    }

    /// <summary>直连模式下替代 Photon matchmaking 会话的本地会话令牌。</summary>
    internal static class LocalSessionToken
    {
        private static MultiplayerTokens.Session _token;

        public static void Store(MultiplayerTokens.Session token)
        {
            if (token != null)
            {
                _token = token;
            }
        }

        public static MultiplayerTokens.Session Get()
        {
            if (_token == null)
            {
                _token = new MultiplayerTokens.Session
                {
                    SessionId = Game.MultiplayerSessionName ?? "local-session",
                    Name = "局域联机",
                    IsPrivate = true,
                    IsBusy = false,
                    IsOccupied = false
                };
            }
            return _token;
        }
    }

    // 15. 别人（非房主）退出不结束游戏。
    //     原版 Peer_Extensions.Disconnect 只对 id==1（第 2 人）温和断线，id!=1 一律
    //     MultiplayerMng.Shutdown() 关闭整个联机会话。MP 扩展后第 3+ 个客户端 id>=2，
    //     退出时会把房主端整个会话关掉。这里把 id>=2 的客户端退出改为：房主端只断开
    //     该客户端连接，客户端端仅 Unlink（不断开与服务器的连接）；房主（id==0）退出
    //     仍走原逻辑结束游戏。
    [HarmonyPatch(typeof(Peer_Extensions), "Disconnect")]
    internal static class Patch_PeerDisconnect_KeepSession
    {
        private static bool Prefix(Peer peer, DisconnectDetail disconnectDetail)
        {
            if (peer == null)
            {
                return true;
            }
            int id = peer.Id;
            if (id == 0 || id == 1)
            {
                return true; // 房主退出 / 原版第 2 人：保留原逻辑
            }
            try
            {
                MultiplayerMng.LogError(string.Format("Disconnect peer {0} {1}", id, disconnectDetail), null);
                MultiplayerMng.DisconnectPayload.Set(disconnectDetail);
                PlayerRegistry.Unlink(peer);
                BoltEntity entity = peer.entity;
                if (BoltNetwork.IsServer && entity != null && entity.Source != null)
                {
                    entity.Source.Disconnect(); // 房主端：只断开该客户端的连接
                }
                // 客户端端：实体销毁由 Bolt 处理，这里不断开与服务器的连接
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MultiplayerPlus] peer disconnect keep-session failed: " + e.Message);
            }
            return false;
        }
    }

    // 15b. 直连模式没有 matchmaking 会话，客户端退出时房主端 UpdateToken() 会因
    //      BoltMatchmaking.CurrentSession 为 null / GetToken() 为 null 抛 NRE（日志刷红字）。
    //      直连联机根本不更新 Photon 房间令牌，联机模式下永远跳过；单人模式保留原逻辑。
    [HarmonyPatch(typeof(MultiplayerMng), "UpdateToken")]
    internal static class Patch_UpdateTokenSafe
    {
        private static bool Prefix()
        {
            return !Game.Mode.IsMultiplayer();
        }
    }

    // 16. 修复"远程玩家头不可见"：
    //     头显隐完全由 Character.PlayerCamera_RenderingCameraChanged(ourCamera) 控制。
    //     其 ourCamera=false 分支（"别人看你"）会：隐藏 FirstPerson、显示 ThirdPerson 并启用头部。
    //     但远程玩家相机在 PlayerCamera.Attached() 里 SetActive(false)，Cameras_OnPreCull 不会
    //     为它触发 → 该方法在 MP 下对远程角色从未被调用 → 头停在 prefab 默认（隐藏）。
    //     v3 仅在 SetPlayer 时调用一次，但远程角色渲染器刚 Instantiate 出来未稳定，
    //     后续初始化/渲染器状态重置可能再次关掉头部 → 单次调用不可靠。
    //     v4 方案（自愈）：
    //     (a) Prefix 强制非 owner 角色走 ourCamera=false 分支（显示 TP+头、隐藏 FP），
    //         这样即使原相机事件将来触发也是正确状态；
    //     (b) SetPlayer Postfix 立即应用一次 + 输出头部渲染器状态诊断日志；
    //     (c) CharacterRenderer.Update Postfix 看门狗：对远程 TP 渲染器每帧
    //         强制应用"别人看你"状态，任何时机被重新隐藏都能自愈。走游戏自带方法，
    //         绝不会造成 v1 那种贴图混乱（v1 是把 FP+TP 全部头渲染器每帧强开）。
    internal static class RemoteHeadUtil
    {
        internal static readonly AccessTools.FieldRef<Character, IPlayer> PlayerRef =
            AccessTools.FieldRefAccess<Character, IPlayer>("_player");

        internal static readonly AccessTools.FieldRef<CharacterRenderer, IPlayer> RendererPlayerRef =
            AccessTools.FieldRefAccess<CharacterRenderer, IPlayer>("_player");

        internal static readonly MethodInfo RenderMethod =
            AccessTools.Method(typeof(Character), "PlayerCamera_RenderingCameraChanged", new[] { typeof(bool) });

        /// <summary>强制 Character 处于"别人看你"状态：隐藏 FP、显示 TP 并启用头部。</summary>
        internal static void ApplyRemoteHead(Character character, string tag)
        {
            if (character == null || RenderMethod == null)
            {
                return;
            }
            try
            {
                RenderMethod.Invoke(character, new object[] { false });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MP-Head] apply(" + tag + ") failed: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(Character), "PlayerCamera_RenderingCameraChanged")]
    internal static class Patch_RenderingCameraChanged_RemoteHead
    {
        private static void Prefix(Character __instance, ref bool ourCamera)
        {
            try
            {
                IPlayer player = RemoteHeadUtil.PlayerRef(__instance);
                if (player != null && !player.IsOwner)
                {
                    // 远程玩家始终按"别人看你"的第三人称+头部逻辑渲染
                    ourCamera = false;
                }
            }
            catch (Exception)
            {
                // 静默
            }
        }
    }

    [HarmonyPatch(typeof(Character), "SetPlayer")]
    internal static class Patch_CharacterSetPlayer_RemoteHead
    {
        private static void Postfix(Character __instance)
        {
            try
            {
                IPlayer player = RemoteHeadUtil.PlayerRef(__instance);
                if (player == null || player.IsOwner)
                {
                    return; // 只处理远程玩家；本地玩家由原始相机事件处理
                }
                RemoteHeadUtil.ApplyRemoteHead(__instance, "SetPlayer");
                // 诊断：输出 TP 头部渲染器数量与 enabled 状态（确认 _headRenderers 是否为空）
                CharacterRenderer tp = Traverse.Create(__instance).Field("_characterThirdPerson").GetValue<CharacterRenderer>();
                if (tp != null)
                {
                    Renderer[] head = Traverse.Create(tp).Field("_headRenderers").GetValue<Renderer[]>();
                    string state = head == null
                        ? "null"
                        : string.Join(",", System.Array.ConvertAll(head, r => r != null ? r.enabled.ToString() : "null"));
                    Debug.Log("[MP-Head] player " + player.Id + " after SetPlayer: TP head count="
                        + (head != null ? head.Length.ToString() : "null") + " enabled=[" + state + "]");
                }
                Debug.Log("[MultiplayerPlus] Remote head fix applied for player " + player.Id);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MultiplayerPlus] remote head fix failed: " + e.Message);
            }
        }
    }

    // 16b. 看门狗：远程 TP 渲染器每帧强制"别人看你"状态，自愈任何重新隐藏头部/身体的情况。
    [HarmonyPatch(typeof(CharacterRenderer), "Update")]
    internal static class Patch_RemoteHeadWatchdog
    {
        private static readonly HashSet<int> _logged = new HashSet<int>();

        private static void Postfix(CharacterRenderer __instance)
        {
            try
            {
                if (!__instance.THIRD_PERSON_CHARACTER)
                {
                    return; // 只看第三人称渲染器（远程玩家以 TP 显示）
                }
                IPlayer player = RemoteHeadUtil.RendererPlayerRef(__instance);
                if (player == null || player.IsOwner)
                {
                    return;
                }
                Character character = player.Character;
                if (character == null)
                {
                    return;
                }
                RemoteHeadUtil.ApplyRemoteHead(character, "watchdog");
                if (_logged.Add(player.Id))
                {
                    Debug.Log("[MP-Head] watchdog enforcing remote TP state for player " + player.Id);
                }
            }
            catch (Exception)
            {
                // 静默：看门狗失败不打扰游戏
            }
        }
    }

}
