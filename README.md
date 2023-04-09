
# FishyUnityTransport

A UnityTransport implementation for Fish-Net.

Important: this is under development and may not be stable, it is not recommended to use it in production.

	This is an improved fork from https://github.com/matthewshirley/FishyUTP.

If you have further questions, come find me as alv#4329 in the **[FirstGearGames Discord](https://discord.gg/Ta9HgDh4Hj)**!

The FishyUnityTransport library API is close to **[UnityTransport for NGO](https://github.com/Unity-Technologies/com.unity.netcode.gameobjects/tree/develop/com.unity.netcode.gameobjects/Runtime/Transports/UTP)** and uses some of its code.

## Dependencies
Make sure you have the following packages installed:
1. **[UnityTransport](https://docs-multiplayer.unity3d.com/transport/current/install)**
2. **[Fish-Net](https://github.com/FirstGearGames/FishNet)**

## Setting Up
1. Install Fish-Net from the **[official repo](https://github.com/FirstGearGames/FishNet/releases)** or **[Asset Store](https://assetstore.unity.com/packages/tools/network/fish-net-networking-evolved-207815)**.
2. Install **[UnityTransport](https://docs-multiplayer.unity3d.com/transport/current/install)** package.
3. Install FishyUnityTransport **[unitypackage](https://github.com/ooonush/FishyUnityTransport/releases)** from the release section.
4. Add the **"FishyUnityTransport"** component to your **"NetworkManager"**.

## Unity Relay support
This library supports Unity Relay, and since its API is similar to the UnityTransport for NGO API, I recommend reading the official Relay **[documentation](https://docs.unity.com/relay/en/manual/relay-and-ngo)**.

To setup Unity Relay with Fishy Unity Transport follow these steps :
1. Complete all steps above from `Setting Up`.
2. Make sure you have installed and set up **Unity Authentication Service**, which is required to use the Relay services. **[AuthenticationService](https://docs.unity.com/authentication/en/manual/sign-up-for-ugs)**
3. Make sure you are signing-in when entering Play Mode with the authentication service before using Unity Relay.
4. Set the `ProtocoleType` field of the `FishyUnityTransport` component to `Relay Unity Transport`.
5. Add the `FishyUnityTransportRelayManager` component to your `NetworkManager`.
6. Call one of the three method of `FishyUnityTransportRelayManager` : `StartHostWithRelay()`, `StartClientWithRelay(string joinCode)`, `StartServerWithRelay()`.


### Simple host connection sample:
```csharp
// Get FishyUnityTransport
var utp = (FishyUnityTransport)NetworkManager.TransportManager.Transport;

// Setup HostAllocation
Allocation hostAllocation = await RelayService.Instance.CreateAllocationAsync(4);
utp.SetRelayServerData(new RelayServerData(hostAllocation, "dtls"));
  
// Start Server Connection
NetworkManager.ServerManager.StartConnection();
  
// Setup JoinAllocation
// Remarks: It will currently work, but with a nasty bug (https://github.com/ooonush/FishyUnityTransport/issues/4).
// This will be reworked in a future version.
string joinCode = await RelayService.Instance.GetJoinCodeAsync(hostAllocation.AllocationId);
JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
utp.SetRelayServerData(new RelayServerData(joinAllocation, "dtls"));

// Start Client Connection
NetworkManager.ClientManager.StartConnection();
```
