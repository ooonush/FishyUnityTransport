# FishyUnityTransport

A UnityTransport implementation for Fish-Net.

If you have further questions, come find me as `ooonush` in the **[FirstGearGames Discord](https://discord.gg/Ta9HgDh4Hj)**!

The FishyUnityTransport library API is close to **[UnityTransport for NGO](https://github.com/Unity-Technologies/com.unity.netcode.gameobjects/tree/develop/com.unity.netcode.gameobjects/Runtime/Transports/UTP)** and uses some of its code.

## Dependencies
Make sure you have the following packages installed:
1. **[UnityTransport](https://docs-multiplayer.unity3d.com/transport/current/install) 1.3.1 or newer**
2. **[Fish-Net](https://github.com/FirstGearGames/FishNet)**

## Setting Up
1. Install Fish-Net from the **[official repo](https://github.com/FirstGearGames/FishNet/releases)** or **[Asset Store](https://assetstore.unity.com/packages/tools/network/fish-net-networking-evolved-207815)**.
2. Install **[UnityTransport](https://docs-multiplayer.unity3d.com/transport/current/install)** package.
3. Install FishyUnityTransport **[unitypackage](https://github.com/ooonush/FishyUnityTransport/releases)** from the release section or using the Git URL:

   ```
   https://github.com/ooonush/FishyUnityTransport.git?path=Assets/FishNet/Plugins/FishyUnityTransport
   ```

4. Add the **"FishyUnityTransport"** component to your **"NetworkManager"**.

## Unity Relay support
This library supports Unity Relay, and since its API is similar to the UnityTransport for NGO API,
I recommend reading the official Relay **[documentation](https://docs.unity.com/relay/en/manual/relay-and-ngo)**.

I also recommend you read the **[Relay section](https://docs-multiplayer.unity3d.com/netcode/current/relay/)** in NGO docs.

### Key differences. Simple host connection sample:
```csharp
[SerializeField] private NetworkManager _networkManager;

public async Task StartHost()
{
    var utp = (FishyUnityTransport)_networkManager.TransportManager.Transport;

    // Setup HostAllocation
    Allocation hostAllocation = await RelayService.Instance.CreateAllocationAsync(4);
    utp.SetRelayServerData(new RelayServerData(hostAllocation, "dtls"));

    // Start Server Connection
    _networkManager.ServerManager.StartConnection();
    // Start Client Connection
    _networkManager.ClientManager.StartConnection();
}

public async Task StartClient(string joinCode)
{
    JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
    utp.SetRelayServerData(new RelayServerData(joinAllocation, "dtls"));

    // Start Client Connection
    _networkManager.ClientManager.StartConnection();
}
```