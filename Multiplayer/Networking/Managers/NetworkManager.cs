using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Serialization;

namespace Multiplayer.Networking.Listeners;

public abstract class NetworkManager : INetEventListener, INatPunchListener
{
    protected readonly NetPacketProcessor netPacketProcessor;
    protected readonly NetManager netManager;
    protected readonly NetDataWriter cachedWriter = new();

    protected abstract string LogPrefix { get; }

    public NetStatistics Statistics => netManager.Statistics;
    public bool IsRunning => netManager.IsRunning;
    public bool IsProcessingPacket { get; private set; }

    protected NetworkManager(Settings settings)
    {
        netManager = new NetManager(this)
        {
            DisconnectTimeout = 10000,
            UnconnectedMessagesEnabled = true,
            BroadcastReceiveEnabled = true,

        };
        netPacketProcessor = new NetPacketProcessor(netManager);
        RegisterNestedTypes();
        OnSettingsUpdated(settings);
        Settings.OnSettingsUpdated += OnSettingsUpdated;
        // ReSharper disable once VirtualMemberCallInConstructor
        Subscribe();
    }

    private void RegisterNestedTypes()
    {
        netPacketProcessor.RegisterNestedType(BogieData.Serialize, BogieData.Deserialize);
        netPacketProcessor.RegisterNestedType<JobUpdateStruct>();
        netPacketProcessor.RegisterNestedType(JobData.Serialize, JobData.Deserialize);
        netPacketProcessor.RegisterNestedType(ModInfo.Serialize, ModInfo.Deserialize);
        netPacketProcessor.RegisterNestedType(RigidbodySnapshot.Serialize, RigidbodySnapshot.Deserialize);
        netPacketProcessor.RegisterNestedType(StationsChainNetworkData.Serialize, StationsChainNetworkData.Deserialize);
        netPacketProcessor.RegisterNestedType(TrainsetMovementPart.Serialize, TrainsetMovementPart.Deserialize);
        netPacketProcessor.RegisterNestedType(TrainsetSpawnPart.Serialize, TrainsetSpawnPart.Deserialize);
        netPacketProcessor.RegisterNestedType(Vector2Serializer.Serialize, Vector2Serializer.Deserialize);
        netPacketProcessor.RegisterNestedType(Vector3Serializer.Serialize, Vector3Serializer.Deserialize);
    }

    private void OnSettingsUpdated(Settings settings)
    {
        if (netManager == null)
            return;
        netManager.NatPunchEnabled = settings.EnableNatPunch;
        netManager.AutoRecycle = settings.ReuseNetPacketReaders;
        netManager.UseNativeSockets = settings.UseNativeSockets;
        netManager.EnableStatistics = settings.ShowStats;
        netManager.SimulatePacketLoss = settings.SimulatePacketLoss;
        netManager.SimulateLatency = settings.SimulateLatency;
        netManager.SimulationPacketLossChance = settings.SimulationPacketLossChance;
        netManager.SimulationMinLatency = settings.SimulationMinLatency;
        netManager.SimulationMaxLatency = settings.SimulationMaxLatency;
    }

    public void PollEvents()
    {
        netManager.PollEvents();
    }

    public virtual void Stop()
    {
        netManager.Stop(true);
        Settings.OnSettingsUpdated -= OnSettingsUpdated;
    }

    protected NetDataWriter WritePacket<T>(T packet) where T : class, new()
    {
        cachedWriter.Reset();
        netPacketProcessor.Write(cachedWriter, packet);
        return cachedWriter;
    }

    protected NetDataWriter WriteNetSerializablePacket<T>(T packet) where T : INetSerializable, new()
    {
        cachedWriter.Reset();
        netPacketProcessor.WriteNetSerializable<T>(cachedWriter, ref packet);
        return cachedWriter;
    }

    protected void SendPacket<T>(NetPeer peer, T packet, DeliveryMethod deliveryMethod) where T : class, new()
    {
        peer?.Send(WritePacket(packet), deliveryMethod);
    }

    protected void SendNetSerializablePacket<T>(NetPeer peer, T packet, DeliveryMethod deliveryMethod) where T : INetSerializable, new()
    {
        peer?.Send(WriteNetSerializablePacket(packet), deliveryMethod);
    }

    protected void SendUnconnectedPacket<T>(T packet, string ipAddress, int port) where T : class, new()
    {
        netManager.SendUnconnectedMessage(WritePacket(packet), ipAddress, port);
    }

    protected abstract void Subscribe(); 

    #region Net Events

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        //Log($"NetworkManager.OnNetworkReceive()");
        try
        {
            IsProcessingPacket = true;
            netPacketProcessor.ReadAllPackets(reader, peer);
        }
        catch (ParseException e)
        {
            Multiplayer.LogWarning($"Failed to parse packet: {e.Message}");
        }
        finally
        {
            IsProcessingPacket = false;
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        Multiplayer.LogError($"Network error from {endPoint}: {socketError}");
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        //Multiplayer.Log($"OnNetworkReceiveUnconnected({remoteEndPoint}, {messageType})");
        try
        {
            IsProcessingPacket = true;
            netPacketProcessor.ReadAllPackets(reader, remoteEndPoint);
        }
        catch (ParseException e) 
        {
            Multiplayer.LogWarning($"Failed to parse packet: {e.Message}");
        }
        finally
        {
            IsProcessingPacket = false;
        }
    }

    //Standard networking callbacks
    public abstract void OnPeerConnected(NetPeer peer);
    public abstract void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo);
    public abstract void OnNetworkLatencyUpdate(NetPeer peer, int latency);
    public abstract void OnConnectionRequest(ConnectionRequest request);

    //NAT punching callbacks
    public abstract void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token);
    public abstract void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token);
    #endregion

    #region Logging

    public void LogDebug(Func<object> resolver)
    {
        if (!Multiplayer.Settings.DebugLogging)
            return;
        Multiplayer.LogDebug(() => $"{LogPrefix} {resolver.Invoke()}");
    }

    public void Log(object msg)
    {
        Multiplayer.Log($"{LogPrefix} {msg}");
    }

    public void LogWarning(object msg)
    {
        Multiplayer.LogWarning($"{LogPrefix} {msg}");
    }

    public void LogError(object msg)
    {
        Multiplayer.LogError($"{LogPrefix} {msg}");
    }

    #endregion
}
