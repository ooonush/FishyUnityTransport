# FishyUnityTransport

A UnityTransport implementation for Fish-Net.

If you have further questions, come find me as `ooonush` in the **[FirstGearGames Discord](https://discord.gg/Ta9HgDh4Hj)**!

The FishyUnityTransport library API is close to **[UnityTransport for NGO](https://github.com/Unity-Technologies/com.unity.netcode.gameobjects/tree/develop/com.unity.netcode.gameobjects/Runtime/Transports/UTP)** and uses some of its code.

## Support the Developer

**If you want to support the developer, you can do so with [Donation Alerts](https://www.donationalerts.com/r/ooonush).**

## Setting Up
1. Install Fish-Net from the **[official repo](https://github.com/FirstGearGames/FishNet/releases)** or **[Asset Store](https://assetstore.unity.com/packages/tools/network/fish-net-networking-evolved-207815)**.
2. Install **[UnityTransport](https://docs-multiplayer.unity3d.com/transport/current/install)** package  (1.3.1 or newer).
3. Install `FishyUnityTransport` **[unitypackage](https://github.com/ooonush/FishyUnityTransport/releases)** from the release section or using the Git URL:
   ```
   https://github.com/ooonush/FishyUnityTransport.git?path=Assets/FishNet/Plugins/FishyUnityTransport
   ```
4. Add `FishyUnityTransport` component to `NetworkManager` GameObject.
5. Set `Transport` variable on the `TransportManager` component to `FishyUnityTransport`.

## Relay

With `FishyUnityTransport`, you can use an IP address and port to connect to a `Dedicated Server`.
However, if you want to use a player device as a server (host), using an IP address to establish a connection does not always work.
For example, the device's firewall may prohibit these kinds of connections because they are not secure.
Instead, use Unity Relay to successfully initiate a connection between multiple clients and a host.

Many factors impact how you connect to the remote host. To connect to a remote host, use one of the following methods:
- Perform a [NAT punchthrough](https://docs-multiplayer.unity3d.com/netcode/current/learn/listen-server-host-architecture/#option-c-nat-punchthrough): This advanced technique directly connects to the host computer, even if it's on another network.
- Use a [Relay server](https://docs.unity.com/relay/en/manual/relay-servers): A Relay server exists on the internet with a public-facing IP that you and the host can access.
After the client and the host connect to a relay server, they can send data to each other through the Relay server.

FishNet doesn't offer tools to help you punch through a NAT.
However, you can use the Relay service provided by Unity Services.

## Unity Relay support

`FishyUnityTransport` supports [Unity Relay](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction), 
and since its API is similar to the `UnityTransport` for [NGO API](https://docs-multiplayer.unity3d.com/netcode/current/about/index.html),

### Installation and configuration

Use the Unity package manager to install the Relay package:
- `com.unity.services.relay`

You must first configure your Unity project for Unity before using Relay with FishNet.
Check out [Get started with Relay](https://docs.unity.com/ugs/en-us/manual/relay/manual/get-started) to learn how to link your project in the project settings.

`Note: The samples in this document use Unity Transport 1.3.4 on Unity 2022.3`

### How to use Relay

To access a Relay server, do the following:
- As the host, request an allocation on the relay server.
- As a client, use the [join code](https://docs.unity.com/relay/en/manual/join-codes) that the host creates to connect to the relay server. This code allows the host and clients to communicate through the Relay server without disclosing their IP addresses and ports directly.

### Create allocation on a Relay server

To create an [allocation](https://docs.unity.com/relay/en/manual/allocations) on a Relay server, make an authenticated call to the Unity backend using the SDK.
To do this, call the `CreateAllocationAsync` method on the host with the maximum number of expected peers. For example, a host that requests a maximum of three peer connections reserves four slots for a four player game. This function can throw exceptions, which you can catch to learn about the error that caused them.

```csharp
//Ask Unity Services to allocate a Relay server that will handle up to eight players: seven peers and the host.
Allocation allocation = await Unity.Services.Relay.RelayService.Instance.CreateAllocationAsync(7);
```

The `Allocation` class represents all the necessary data for a [host player](https://docs.unity.com/relay/manual/players#Host) to start hosting using the specific Relay server allocated. You don't need to understand each part of this allocation; you only need to feed them to your chosen transport that handles the Relay communication on its own. For the more curious (and for reference), here's a simple overview of those parameters:

- A `RelayServer` class containing the IP and port of your allocation's server.
- The allocation ID in a Base64 form and GUID form referred to as `AllocationIdBytes` and `AllocationId`.
- A blob of encrypted bytes representing the [connection data](https://docs.unity.com/relay/en/manual/connection-data) (known as `ConnectionData`) allows users to connect to this host.
- A Base64 encoded `Key` for message signature.

Each allocation creates a unique [join code](https://docs.unity.com/relay/en/manual/join-codes) suitable for sharing over instant messages or other means.
This join code allows your clients to join your game. You can retrieve it by calling the Relay SDK like so:

```csharp
string joinCode = await Unity.Services.Relay.RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
```

With those two calls, you now have your Relay allocation ready and the associated join code. Pass the allocation parameters to your host transport and send the join code (a simple string) over the Internet by the mean of your choice to your clients.


Remember to [authenticate](https://docs.unity.com/relay/en/manual/authentication) your users before using SDK methods.
The easiest way is the anonymous one (shown in the following code snippet), but you can use more advanced techniques.

```csharp
//Initialize the Unity Services engine
await UnityServices.InitializeAsync();
if (!AuthenticationService.Instance.IsSignedIn)
{
    //If not already logged, log the user in
    await AuthenticationService.Instance.SignInAnonymouslyAsync();
}
```

### Join an allocation on a Relay server

To join an allocation on a Relay server, the following must be true:

- The host of the game has created a Relay allocation.
- The client has received a join code.

To join a relay, a client requests all the allocation parameters from the join code to join the game.
To do this, use the join code to call the `JoinAllocationAsync` method as the following code snippet demonstrates:

```csharp
//Ask Unity Services to join a Relay allocation based on our join code
JoinAllocation allocation = await Unity.Services.Relay.RelayService.Instance.JoinAllocationAsync(joinCode);
```

For more information about the join code connection process, refer to [Connection flow](https://docs.unity.com/relay/manual/connection-flow#4).

### Pass allocation data to a transport component

When an allocation exists, you need to make all traffic that comes from FishNet go through the Relay.
To do this, perform the following actions to pass the allocation parameters to FishyUnityTransport:

1. Retrieve FishyUnityTransport from your `NetworkManager`:
```csharp
//Retrieve the FishyUnityTransport used by the NetworkManager
FishyUnityTransport transport = NetworkManager.TransportManager.GetTransport<FishyUnityTransport>();
```

2. Call the `SetRelayServerData` method on the retrieved transport by passing the allocation data that you retrieved, as well as the connection type (here set to [dtls](https://docs.unity.com/relay/en/manual/dtls-encryption)). For example:

```csharp
transport.SetRelayServerData(new RelayServerData(allocation, connectionType:"dtls"));
```

3. Call `NetworkManager.ServerManager.StartConnection()` to start server or `NetworkManager.ClientManager.StartConnection()` to start client.

### Code samples

Use the following code to work with the [Relay server](https://docs.unity.com/relay/en/manual/relay-servers).
For more information, refer to [Unity Relay](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction).

#### Host player

The following function, `StartHostWithRelay`, shows how to use the Relay SDK to create an `allocation`, request a `join code`,
and configure the connection type as [DTLS](https://docs.unity.com/ugs/en-us/manual/relay/manual/dtls-encryption).

```csharp
[SerializeField] private NetworkManager _networkManager;

/// <summary>
/// Starts a game host with a relay allocation: it initializes the Unity services, signs in anonymously and starts the host with a new relay allocation.
/// </summary>
/// <param name="maxConnections">Maximum number of connections to the created relay.</param>
/// <returns>The join code that a client can use.</returns>
/// <exception cref="ServicesInitializationException"> Exception when there's an error during services initialization </exception>
/// <exception cref="UnityProjectNotLinkedException"> Exception when the project is not linked to a cloud project id </exception>
/// <exception cref="CircularDependencyException"> Exception when two registered <see cref="IInitializablePackage"/> depend on the other </exception>
/// <exception cref="AuthenticationException"> The task fails with the exception when the task cannot complete successfully due to Authentication specific errors. </exception>
/// <exception cref="RequestFailedException"> See <see cref="IAuthenticationService.SignInAnonymouslyAsync"/></exception>
/// <exception cref="ArgumentException">Thrown when the maxConnections argument fails validation in Relay Service SDK.</exception>
/// <exception cref="RelayServiceException">Thrown when the request successfully reach the Relay Allocation service but results in an error.</exception>
/// <exception cref="ArgumentNullException">Thrown when the UnityTransport component cannot be found.</exception>
public async Task<string> StartHostWithRelay(int maxConnections = 5)
{
    //Initialize the Unity Services engine
    await UnityServices.InitializeAsync();
    //Always authenticate your users beforehand
    if (!AuthenticationService.Instance.IsSignedIn)
    {
        //If not already logged, log the user in
        await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    // Request allocation and join code
    Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
    var joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
    // Configure transport
    var unityTransport = _networkManager.TransportManager.GetTransport<FishyUnityTransport>();
    unityTransport.SetRelayServerData(new RelayServerData(allocation, "dtls"));

    // Start host
    if (_networkManager.ServerManager.StartConnection()) // Server is successfully started.
    {
        _networkManager.ClientManager.StartConnection();
        return joinCode;
    }
    return null;
}
```

This code should be adapted to your needs to use a different authentication mechanism, a different error handling, or to use a different connection type.
Similarly, you can start only the server instead of the host, to start a server instead of a host.

#### Joining player

When your game client functions as a joining player, the relay join code must be passed to find the allocation.
The following code sample shows how to use the Relay SDK to join an allocation with a join code and configure the connection type as [DTLS](https://docs.unity.com/ugs/en-us/manual/relay/manual/dtls-encryption).

```csharp
[SerializeField] private NetworkManager _networkManager;

/// <summary>
/// Joins a game with relay: it will initialize the Unity services, sign in anonymously, join the relay with the given join code and start the client.
/// </summary>
/// <param name="joinCode">The join code of the allocation</param>
/// <returns>True if starting the client was successful</returns>
/// <exception cref="ServicesInitializationException"> Exception when there's an error during services initialization </exception>
/// <exception cref="UnityProjectNotLinkedException"> Exception when the project is not linked to a cloud project id </exception>
/// <exception cref="CircularDependencyException"> Exception when two registered <see cref="IInitializablePackage"/> depend on the other </exception>
/// <exception cref="AuthenticationException"> The task fails with the exception when the task cannot complete successfully due to Authentication specific errors. </exception>
/// <exception cref="RequestFailedException">Thrown when the request does not reach the Relay Allocation service.</exception>
/// <exception cref="ArgumentException">Thrown if the joinCode has the wrong format.</exception>
/// <exception cref="RelayServiceException">Thrown when the request successfully reach the Relay Allocation service but results in an error.</exception>
/// <exception cref="ArgumentNullException">Thrown when the UnityTransport component cannot be found.</exception>
public async Task<bool> StartClientWithRelay(string joinCode)
{
    //Initialize the Unity Services engine
    await UnityServices.InitializeAsync();
    //Always authenticate your users beforehand
    if (!AuthenticationService.Instance.IsSignedIn)
    {
        //If not already logged, log the user in
        await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    // Join allocation
    var joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode: joinCode);
    // Configure transport
    var unityTransport = _networkManager.TransportManager.GetTransport<FishyUnityTransport>();
    unityTransport.SetRelayServerData(new RelayServerData(joinAllocation, "dtls"));
    // Start client
    return !string.IsNullOrEmpty(joinCode) && _networkManager.ClientManager.StartConnection();
}
```

### Note about WebGL support

To use Relay with FishNet in `WebGL`, you must upgrade the Unity Transport Package to 2.0.0 or later and configure the `FishyUnityTransport` component to use `Web Sockets`.

```csharp
var unityTransport = _networkManager.TransportManager.GetTransport<FishyUnityTransport>();
unityTransport.SetRelayServerData(new RelayServerData(allocation, "wss"));
unityTransport.UseWebSockets = true;
```

## Other Unity Services support

In addition, `FishyUnityTransport` supports other Unity Services. You can explore them yourself and use them in your project:
- [Unity Lobby](https://docs.unity.com/ugs/en-us/manual/lobby/manual/unity-lobby-service)
- [Game Server Hosting (Multiplay)](https://docs.unity.com/ugs/en-us/manual/game-server-hosting/manual/welcome)