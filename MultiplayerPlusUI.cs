using System;
using System.Collections;
using System.Collections.Generic;
using Beam;
using Funlabs;
using Photon.Bolt;
using Rewired;
using UnityEngine;

namespace MultiplayerPlus
{
    /// <summary>
    /// 模组 UI：MC 风格聊天框 + IP 输入框。
    /// - 聊天消息：不打开聊天框也能看到新消息（自动显示几秒）；按 T 打开输入栏（捕获输入）。
    /// - 完全自定义深色半透明样式，不使用 Unity 默认 GUI 皮肤。
    /// </summary>
    public class MultiplayerPlusUI : MonoBehaviour
    {
        private struct ChatEntry
        {
            public string Sender;
            public string Text;
            public bool IsSystem;
        }

        private static MultiplayerPlusUI _instance;

        private static readonly List<ChatEntry> Messages = new List<ChatEntry>();
        private const int MaxMessages = 100;
        private const int BriefVisible = 6;    // 未打开时自动显示的条数
        private const int FullVisible = 12;    // 打开时显示的条数
        private const float BriefHoldSeconds = 5f; // 新消息自动显示时长

        private bool _chatOpen;
        private string _chatInput = "";
        private float _showMessagesUntil;
        private bool _prevCursorVisible;
        private CursorLockMode _prevCursorLockMode;

        private bool _ipDialogOpen;
        private string _ipInput = "";

        /// <summary>客户端加入时选择的角色性别：0 = 男（Male），1 = 女（Female）。</summary>
        public static int SelectedGender = 0;

        private Vector2 _scrollPosition;
        private static Font _emojiFont;

        private GUIStyle _boxStyle;
        private GUIStyle _boxStyleDim;
        private GUIStyle _labelStyle;
        private GUIStyle _inputStyle;
        private GUIStyle _buttonStyle;
        private Texture2D _panelTex;
        private Texture2D _panelTexDim;
        private Texture2D _inputTex;
        private Texture2D _selectedTex;
        private GUIStyle _selectedButtonStyle;
        private bool _stylesReady;

        // ---------- 外部接口 ----------

        public static void AddMessage(string sender, string text)
        {
            Messages.Add(new ChatEntry
            {
                Sender = string.IsNullOrEmpty(sender) ? "?" : sender,
                Text = text,
                IsSystem = false
            });
            if (Messages.Count > MaxMessages)
            {
                Messages.RemoveAt(0);
            }
            NotifyNewMessage();
        }

        public static void LogSystem(string text)
        {
            Messages.Add(new ChatEntry { Sender = "系统", Text = text, IsSystem = true });
            if (Messages.Count > MaxMessages)
            {
                Messages.RemoveAt(0);
            }
            NotifyNewMessage();
        }

        private static void NotifyNewMessage()
        {
            if (_instance != null)
            {
                _instance._showMessagesUntil = Time.unscaledTime + BriefHoldSeconds;
                _instance._scrollPosition.y = float.MaxValue; // 新消息自动滚到底部（ScrollView 会 clamp）
            }
        }

        public static void ShowIPDialog()
        {
            if (_instance == null)
            {
                return;
            }
            _instance._ipDialogOpen = true;
            _instance._ipInput = MultiplayerPlusPlugin.Instance.ConnectAddress.Value;
        }

        public static void HideIPDialog()
        {
            if (_instance == null)
            {
                return;
            }
            _instance._ipDialogOpen = false;
        }

        public static void WaitForServerPeerAndCall(Action action)
        {
            if (_instance == null)
            {
                if (action != null)
                {
                    action();
                }
                return;
            }
            _instance.StartCoroutine(_instance.WaitForServerPeerCoroutine(action));
        }

        public static void WaitAndCall(Action action, float delaySeconds)
        {
            if (_instance == null)
            {
                if (action != null)
                {
                    action();
                }
                return;
            }
            _instance.StartCoroutine(_instance.WaitAndCallCoroutine(action, delaySeconds));
        }

        // ---------- Unity 生命周期 ----------

        private void Awake()
        {
            _instance = this;
        }

        private void Update()
        {
            if (_chatOpen)
            {
                // ESC / Enter 只在聊天栏打开时检测，聊天栏关闭时绝不拦截这些键
                if (IsKeyDown(KeyCode.Escape))
                {
                    CloseChat();
                    return;
                }
                if (IsKeyDown(KeyCode.Return) || IsKeyDown(KeyCode.KeypadEnter))
                {
                    SendAndClose();
                    return;
                }
            }
            else if (!_ipDialogOpen)
            {
                // T 键只在"正常游玩"状态打开（游戏内、未暂停、无背包等 UI 遮挡），防止误开 bug
                if (CanOpenChat() && IsKeyDown(KeyCode.T))
                {
                    OpenChat();
                }
            }
        }

        // 键盘检测：Unity Input + Rewired 键盘控制器双保险（游戏用 Rewired，可能影响 Unity Input）
        private bool IsKeyDown(KeyCode key)
        {
            try
            {
                if (Input.GetKeyDown(key))
                {
                    return true;
                }
                Rewired.Player p = ReInput.players.GetPlayer(0);
                if (p != null && p.controllers.Keyboard != null)
                {
                    if (p.controllers.Keyboard.GetKeyDown(key))
                    {
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MultiplayerPlus] key check failed: " + e.Message);
            }
            return false;
        }

        // 是否处于"正常游玩"状态：游戏内、未暂停、无 UI 遮挡（背包等界面会解锁鼠标光标）
        private bool CanOpenChat()
        {
            if (!Game.State.IsGame())
            {
                return false;
            }
            try
            {
                if (Beam.UI.MainMenuPresenter.Instance != null && Beam.UI.MainMenuPresenter.Instance.IsGamePaused)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                // ignore
            }
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                return false;
            }
            return true;
        }

        private bool IsGamePausedNow()
        {
            try
            {
                return Beam.UI.MainMenuPresenter.Instance != null && Beam.UI.MainMenuPresenter.Instance.IsGamePaused;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void OnGUI()
        {
            EnsureStyles();

            // 聊天只在游戏内显示（主菜单不显示聊天消息）
            if (Game.State.IsGame())
            {
                // 暂停等非正常游玩状态下，聊天栏降为 70% 不透明度（弱化存在感）
                bool dimmed = IsGamePausedNow();
                if (_chatOpen)
                {
                    DrawChat(true, dimmed);
                }
                else if (Time.unscaledTime < _showMessagesUntil && Messages.Count > 0)
                {
                    DrawChat(false, dimmed);
                }
            }

            if (_ipDialogOpen)
            {
                DrawIPDialog();
            }
        }

        // ---------- 聊天框 ----------

        private void OpenChat()
        {
            _chatOpen = true;
            _chatInput = "";
            _prevCursorVisible = Cursor.visible;
            _prevCursorLockMode = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            SetPlayerInputEnabled(false);
        }

        private void CloseChat()
        {
            if (!_chatOpen)
            {
                return;
            }
            _chatOpen = false;
            _chatInput = "";
            Cursor.visible = _prevCursorVisible;
            Cursor.lockState = _prevCursorLockMode;
            SetPlayerInputEnabled(true);
        }

        private void SendAndClose()
        {
            string text = _chatInput.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                SendChat(text);
            }
            CloseChat();
        }

        private void SetPlayerInputEnabled(bool enabled)
        {
            try
            {
                Rewired.Player player = ReInput.players.GetPlayer(0);
                if (player != null)
                {
                    // 只禁用"Default"分类（游戏玩法移动/视角等），保留 UI 与暂停映射，
                    // 避免 SetAllMapsEnabled 把 ESC 暂停等一起禁掉。
                    player.controllers.maps.SetMapsEnabled(enabled, "Default");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MultiplayerPlus] input toggle failed: " + e.Message);
            }
        }

        private float GetChatScale()
        {
            try
            {
                float s = MultiplayerPlusPlugin.Instance != null ? MultiplayerPlusPlugin.Instance.ChatScale.Value : 1f;
                if (s < 0.5f) s = 0.5f;
                if (s > 3f) s = 3f;
                return s;
            }
            catch
            {
                return 1f;
            }
        }

        private void DrawChat(bool withInput, bool dimmed)
        {
            float scale = GetChatScale();
            int width = Mathf.Min(Mathf.RoundToInt(560 * scale), Screen.width - 20);
            int lineH = Mathf.RoundToInt(20 * scale);
            int pad = Mathf.RoundToInt(8 * scale);
            int inputH = Mathf.RoundToInt(26 * scale);

            int msgAreaH;
            if (withInput)
            {
                // 打开时：固定高度消息区，可用滚动条查看全部历史
                msgAreaH = Mathf.RoundToInt(220 * scale);
            }
            else
            {
                int visible = Mathf.Min(Messages.Count, BriefVisible);
                msgAreaH = visible * lineH;
            }

            int boxH = pad * 2 + msgAreaH + (withInput ? (pad + inputH) : 0);

            float x = 10;
            float y = Screen.height - boxH - 10;

            GUI.Box(new Rect(x, y, width, boxH), GUIContent.none, dimmed ? _boxStyleDim : _boxStyle);

            float msgX = x + pad;
            float msgY = y + pad;
            float msgW = width - pad * 2;

            if (withInput)
            {
                // 滚动区域：可滚动查看全部历史消息
                float contentH = Messages.Count * lineH;
                _scrollPosition = GUI.BeginScrollView(
                    new Rect(msgX, msgY, msgW, msgAreaH),
                    _scrollPosition,
                    new Rect(0, 0, msgW - 16, contentH));

                for (int i = 0; i < Messages.Count; i++)
                {
                    ChatEntry entry = Messages[i];
                    string line = "[" + entry.Sender + "] " + entry.Text;
                    GUI.Label(new Rect(0, i * lineH, msgW - 16, lineH), line, entry.IsSystem ? GetSystemLabelStyle() : _labelStyle);
                }
                GUI.EndScrollView();
            }
            else
            {
                // 未打开：固定显示最后 BriefVisible 条
                int start = Messages.Count - Mathf.Min(Messages.Count, BriefVisible);
                for (int i = start; i < Messages.Count; i++)
                {
                    ChatEntry entry = Messages[i];
                    string line = "[" + entry.Sender + "] " + entry.Text;
                    GUI.Label(new Rect(msgX, msgY + (i - start) * lineH, msgW, lineH), line, entry.IsSystem ? GetSystemLabelStyle() : _labelStyle);
                }
            }

            if (withInput)
            {
                float ty = y + pad + msgAreaH + pad;
                float btnW = Mathf.RoundToInt(56 * scale);
                float btnGap = 6;
                float inputW = width - pad * 2 - btnW * 2 - btnGap * 2;

                GUI.SetNextControlName("MPChatInput");
                _chatInput = GUI.TextField(new Rect(x + pad, ty, inputW, inputH), _chatInput, _inputStyle);
                GUI.FocusControl("MPChatInput");

                if (GUI.Button(new Rect(x + pad + inputW + btnGap, ty, btnW, inputH), "发送", _buttonStyle))
                {
                    SendAndClose();
                }
                if (GUI.Button(new Rect(x + pad + inputW + btnGap * 2 + btnW, ty, btnW, inputH), "关闭", _buttonStyle))
                {
                    CloseChat();
                }

                UnityEngine.Event ev = UnityEngine.Event.current;
                if (ev != null && ev.type == EventType.KeyDown)
                {
                    if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                    {
                        SendAndClose();
                        ev.Use();
                    }
                    else if (ev.keyCode == KeyCode.Escape)
                    {
                        CloseChat();
                        ev.Use();
                    }
                }
            }
        }

        private GUIStyle _systemLabelStyle;
        private GUIStyle GetSystemLabelStyle()
        {
            if (_systemLabelStyle == null)
            {
                _systemLabelStyle = new GUIStyle(_labelStyle);
                _systemLabelStyle.normal.textColor = new Color(1f, 0.85f, 0.3f, 1f);
            }
            return _systemLabelStyle;
        }

        private void SendChat(string text)
        {
            string name = GetPlayerName();
            AddMessage(name, text);

            if (Game.Mode.IsMultiplayer() && (BoltNetwork.IsServer || BoltNetwork.IsClient))
            {
                try
                {
                    new ChatMessage { Sender = name, Text = text }.Post();
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[MultiplayerPlus] chat send failed: " + e.Message);
                }
            }
        }

        private string GetPlayerName()
        {
            try
            {
                string n = MultiplayerMng.JoinToken.Name;
                if (!string.IsNullOrEmpty(n))
                {
                    return n;
                }
            }
            catch
            {
                // ignore
            }
            return "玩家";
        }

        // ---------- IP 输入框 ----------

        private void DrawIPDialog()
        {
            int w = 440;
            int h = 168;
            float x = (Screen.width - w) / 2f;
            float y = (Screen.height - h) / 2f;

            GUI.Box(new Rect(x, y, w, h), GUIContent.none, _boxStyle);

            GUI.Label(new Rect(x + 16, y + 12, w - 32, 22), "输入服务器 IP:端口 后点击加入", _labelStyle);

            GUI.SetNextControlName("MPIPInput");
            _ipInput = GUI.TextField(new Rect(x + 16, y + 38, w - 32, 26), _ipInput, _inputStyle);

            // 性别（角色模型）选择：客户端决定自己在服务端渲染的模型
            GUI.Label(new Rect(x + 16, y + 70, w - 32, 22), "我的角色模型:", _labelStyle);
            if (GUI.Button(new Rect(x + 16, y + 94, 96, 26), SelectedGender == 0 ? "▶ 男性" : "男性", SelectedGender == 0 ? _selectedButtonStyle : _buttonStyle))
            {
                SelectedGender = 0;
            }
            if (GUI.Button(new Rect(x + 122, y + 94, 96, 26), SelectedGender == 1 ? "▶ 女性" : "女性", SelectedGender == 1 ? _selectedButtonStyle : _buttonStyle))
            {
                SelectedGender = 1;
            }

            if (GUI.Button(new Rect(x + 16, y + 130, 100, 28), "加入"))
            {
                ConfirmIPDialog();
            }
            if (GUI.Button(new Rect(x + w - 116, y + 130, 100, 28), "取消"))
            {
                _ipDialogOpen = false;
            }

            GUI.FocusControl("MPIPInput");
        }

        private void ConfirmIPDialog()
        {
            string input = (_ipInput ?? "").Trim();
            if (input.Length == 0)
            {
                return;
            }

            string ip;
            ushort port = (ushort)MultiplayerPlusPlugin.Instance.ListenPort.Value;

            int idx = input.LastIndexOf(':');
            if (idx > 0 && idx < input.Length - 1)
            {
                ip = input.Substring(0, idx);
                ushort.TryParse(input.Substring(idx + 1), out port);
            }
            else
            {
                ip = input;
            }

            if (port == 0)
            {
                port = (ushort)MultiplayerPlusPlugin.Instance.ListenPort.Value;
            }

            _ipDialogOpen = false;
            StartClientJoin(ip, port);
        }

        private void StartClientJoin(string ip, ushort port)
        {
            if (BoltNetwork.IsRunning)
            {
                BoltNetwork.ShutdownImmediate();
            }

            LogSystem("正在连接 " + ip + ":" + port + " ...");

            Game.Mode = GameMode.Coop_Client;

            MultiplayerMng.ConnectToCloud(0, delegate
            {
                DirectConnect.ConnectClient(ip, port, MultiplayerMng.JoinToken);
            }, delegate (string error)
            {
                LogSystem("连接失败: " + error);
                Game.Mode = GameMode.SinglePlayer;
            });
        }

        // ---------- 协程 ----------

        private IEnumerator WaitForServerPeerCoroutine(Action action)
        {
            float start = Time.unscaledTime;
            while (PlayerRegistry.ServerPeer == null && Time.unscaledTime - start < 3f)
            {
                yield return null;
            }
            if (action != null)
            {
                action();
            }
        }

        private IEnumerator WaitAndCallCoroutine(Action action, float delaySeconds)
        {
            yield return new WaitForSecondsRealtime(delaySeconds);
            if (action != null)
            {
                action();
            }
        }

        // ---------- 样式（完全自定义，不继承 Unity 默认皮肤） ----------

        private void EnsureStyles()
        {
            if (_stylesReady)
            {
                return;
            }

            // 半透明深色面板背景（参考游戏控制台的深色底）—— 正常 80% 不透明
            _panelTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _panelTex.SetPixel(0, 0, new Color(0.07f, 0.08f, 0.10f, 0.80f));
            _panelTex.Apply();

            // 弱化版 70% 不透明（暂停等非正常游玩状态下使用）
            _panelTexDim = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _panelTexDim.SetPixel(0, 0, new Color(0.07f, 0.08f, 0.10f, 0.70f));
            _panelTexDim.Apply();

            // 输入框深色背景
            _inputTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _inputTex.SetPixel(0, 0, new Color(0.02f, 0.02f, 0.03f, 0.90f));
            _inputTex.Apply();

            _boxStyle = new GUIStyle();
            _boxStyle.normal.background = _panelTex;
            _boxStyle.border = new RectOffset(4, 4, 4, 4);
            _boxStyle.padding = new RectOffset(4, 4, 4, 4);

            _boxStyleDim = new GUIStyle();
            _boxStyleDim.normal.background = _panelTexDim;
            _boxStyleDim.border = new RectOffset(4, 4, 4, 4);
            _boxStyleDim.padding = new RectOffset(4, 4, 4, 4);

            Font emojiFont = GetEmojiFont();

            _labelStyle = new GUIStyle();
            _labelStyle.normal.textColor = new Color(0.88f, 0.90f, 0.93f, 1f);
            _labelStyle.fontSize = Mathf.RoundToInt(14 * GetChatScale());
            _labelStyle.alignment = TextAnchor.MiddleLeft;
            _labelStyle.clipping = TextClipping.Clip;
            _labelStyle.font = emojiFont;

            _inputStyle = new GUIStyle();
            _inputStyle.normal.background = _inputTex;
            _inputStyle.normal.textColor = new Color(0.92f, 0.94f, 0.96f, 1f);
            _inputStyle.fontSize = Mathf.RoundToInt(14 * GetChatScale());
            _inputStyle.border = new RectOffset(4, 4, 4, 4);
            _inputStyle.padding = new RectOffset(6, 6, 3, 3);
            _inputStyle.alignment = TextAnchor.MiddleLeft;
            _inputStyle.font = emojiFont;

            _buttonStyle = new GUIStyle();
            _buttonStyle.normal.background = _inputTex;
            _buttonStyle.normal.textColor = new Color(0.92f, 0.94f, 0.96f, 1f);
            _buttonStyle.fontSize = Mathf.RoundToInt(13 * GetChatScale());
            _buttonStyle.alignment = TextAnchor.MiddleCenter;
            _buttonStyle.border = new RectOffset(4, 4, 4, 4);
            _buttonStyle.font = emojiFont;

            // 性别选择按钮的高亮样式（绿色背景）
            _selectedTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _selectedTex.SetPixel(0, 0, new Color(0.10f, 0.45f, 0.20f, 0.90f));
            _selectedTex.Apply();

            _selectedButtonStyle = new GUIStyle();
            _selectedButtonStyle.normal.background = _selectedTex;
            _selectedButtonStyle.normal.textColor = Color.white;
            _selectedButtonStyle.fontSize = Mathf.RoundToInt(13 * GetChatScale());
            _selectedButtonStyle.alignment = TextAnchor.MiddleCenter;
            _selectedButtonStyle.border = new RectOffset(4, 4, 4, 4);
            _selectedButtonStyle.font = emojiFont;

            _stylesReady = true;
        }

        // 加载支持 emoji 的系统字体（微软雅黑中文 + Segoe UI Emoji + Arial 英文 fallback），缓存复用
        private Font GetEmojiFont()
        {
            if (_emojiFont == null)
            {
                try
                {
                    _emojiFont = Font.CreateDynamicFontFromOSFont(new string[] { "Microsoft YaHei", "Segoe UI Emoji", "Arial" }, 16);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[MultiplayerPlus] emoji font load failed: " + e.Message);
                }
            }
            return _emojiFont;
        }
    }
}
