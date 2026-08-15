using Beam;
using Funlabs;

namespace MultiplayerPlus
{
    /// <summary>
    /// 客户端性别选择消息：客户端连接后把自己在加入弹窗里选的性别发给房主，
    /// 房主据此设置 ServerPeer.Gender2（客户端玩家 id=1 的模型性别），并复制回客户端两端一致渲染。
    /// </summary>
    public class GenderSelectMessage : MultiplayerMessage
    {
        [Replicate]
        public int Gender; // 0 = Male, 1 = Female（对应 EGameGenderMode）

        public override void OnPeer()
        {
            // 只在房主端处理：设置客户端玩家的性别
            if (Game.Mode.IsServer() && PlayerRegistry.ServerPeer != null)
            {
                PlayerRegistry.ServerPeer.Gender2 = (EGameGenderMode)Gender;
            }
        }
    }
}
