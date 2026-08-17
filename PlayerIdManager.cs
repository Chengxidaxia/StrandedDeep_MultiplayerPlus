using System.Collections.Generic;
using Funlabs;
using Photon.Bolt;

namespace MultiplayerPlus
{
    /// <summary>
    /// 多客户端玩家 id 分配管理。原版联机硬编码 1 房主 + 1 客户端（客户端 id 恒为 1），
    /// 本类在房主端为每个客户端分配递增 id（1、2、3…），并通过 Accept token 传回给客户端。
    /// </summary>
    public static class PlayerIdManager
    {
        /// <summary>客户端自己的玩家 id（从房主 Accept token 读到），默认 1 保持原版行为。</summary>
        public static int MyPlayerId = 1;

        /// <summary>房主 Accept 时待写入 Accept token 的 id（由 AllocateClientId 设置）。</summary>
        public static int PendingAcceptId = 1;

        /// <summary>当前房主设定的人数上限（房主端生效，客户端从房主同步）。</summary>
        public static int MaxPlayers = 2;

        private static int _nextClientId = 1;

        // 房主端：客户端设备标识（Join.Id）→ 分配的玩家 id
        private static readonly Dictionary<string, int> ClientKeyToId = new Dictionary<string, int>();

        // 房主端：BoltConnection.ConnectionId → 玩家 id
        private static readonly Dictionary<uint, int> ConnectionToId = new Dictionary<uint, int>();

        /// <summary>房主在 ConnectRequest 里为某个客户端分配 id（1 起递增）。</summary>
        public static int AllocateClientId(string clientKey)
        {
            int id = _nextClientId;
            _nextClientId++;
            PendingAcceptId = id;
            if (!string.IsNullOrEmpty(clientKey))
            {
                ClientKeyToId[clientKey] = id;
            }
            return id;
        }

        /// <summary>房主在 Connected 回调里，把连接绑定到已分配的 id（用 ConnectToken 里的 Join.Id 关联）。</summary>
        public static void BindConnection(BoltConnection connection)
        {
            if (connection == null)
            {
                return;
            }
            int id = PendingAcceptId; // 兜底：直接用最近分配的 id
            MultiplayerTokens.Join join = connection.ConnectToken as MultiplayerTokens.Join;
            if (join != null && !string.IsNullOrEmpty(join.Id) && ClientKeyToId.TryGetValue(join.Id, out int mapped))
            {
                id = mapped;
            }
            ConnectionToId[connection.ConnectionId] = id;
        }

        public static void UnbindConnection(BoltConnection connection)
        {
            if (connection != null)
            {
                ConnectionToId.Remove(connection.ConnectionId);
            }
        }

        /// <summary>房主端：通过实体 owner connection 查该实体所属客户端的玩家 id。</summary>
        public static int GetClientId(BoltEntity entity)
        {
            if (entity != null && entity.Source != null)
            {
                int id;
                if (ConnectionToId.TryGetValue(entity.Source.ConnectionId, out id))
                {
                    return id;
                }
            }
            return 1; // 兜底：映射未建立时退回原版客户端 id
        }

        /// <summary>重置所有分配状态（回到主菜单/退出联机时调用）。</summary>
        public static void Reset()
        {
            MyPlayerId = 1;
            PendingAcceptId = 1;
            _nextClientId = 1;
            ClientKeyToId.Clear();
            ConnectionToId.Clear();
        }
    }
}
