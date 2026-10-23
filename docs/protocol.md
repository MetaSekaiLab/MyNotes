# Protocol behavior

The server exposes unary Protocol Buffer RPCs over HTTP/2. A request uses `application/grpc` or
`application/grpc+proto` and exactly one five-byte-prefixed message frame. The maximum message size is 16 MiB.
Responses use HTTP 200 and gRPC status headers or trailers. Malformed messages return `INVALID_ARGUMENT`, oversized
messages return `RESOURCE_EXHAUSTED`, and unsupported compression or RPCs return `UNIMPLEMENTED`.
`Server:CertificatePath` and `Server:CertificatePassword` enable TLS on the gRPC and HTTP listeners with a local
PFX certificate. An empty certificate path uses plaintext HTTP/2 on `GrpcPort` and HTTP/1.1 on `HttpPort`.
The PFX can contain the leaf certificate, intermediate certificates, and root needed to build its chain.
Start with `dotnet run -- --config settings.json` to load a separate JSON configuration file. Environment
variables and command-line values take precedence over that file.

## Player identity

Registration returns a player credential. Player operations accept that credential as a raw authorization value
or `Bearer <credential>`. Clients can also send `x-player-credential` with the matching `x-player-id`.
Duplicate or conflicting identity headers are rejected. A valid configured Basic authorization can select an
existing player with `x-player-id`.
Missing or invalid player credentials return `UNAUTHENTICATED`. When `Game:RequireBasicAuth` is enabled, every RPC
requires the configured Basic authorization. Authorization logs contain scheme labels only.

`PlayerLoginService/GetServerList` reports the configured `Login:Server` fields. `PlayerPreLogin` and
`PlayerLogin` authenticate `sdkUid` and `sdkAccessToken` against `Login:Accounts`. Each account contains
`Uid`, `AccessTokenSha256` (the SHA-256 digest of the UTF-8 token), and `InitialDataGroup`. Login returns a
stable player credential for that account during the server process. An empty account list disables login.
The server validates locally configured tokens; the account gateway is configured separately. Store account
configuration outside the repository and use high-entropy tokens.

Player data includes initialized memory, VIP, tutorial, and personal-notification sections, plus an account ID
that matches the player's profile ID. Home replies include initialized personal notifications. Empty sections
represent their default state.

`PlayerExtService/SyncData` reports the player's account ID with initialized gem and live-boost sections in
their default state. `SeasonPassService/Fetch` returns the default season-pass state with no passes and no
mission counters. `ClientChatService/SyncMsg` and `ClientChatService/GetNotifications` return the default chat
state with no notifications, and `LiveService/FetchBattleLiveInvitation` returns no invitations.

`FeatureSwitchService/Get` returns the boolean map configured under `FeatureSwitches` for an authenticated
player. The default map is empty. Invalid configuration values prevent startup.

## Free live

`StartFree` validates the song difficulty, deck, and boost range and records one active free live per player. A
subsequent start changes the active selection. `FinishFree` consumes that context and records the best score,
maximum combo, and highest clear state independently. `GetPlayerData` projects these records back to the client.
Results from another player or without an active live return `FAILED_PRECONDITION`.

Finish replies contain an initialized notification message. Rewards and boost accounting are not implemented by
this free-live service. `CheckMaintenanceFree` reports no maintenance, and `SkipFree` returns `UNIMPLEMENTED`.
Player state, including scores, is held for the current server process and resets on restart.

## Checks

```sh
dotnet test tests/MyNotes.Tests/MyNotes.Tests.csproj
dotnet publish MyNotes.csproj -c Release -o publish
```

Tests create synthetic resources and protobuf requests in temporary directories. They cover framing, credentials,
local downloads and ranges, upstream caching and timeouts, release validation, explicit score relationships, and
free-live result projection. Symbolic-link checks run when the test host can create links.
