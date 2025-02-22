using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.Simulation.Brake;
using DV.Simulation.Cars;
using DV.ThingTypes;
using LocoSim.Definitions;
using LocoSim.Implementations;
using Multiplayer.Components.Networking.Player;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Packets.Common.Train;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Components.Networking.Train;

public class NetworkedTrainCar : IdMonoBehaviour<ushort, NetworkedTrainCar>
{
    #region Lookup Cache

    private static readonly Dictionary<TrainCar, NetworkedTrainCar> trainCarsToNetworkedTrainCars = new();
    private static readonly Dictionary<string, NetworkedTrainCar> trainCarIdToNetworkedTrainCars = new();
    private static readonly Dictionary<string, TrainCar> trainCarIdToTrainCars = new();
    private static readonly Dictionary<HoseAndCock, Coupler> hoseToCoupler = new();

    public static bool Get(ushort netId, out NetworkedTrainCar obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedTrainCar> rawObj);
        obj = (NetworkedTrainCar)rawObj;
        return b;
    }

    public static bool GetTrainCar(ushort netId, out TrainCar obj)
    {
        bool b = Get(netId, out NetworkedTrainCar networkedTrainCar);
        obj = b ? networkedTrainCar.TrainCar : null;
        return b;
    }

    public static Coupler GetCoupler(HoseAndCock hoseAndCock)
    {
        return hoseToCoupler[hoseAndCock];
    }
    public static bool TryGetCoupler(HoseAndCock hoseAndCock, out Coupler coupler)
    {
        return hoseToCoupler.TryGetValue(hoseAndCock, out coupler);
    }

    public static bool GetFromTrainId(string carId, out NetworkedTrainCar networkedTrainCar)
    {
        return trainCarIdToNetworkedTrainCars.TryGetValue(carId, out networkedTrainCar);
    }
    public static bool  GetTrainCarFromTrainId(string carId, out TrainCar trainCar)
    {
        return trainCarIdToTrainCars.TryGetValue(carId, out trainCar);
    }

    public static bool TryGetFromTrainCar(TrainCar trainCar, out NetworkedTrainCar networkedTrainCar)
    {
        return trainCarsToNetworkedTrainCars.TryGetValue(trainCar, out networkedTrainCar);
    }

    #endregion

    public TrainCar TrainCar;
    public uint TicksSinceSync = uint.MaxValue;
    public bool HasPlayers => PlayerManager.Car == TrainCar || GetComponentInChildren<NetworkedPlayer>() != null;

    private Bogie bogie1;
    private Bogie bogie2;
    private BrakeSystem brakeSystem;

    private bool hasSimFlow;
    private SimulationFlow simulationFlow;
    public FireboxSimController firebox;

    private HashSet<string> dirtyPorts;
    private Dictionary<string, float> lastSentPortValues;
    private HashSet<string> dirtyFuses;
    private bool handbrakeDirty;
    private bool mainResPressureDirty;
    public bool BogieTracksDirty;
    private bool cargoDirty;
    private bool cargoIsLoading;
    public byte CargoModelIndex = byte.MaxValue;
    private bool healthDirty;
    private bool sendCouplers;
    private bool sendCables;
    private bool fireboxDirty;

    public bool IsDestroying;

    #region Client

    public bool Client_Initialized {get; private set;}
    public TickedQueue<float> Client_trainSpeedQueue;
    public TickedQueue<RigidbodySnapshot> Client_trainRigidbodyQueue;
    public TickedQueue<BogieData> client_bogie1Queue;
    public TickedQueue<BogieData> client_bogie2Queue;

    #endregion

    protected override bool IsIdServerAuthoritative => true;

    protected override void Awake()
    {
        base.Awake();

        TrainCar = GetComponent<TrainCar>();
        trainCarsToNetworkedTrainCars[TrainCar] = this;

        TrainCar.LogicCarInitialized += OnLogicCarInitialised;
        
        bogie1 = TrainCar.Bogies[0];
        bogie2 = TrainCar.Bogies[1];

        if (NetworkLifecycle.Instance.IsHost())
        {
            NetworkTrainsetWatcher.Instance.CheckInstance(); // Ensure the NetworkTrainsetWatcher is initialized
        }
        else
        {
            Client_trainSpeedQueue = TrainCar.GetOrAddComponent<TrainSpeedQueue>();
            Client_trainRigidbodyQueue = TrainCar.GetOrAddComponent<NetworkedRigidbody>();
            StartCoroutine(Client_InitLater());
        }
    }

    private void Start()
    {
        brakeSystem = TrainCar.brakeSystem;

        foreach (Coupler coupler in TrainCar.couplers)
            hoseToCoupler[coupler.hoseAndCock] = coupler;

        SimController simController = GetComponent<SimController>();
        if (simController != null)
        {
            hasSimFlow = true;
            simulationFlow = simController.SimulationFlow;

            dirtyPorts = new HashSet<string>(simulationFlow.fullPortIdToPort.Count);
            lastSentPortValues = new Dictionary<string, float>(dirtyPorts.Count);
            foreach (KeyValuePair<string, Port> kvp in simulationFlow.fullPortIdToPort)
                if (kvp.Value.valueType == PortValueType.CONTROL || NetworkLifecycle.Instance.IsHost())
                    kvp.Value.ValueUpdatedInternally += _ => { Common_OnPortUpdated(kvp.Value); };

            dirtyFuses = new HashSet<string>(simulationFlow.fullFuseIdToFuse.Count);
            foreach (KeyValuePair<string, Fuse> kvp in simulationFlow.fullFuseIdToFuse)
                kvp.Value.StateUpdated += _ => { Common_OnFuseUpdated(kvp.Value); };
        
            if (simController.firebox != null)
            {
                firebox = simController.firebox;
                firebox.fireboxCoalControlPort.ValueUpdatedInternally += Client_OnAddCoal;   //Player adding coal
                firebox.fireboxIgnitionPort.ValueUpdatedInternally += Client_OnIgnite;      //Player igniting firebox
            }
        }
         
        brakeSystem.HandbrakePositionChanged += Common_OnHandbrakePositionChanged;
        brakeSystem.BrakeCylinderReleased += Common_OnBrakeCylinderReleased;
        

        NetworkLifecycle.Instance.OnTick += Common_OnTick;
        if (NetworkLifecycle.Instance.IsHost())
        {
            NetworkLifecycle.Instance.OnTick += Server_OnTick;
            bogie1.TrackChanged += Server_BogieTrackChanged;
            bogie2.TrackChanged += Server_BogieTrackChanged;
            TrainCar.CarDamage.CarEffectiveHealthStateUpdate += Server_CarHealthUpdate;

            brakeSystem.MainResPressureChanged += Server_MainResUpdate;

            if (firebox != null)
            {
                firebox.fireboxContentsPort.ValueUpdatedInternally += Common_OnFireboxUpdate;
                firebox.fireOnPort.ValueUpdatedInternally += Common_OnFireboxUpdate;
            }

            StartCoroutine(Server_WaitForLogicCar());
        }
    }

    private void OnDisable()
    {
        if (UnloadWatcher.isQuitting)
            return;

        NetworkLifecycle.Instance.OnTick -= Common_OnTick;
        NetworkLifecycle.Instance.OnTick -= Server_OnTick;
        if (UnloadWatcher.isUnloading)
            return;

        trainCarsToNetworkedTrainCars.Remove(TrainCar);
        if (TrainCar.logicCar != null)
        {
            trainCarIdToNetworkedTrainCars.Remove(TrainCar.ID);
            trainCarIdToTrainCars.Remove(TrainCar.ID);
        }

        foreach (Coupler coupler in TrainCar.couplers)
            hoseToCoupler.Remove(coupler.hoseAndCock);

        if (firebox != null)
        {
            firebox.fireboxCoalControlPort.ValueUpdatedInternally -= Client_OnAddCoal;   //Player adding coal
            firebox.fireboxIgnitionPort.ValueUpdatedInternally -= Client_OnIgnite;      //Player igniting firebox
        }

        if (brakeSystem != null)
        {
            brakeSystem.HandbrakePositionChanged -= Common_OnHandbrakePositionChanged;
            brakeSystem.BrakeCylinderReleased -= Common_OnBrakeCylinderReleased;
        }

        if (NetworkLifecycle.Instance.IsHost())
        {
            bogie1.TrackChanged -= Server_BogieTrackChanged;
            bogie2.TrackChanged -= Server_BogieTrackChanged;

            TrainCar.CarDamage.CarEffectiveHealthStateUpdate -= Server_CarHealthUpdate;

            if(brakeSystem != null)
                brakeSystem.MainResPressureChanged -= Server_MainResUpdate;

            if (firebox != null)
            {
                firebox.fireboxContentsPort.ValueUpdatedInternally -= Common_OnFireboxUpdate;
                firebox.fireOnPort.ValueUpdatedInternally -= Common_OnFireboxUpdate;
            }

            if (TrainCar.logicCar != null)
            {
                TrainCar.logicCar.CargoLoaded -= Server_OnCargoLoaded;
                TrainCar.logicCar.CargoUnloaded -= Server_OnCargoUnloaded;
            }
        }

        Destroy(this);
    }

    #region Server

    private void OnLogicCarInitialised()
    {
        //Multiplayer.LogWarning("OnLogicCarInitialised");
        if (TrainCar.logicCar != null)
        {
            trainCarIdToNetworkedTrainCars[TrainCar.ID] = this;
            trainCarIdToTrainCars[TrainCar.ID] = TrainCar;

            TrainCar.LogicCarInitialized -= OnLogicCarInitialised;
        }
        else
        {
            Multiplayer.LogWarning("OnLogicCarInitialised Car Not Initialised!");
        }
        
    }
    private IEnumerator Server_WaitForLogicCar()
    {
        while (TrainCar.logicCar == null)
            yield return null;

        TrainCar.logicCar.CargoLoaded += Server_OnCargoLoaded;
        TrainCar.logicCar.CargoUnloaded += Server_OnCargoUnloaded;

        Server_DirtyAllState();
    }

    public void Server_DirtyAllState()
    {
        handbrakeDirty = true;
        mainResPressureDirty = true;
        cargoDirty = true;
        cargoIsLoading = true;
        healthDirty = true;
        BogieTracksDirty = true;
        sendCouplers = true;
        sendCables = true;
        fireboxDirty = firebox != null; //only dirty if exists

        if (!hasSimFlow)
            return;
        foreach (string portId in simulationFlow.fullPortIdToPort.Keys)
        {
            dirtyPorts.Add(portId);
            if (simulationFlow.TryGetPort(portId, out Port port))
            {
                lastSentPortValues[portId] = port.value;

                //Multiplayer.Log($"Server_DirtyAllState({TrainCar.ID}): {portId}({port.type}): {port.value}({port.valueType})");

            }
        }

        foreach (string fuseId in simulationFlow.fullFuseIdToFuse.Keys)
            dirtyFuses.Add(fuseId);
    }

    public bool Server_ValidateClientSimFlowPacket(ServerPlayer player, CommonTrainPortsPacket packet)
    {
        // Only allow control ports to be updated by clients
        if (hasSimFlow)
            foreach (string portId in packet.PortIds)
                if (simulationFlow.TryGetPort(portId, out Port port) && port.valueType != PortValueType.CONTROL)
                {
                    NetworkLifecycle.Instance.Server.LogWarning($"Player {player.Username} tried to send a non-control port!");
                    Common_DirtyPorts(packet.PortIds);
                    return false;
                }
                else
                {
                    NetworkLifecycle.Instance.Server.LogWarning($"Player {player.Username} sent portId: {portId}, value type: {port.valueType}");
                }

        // Only allow the player to update ports on the car they are in/near
        if (player.CarId == packet.NetId)
            return true;

        // Some ports can be updated by the player even if they are not in the car, like doors and windows.
        // Only deny the request if the player is more than 5 meters away from any point of the car.
        float carLength = CarSpawner.Instance.carLiveryToCarLength[TrainCar.carLivery];
        if ((player.WorldPosition - transform.position).sqrMagnitude <= carLength * carLength)
            return true;

        NetworkLifecycle.Instance.Server.LogWarning($"Player {player.Username} tried to send a sim flow packet for a car they are not in!");
        Common_DirtyPorts(packet.PortIds);
        return false;
    }

    private void Server_BogieTrackChanged(RailTrack arg1, Bogie arg2)
    {
        BogieTracksDirty = true;
    }

    private void Server_OnCargoLoaded(CargoType obj)
    {
        cargoDirty = true;
        cargoIsLoading = true;
    }

    private void Server_OnCargoUnloaded()
    {
        cargoDirty = true;
        cargoIsLoading = false;
        CargoModelIndex = byte.MaxValue;
    }

    private void Server_CarHealthUpdate(float health)
    {
        healthDirty = true;
    }

    private void Server_MainResUpdate(float normalizedPressure, float pressure)
    {
        mainResPressureDirty = true;
    }

    private void Server_FireboxUpdate(float normalizedPressure, float pressure)
    {
        fireboxDirty = true;
    }

    private void Server_OnTick(uint tick)
    {
        if (UnloadWatcher.isUnloading)
            return;

        Server_SendBrakePressures();
        Server_SendFireBoxState();
        //Server_SendCouplers();
        Server_SendCables();
        Server_SendCargoState();
        Server_SendHealthState();

        TicksSinceSync++; //keep track of last full sync
    }

    private void Server_SendBrakePressures()
    {
        if (!mainResPressureDirty)
            return;

        mainResPressureDirty = false;
        //B99 review need / mod NetworkLifecycle.Instance.Server.SendBrakePressures(NetId, brakeSystem.mainReservoirPressure, brakeSystem.independentPipePressure, brakeSystem.brakePipePressure, brakeSystem.brakeCylinderPressure);
    }

    private void Server_SendFireBoxState()
    {
        if (!fireboxDirty || firebox == null)
            return;

        fireboxDirty = false;
        NetworkLifecycle.Instance.Server.SendFireboxState(NetId, firebox.fireboxContentsPort.value, firebox.IsFireOn);
    }

    private void Server_SendCouplers()
    {
        if (!sendCouplers)
            return;

        sendCouplers = false;

        if(TrainCar.frontCoupler.IsCoupled())
            NetworkLifecycle.Instance.Client.SendTrainCouple(TrainCar.frontCoupler,TrainCar.frontCoupler.coupledTo,false, false);

        if(TrainCar.rearCoupler.IsCoupled())
            NetworkLifecycle.Instance.Client.SendTrainCouple(TrainCar.rearCoupler,TrainCar.rearCoupler.coupledTo,false, false);

        if (TrainCar.frontCoupler.hoseAndCock.IsHoseConnected)
            NetworkLifecycle.Instance.Client.SendHoseConnected(TrainCar.frontCoupler, TrainCar.frontCoupler.coupledTo, false);

        if (TrainCar.rearCoupler.hoseAndCock.IsHoseConnected)
            NetworkLifecycle.Instance.Client.SendHoseConnected(TrainCar.rearCoupler, TrainCar.rearCoupler.coupledTo, false);

        NetworkLifecycle.Instance.Client.SendCockState(NetId, TrainCar.frontCoupler, TrainCar.frontCoupler.IsCockOpen);
        NetworkLifecycle.Instance.Client.SendCockState(NetId, TrainCar.rearCoupler, TrainCar.rearCoupler.IsCockOpen);
    }
    private void Server_SendCables()
    {
        if (!sendCables)
            return;
        sendCables = false;

        if(TrainCar.muModule == null)
            return;

        if (TrainCar.muModule.frontCable.IsConnected)
            NetworkLifecycle.Instance.Client.SendMuConnected(TrainCar.muModule.frontCable, TrainCar.muModule.frontCable.connectedTo, false);

        if (TrainCar.muModule.rearCable.IsConnected)
            NetworkLifecycle.Instance.Client.SendMuConnected(TrainCar.muModule.rearCable, TrainCar.muModule.rearCable.connectedTo, false);
    }

    private void Server_SendCargoState()
    {
        if (!cargoDirty)
            return;
        cargoDirty = false;
        if (cargoIsLoading && TrainCar.logicCar.CurrentCargoTypeInCar == CargoType.None)
            return;
        NetworkLifecycle.Instance.Server.SendCargoState(TrainCar, NetId, cargoIsLoading, CargoModelIndex);
    }

    private void Server_SendHealthState()
    {
        if (!healthDirty)
            return;
        healthDirty = false;
        NetworkLifecycle.Instance.Server.SendCarHealthUpdate(NetId, TrainCar.CarDamage.currentHealth);
    }

    #endregion

    #region Common

    private void Common_OnTick(uint tick)
    {
        if (UnloadWatcher.isUnloading)
            return;

        Common_SendHandbrakePosition();
        Common_SendFuses();
        Common_SendPorts();
    }

    private void Common_SendHandbrakePosition()
    {
        if (!handbrakeDirty)
            return;
        if (!TrainCar.brakeSystem.hasHandbrake)
            return;

        handbrakeDirty = false;
        NetworkLifecycle.Instance.Client.SendHandbrakePositionChanged(NetId, brakeSystem.handbrakePosition);
    }

    public void Common_DirtyPorts(string[] portIds)
    {
        if (!hasSimFlow)
            return;

        foreach (string portId in portIds)
        {
            if (!simulationFlow.TryGetPort(portId, out Port _))
            {

                Multiplayer.LogWarning($"Tried to dirty port {portId} on UNKNOWN but it doesn't exist!");
                Multiplayer.LogWarning($"Tried to dirty port {portId} on {TrainCar.ID} but it doesn't exist!");
                continue;
            }

            dirtyPorts.Add(portId);
        }
    }

    public void Common_DirtyFuses(string[] fuseIds)
    {
        if (!hasSimFlow)
            return;

        foreach (string fuseId in fuseIds)
        {
            if (!simulationFlow.TryGetFuse(fuseId, out Fuse _))
            {
                Multiplayer.LogWarning($"Tried to dirty port {fuseId} on UNKOWN but it doesn't exist!");
                Multiplayer.LogWarning($"Tried to dirty port {fuseId} on {TrainCar.ID} but it doesn't exist!");
                continue;
            }

            dirtyFuses.Add(fuseId);
        }
    }

    private void Common_SendPorts()
    {
        if (!hasSimFlow || dirtyPorts.Count == 0)
            return;

        int i = 0;
        string[] portIds = dirtyPorts.ToArray();
        float[] portValues = new float[portIds.Length];
        foreach (string portId in dirtyPorts)
        {
            float value = simulationFlow.fullPortIdToPort[portId].Value;
            portValues[i++] = value;
            lastSentPortValues[portId] = value;
        }

        dirtyPorts.Clear();

        NetworkLifecycle.Instance.Client.SendPorts(NetId, portIds, portValues);
    }

    private void Common_SendFuses()
    {
        if (!hasSimFlow || dirtyFuses.Count == 0)
            return;

        int i = 0;
        string[] fuseIds = dirtyFuses.ToArray();
        bool[] fuseValues = new bool[fuseIds.Length];
        foreach (string fuseId in dirtyFuses)
            fuseValues[i++] = simulationFlow.fullFuseIdToFuse[fuseId].State;

        dirtyFuses.Clear();

        NetworkLifecycle.Instance.Client.SendFuses(NetId, fuseIds, fuseValues);
    }

    private void Common_OnHandbrakePositionChanged((float, bool) data)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return;
        handbrakeDirty = true;
    }

    private void Common_OnBrakeCylinderReleased()
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return;
        NetworkLifecycle.Instance.Client.SendBrakeCylinderReleased(NetId);
    }

    private void Common_OnFireboxUpdate(float _)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return;

        fireboxDirty = true;
    }

    private void Common_OnPortUpdated(Port port)
    {
        if (UnloadWatcher.isUnloading || NetworkLifecycle.Instance.IsProcessingPacket)
            return;
        if (float.IsNaN(port.prevValue) && float.IsNaN(port.Value))
            return;
        if (lastSentPortValues.TryGetValue(port.id, out float value) && Mathf.Abs(value - port.Value) < 0.001f)
            return;
        dirtyPorts.Add(port.id);
    }

    private void Common_OnFuseUpdated(Fuse fuse)
    {
        if (UnloadWatcher.isUnloading || NetworkLifecycle.Instance.IsProcessingPacket)
            return;
        dirtyFuses.Add(fuse.id);
    }

    public void Common_UpdatePorts(CommonTrainPortsPacket packet)
    {
        if (!hasSimFlow)
            return;

        //string log = $"CommonTrainPortsPacket({TrainCar.ID})";
        for (int i = 0; i < packet.PortIds.Length; i++)
        {
            Port port = simulationFlow.fullPortIdToPort[packet.PortIds[i]];
            float value = packet.PortValues[i];
            float before = port.value;

            if (port.type == PortType.EXTERNAL_IN)
                port.ExternalValueUpdate(value);
            else
                port.Value = value;

            /*
            if (Multiplayer.Settings.DebugLogging)
                log += $"\r\n\tPort name: {port.id}, value before: {before}, value after: {port.value}, value: {value}, port type: {port.type}";)
            */
        }

        //NetworkLifecycle.Instance.Client.LogDebug(() => log);
    }

    public void Common_UpdateFuses(CommonTrainFusesPacket packet)
    {
        if (!hasSimFlow)
            return;

        for (int i = 0; i < packet.FuseIds.Length; i++)
            simulationFlow.fullFuseIdToFuse[packet.FuseIds[i]].ChangeState(packet.FuseValues[i]);
    }

    #endregion

    #region Client

    private IEnumerator Client_InitLater()
    {
        while ((client_bogie1Queue = bogie1.GetComponent<NetworkedBogie>()) == null)
            yield return null;
        while ((client_bogie2Queue = bogie2.GetComponent<NetworkedBogie>()) == null)
            yield return null;

        Client_Initialized = true;
    }

    public void Client_ReceiveTrainPhysicsUpdate(in TrainsetMovementPart movementPart, uint tick)
    {
        if (!Client_Initialized)
            return;

        if (TrainCar.isEligibleForSleep)
            TrainCar.ForceOptimizationState(false);

        if (movementPart.typeFlag == TrainsetMovementPart.MovementType.RigidBody)
        {
            //Multiplayer.LogDebug(() => $"Client_ReceiveTrainPhysicsUpdate({TrainCar.ID}, {tick}): is RigidBody");
            TrainCar.Derail();
            TrainCar.stress.ResetTrainStress();
            Client_trainRigidbodyQueue.ReceiveSnapshot(movementPart.RigidbodySnapshot, tick);
        }
        else
        {
            //move the car to the correct position first - maybe?
            if (movementPart.typeFlag.HasFlag(TrainsetMovementPart.MovementType.Position))
            {
                TrainCar.transform.position = movementPart.Position + WorldMover.currentMove;
                TrainCar.transform.rotation = movementPart.Rotation;

                //clear the queues?
                Client_trainSpeedQueue.Clear();
                Client_trainRigidbodyQueue.Clear();
                client_bogie1Queue.Clear();
                client_bogie2Queue.Clear();

                TrainCar.stress.ResetTrainStress();
            }

            Client_trainSpeedQueue.ReceiveSnapshot(movementPart.Speed, tick);
            TrainCar.stress.slowBuildUpStress = movementPart.SlowBuildUpStress;
            client_bogie1Queue.ReceiveSnapshot(movementPart.Bogie1, tick);
            client_bogie2Queue.ReceiveSnapshot(movementPart.Bogie2, tick);

            
        }
    }

    public void Client_ReceiveBrakePressureUpdate(float mainReservoirPressure, float independentPipePressure, float brakePipePressure, float brakeCylinderPressure)
    {
        if (brakeSystem == null)
            return;

        if (!hasSimFlow)
            return;

        //B99 review need / mod brakeSystem.ForceIndependentPipePressure(independentPipePressure);
        //B99 review need / mod brakeSystem.ForceTargetIndBrakeCylinderPressure(brakeCylinderPressure);
        brakeSystem.SetMainReservoirPressure(mainReservoirPressure);

        brakeSystem.brakePipePressure = brakePipePressure;
        brakeSystem.brakeCylinderPressure = brakeCylinderPressure;
    }
    private void Client_OnAddCoal(float coalMassDelta)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return;

        if (coalMassDelta <= 0)
            return;

        NetworkLifecycle.Instance.Client.LogDebug(() => $"Common_OnAddCoal({TrainCar.ID}): coalMassDelta: {coalMassDelta}");
        NetworkLifecycle.Instance.Client.SendAddCoal(NetId, coalMassDelta);
    }

    private void Client_OnIgnite(float ignition)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return;

        if (ignition == 0f)
            return;

        NetworkLifecycle.Instance.Client.LogDebug(() => $"Common_OnIgnite({TrainCar.ID})");
        NetworkLifecycle.Instance.Client.SendFireboxIgnition(NetId);
    }

    public void Client_ReceiveFireboxStateUpdate(float fireboxContents, bool isOn)
    {
        if (firebox == null)
            return;

        if (!hasSimFlow)
            return;

        firebox.fireboxContentsPort.Value = fireboxContents;
        firebox.fireOnPort.Value = isOn ? 1f : 0f;
    }
    #endregion
}
