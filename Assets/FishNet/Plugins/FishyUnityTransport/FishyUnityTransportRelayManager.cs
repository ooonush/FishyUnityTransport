using System;
using System.Threading.Tasks;
using FishNet.Managing;
using FishNet.Transporting.FishyUnityTransport;
using Unity.Networking.Transport.Relay;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

public class FishyUnityTransportRelayManager : MonoBehaviour
{
    public int MaxConnections = 10;
    [SerializeField] private string _currentRelayJoinCode;
    [SerializeField] private bool _log;
    
    private NetworkManager _networkManager;
    
    private void Awake()
    {
        _networkManager = FindObjectOfType<NetworkManager>();
        if (_networkManager == null)
        {
            Debug.LogError("NetworkManager not found, cannot start relay server.");
        }
    }
    
    public async void StartServerWithRelay()
    {
        await AllocateRelayServerAndGetJoinCode(MaxConnections);
    }
    
    public async void StartHostWithRelay()
    {
        await AllocateRelayServerAndGetJoinCode(MaxConnections);
        await JoinRelayServerFromJoinCode(_currentRelayJoinCode);
    }

    public async void StartClientWithRelay(string joinCode)
    {
        await JoinRelayServerFromJoinCode(joinCode);
    }

    private async Task<RelayServerData> AllocateRelayServerAndGetJoinCode(int maxConnections, string region = null)
    {
        if(_log) Debug.Log("Relay server : creating allocation...");
        RelayServerData relayServerData;
        Allocation currentAllocation;
        try
        {
            // Setup HostAllocation
            currentAllocation = await RelayService.Instance.CreateAllocationAsync(maxConnections, region);
            // Get FishyUnityTransport
            var fishyUnityTransport = (FishyUnityTransport)_networkManager.TransportManager.Transport;
            
            relayServerData = new RelayServerData(currentAllocation, "dtls");
            fishyUnityTransport.SetRelayServerData(relayServerData);
            
            // Start Server Connection
            _networkManager.ServerManager.StartConnection();
            
            if(_log) Debug.Log("Relay server : allocation successfully created");
        }
        catch (Exception e)
        {
            Debug.LogError($"Relay server : create allocation request failed {e.Message}");
            throw;
        }

        if(_log) Debug.Log($"Relay server : {currentAllocation.ConnectionData[0]} {currentAllocation.ConnectionData[1]}");
        if(_log) Debug.Log($"Relay server : {currentAllocation.AllocationId}");

        if (_log) Debug.Log("Relay server : getting Join Code...");
        
        try
        {
            _currentRelayJoinCode = await RelayService.Instance.GetJoinCodeAsync(currentAllocation.AllocationId);
            if (_log) Debug.Log("Relay server : join code -> " + _currentRelayJoinCode);
            // If you want to update the lobby metadata with the join code, you can do it here.
        }
        catch
        {
            Debug.LogError("Relay server : join code request failed");
            throw;
        }

        return relayServerData;
    }

    private async Task<RelayServerData> JoinRelayServerFromJoinCode(string joinCode)
    {
        if(_log) Debug.Log("Relay server : joining Relay Server...");
        JoinAllocation joinAllocation;
        
        try
        {
            joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
            var fishyUnityTransport = (FishyUnityTransport)_networkManager.TransportManager.Transport;
            fishyUnityTransport.SetRelayServerData(new RelayServerData(joinAllocation, "dtls"));
            _networkManager.ClientManager.StartConnection();
            if(_log) Debug.Log("Relay server : joined Relay Server");
        }
        catch
        {
            Debug.LogError("Relay server : attempt to join Relay Server failed");
            throw;
        }

        return new RelayServerData(joinAllocation, "dtls");
    }
}
