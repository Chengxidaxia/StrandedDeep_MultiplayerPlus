using System;
using System.Reflection;
using Photon.Bolt;
using UdpKit;
using UdpKit.Platform;
using UnityEngine;

namespace MultiplayerPlus
{
    /// <summary>
    /// IP 直连核心：负责把 Photon Cloud 平台替换为本地 UDP（DotNetPlatform），
    /// 以及调用 Bolt 内部接口建立 IP 直连。
    /// </summary>
    public static class DirectConnect
    {
        private static UdpPlatform _platform;
        private static Action<UdpEndPoint, IProtocolToken> _boltCoreConnect;

        /// <summary>
        /// 获取（并缓存）DotNetPlatform 实例。该类型在发行版里是 internal 且可能被混淆，
        /// 因此通过反射定位并实例化。
        /// </summary>
        public static UdpPlatform GetDotNetPlatform()
        {
            if (_platform != null)
            {
                return _platform;
            }

            try
            {
                Type type = FindType("UdpKit.Platform.DotNetPlatform", "udpkit.platform.dotnet");
                if (type == null)
                {
                    // 兜底：遍历 udpkit.platform.dotnet 程序集，找 UdpPlatform 的非抽象子类
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name != "udpkit.platform.dotnet")
                        {
                            continue;
                        }
                        foreach (Type t in asm.GetTypes())
                        {
                            if (typeof(UdpPlatform).IsAssignableFrom(t) && !t.IsAbstract)
                            {
                                type = t;
                                break;
                            }
                        }
                        if (type != null)
                        {
                            break;
                        }
                    }
                }

                if (type != null)
                {
                    _platform = (UdpPlatform)Activator.CreateInstance(type, true);
                    Debug.Log("[MultiplayerPlus] UDP platform ready: " + type.FullName);
                }
                else
                {
                    Debug.LogError("[MultiplayerPlus] DotNetPlatform not found, IP direct connect will not work.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[MultiplayerPlus] Failed to create DotNetPlatform: " + e);
            }

            return _platform;
        }

        /// <summary>
        /// 客户端直连房主。BoltCore.Connect(UdpEndPoint, IProtocolToken) 是 internal 方法，
        /// 通过 CreateDelegate 生成强类型委托调用实现真正的 IP 直连。
        /// </summary>
        public static void ConnectClient(string ip, ushort port, IProtocolToken token)
        {
            try
            {
                Action<UdpEndPoint, IProtocolToken> connect = GetBoltCoreConnect();
                if (connect == null)
                {
                    Debug.LogError("[MultiplayerPlus] BoltCore.Connect method not found.");
                    return;
                }

                UdpIPv4Address address = UdpIPv4Address.Parse(ip);
                UdpEndPoint endpoint = new UdpEndPoint(address, port);
                // 关键：用强类型委托直接调用，而非 MethodInfo.Invoke。
                // Bolt 的 UdpSocket.Raise 有反反射保护：直连非 localhost 时遍历调用栈，
                // 只要发现 System.Reflection 命名空间的帧（反射调用）且其程序集不在白名单
                // ["bolt","udpkit.platform.dotnet","udpkit.platform.photon"] 就抛 UdpKitAccessException。
                // CreateDelegate 生成的委托直接调用，调用栈里没有 System.Reflection 帧，可绕过。
                connect(endpoint, token);
                Debug.Log("[MultiplayerPlus] Connecting to " + ip + ":" + port);
            }
            catch (Exception e)
            {
                Debug.LogError("[MultiplayerPlus] Connect failed: " + e);
            }
        }

        private static Action<UdpEndPoint, IProtocolToken> GetBoltCoreConnect()
        {
            if (_boltCoreConnect != null)
            {
                return _boltCoreConnect;
            }

            Type boltCore = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "bolt")
                {
                    continue;
                }
                boltCore = asm.GetType("Photon.Bolt.Internal.BoltCore");
                if (boltCore != null)
                {
                    break;
                }
            }

            if (boltCore == null)
            {
                return null;
            }

            MethodInfo method = boltCore.GetMethod(
                "Connect",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new Type[] { typeof(UdpEndPoint), typeof(IProtocolToken) },
                null);

            if (method == null)
            {
                return null;
            }

            try
            {
                _boltCoreConnect = (Action<UdpEndPoint, IProtocolToken>)method.CreateDelegate(typeof(Action<UdpEndPoint, IProtocolToken>));
            }
            catch (Exception e)
            {
                Debug.LogError("[MultiplayerPlus] CreateDelegate for BoltCore.Connect failed: " + e);
                return null;
            }

            return _boltCoreConnect;
        }

        private static Type FindType(string fullName, string assemblyName)
        {
            try
            {
                Type t = Type.GetType(fullName + ", " + assemblyName);
                if (t != null)
                {
                    return t;
                }
            }
            catch
            {
                // ignore
            }

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != assemblyName)
                {
                    continue;
                }
                Type t = asm.GetType(fullName);
                if (t != null)
                {
                    return t;
                }
            }
            return null;
        }
    }
}
