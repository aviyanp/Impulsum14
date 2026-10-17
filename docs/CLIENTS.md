# Connecting clients

## Local PC

1. Build/publish the backend in Release. Keep `certs`, `FUTDB`, `web`, and the managed DLLs beside `Impulsum14.dll`.
2. Run the default loopback server (42127 redirector, 10000 Blaze, 9988 HTTP).
3. Use the repository's existing launcher setup with your installed FIFA 14 `Game` directory. Its hosts redirector maps `gosredirector.ea.com` to loopback and it supplies its existing Cards/POW integration. Do not run two backend instances on those ports.
4. Check `/healthz`, then the actual game: login → FUT club → squad → tournament/season match → post-match → leave/re-enter → restart. Synthetic checks do not replace this acceptance pass.

The existing launcher is Windows-only and may start its own bundled server. For a separately published backend or bridge, disable that automatic start/use its client setup and then start FIFA through your existing installation. This branch does not rebuild or modify the launcher's binary client patches. Merely resolving the redirector hostname does not remove the game's certificate, activation or client-DLL requirements.

## Remote PC through the loopback bridge

The server operator provisions a private account token and provides a reachable web origin and Blaze host. On the client computer, keep local/default backend ports, remove any hosted `FIFA14_CONFIG`, and start the same server binary in bridge mode. PowerShell example:

```powershell
$env:FIFA14_UPSTREAM_WEB_URL = 'https://fut.example.com'
$env:FIFA14_UPSTREAM_BLAZE_HOST = '10.8.0.1' # reachable private/VPN host
$env:FIFA14_UPSTREAM_BLAZE_PORT = '10000'
$env:FIFA14_UPSTREAM_TOKEN_FILE = 'C:\FUT14\player.token'
dotnet .\Impulsum14.dll --bridge
```

Put only your private-server token in `player.token`. Keep it outside the game folder. Start the existing client setup against loopback. The bridge:

- answers the local legacy redirector;
- relays Fire/Heat2 Blaze packets and login notifications;
- replaces private-server login credentials on outbound authentication requests;
- rewrites the upstream web origin in fetched client configuration to the local HTTP origin;
- injects server authorization into proxied HTTP requests.

This path is tested with actual HTTP/Blaze sockets and SDK fixtures, including identity and URL rewrites. It is **not a successful run of FIFA 14 itself** in this development environment. The game may have additional hard-coded services for a particular executable/DLL revision. Capture the failing method/component or HTTP path during a client acceptance run before adding a compatibility shim.

The bridge uses default PC Fire + Heat2. It accepts only loopback clients. It does not proxy peer gameplay or make unreachable peers reachable. Its upstream Blaze connection must use your private network/VPN; HTTP can use HTTPS independently.

## Two-player ranked PC testing

Peer lobbies default to disabled. Set `EnablePeerLobbies=true` in the server JSON and use two distinct provisioned accounts (`LocalMode=false`); the single default loopback identity cannot be its own opponent. Both clients need the same protocol-version string and game mode. A Blaze `StartMatchmaking` request enters the Elo-backed pool only when `CriteriaData.RankedGameRulePrefs.DesiredRankedGameValue` is `RANKED`; direct create/join lobbies are not ranked. See [RANKED.md](RANKED.md) for configuration, report endpoints, Elo, online seasons and disconnect policy.

The server binds the pair and exchanges setup/address notifications; neither the backend nor bridge relays football gameplay. Configure reachable peer networking independently. Corroborated inverse score reports or an explicit self-forfeit settle the server receipt. An abrupt gameplay disconnect is not proof of a loss: the receipt remains unresolved until a valid result/concession or timeout cancellation.

Native match-end waits briefly for the peer (2500 ms by default), then can return `settlementPending` without a reward if the peer is late. A later account transaction applies cumulative settled earnings. Verify that the exact retail executable/DLL set displays the eventual balance and both players' results correctly; no successful retail PC-vs-PC match or native pending-result UI acceptance is claimed here. Record both client hashes and test both report orders, quits, lost connections, conflicts, leave/re-enter, restart, and a full ten-game online season separately from offline progression.

## Legends

Stock PC clients do not have all 42 Legends identity records. Their server metadata alone cannot fix a missing client database row, portrait or model. Legends stay disabled by default.

If your installed client data already supports the identities, export its player table to CSV/TSV and run:

```sh
python3 tools/legend_manifest.py exported-players.tsv --output legend-client.json
```

Set `EnableLegends=true` and `LegendClientManifest` to the manifest path; tune `LegendChance`. The server checks all identities at startup. The manifest is evidence about the supplied export, not remote attestation of every connecting client. Use a homogeneous compatible client build. The tool does not patch or distribute game files. Test squad loading, pack presentation and a match in the actual client before enabling them for a community.

## Consoles

PS3/Xbox 360/PS4/Xbox One are **unsupported and untested** here. Console platform authentication, title updates, certificate handling, service discovery and peer networking differ from the PC path. No console-specific authentication implementation, executable patch or retail-console connection is claimed. A DNS redirect alone does not provide compatibility. The backend's configurable network addresses can support future adapters once platform-specific captures and valid client testing exist.
