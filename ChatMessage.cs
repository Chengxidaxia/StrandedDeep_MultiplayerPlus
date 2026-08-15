using Funlabs;

namespace MultiplayerPlus
{
    /// <summary>
    /// 聊天消息。继承游戏自带的 MultiplayerMessage，借助其 [Replicate] 序列化与
    /// MessageGlobalEvent 广播机制，实现多人聊天同步。会被 MultiplayerMessageManager 自动注册。
    /// </summary>
    public class ChatMessage : MultiplayerMessage
    {
        [Replicate]
        public string Sender;

        [Replicate]
        public string Text;

        public override void OnPeer()
        {
            MultiplayerPlusUI.AddMessage(Sender, Text);
        }
    }
}
