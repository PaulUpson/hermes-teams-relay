# Hermes Agent — Teams messaging via Azure Relay (no dev tunnel)

Replaces the dev-tunnel step in Hermes's [Microsoft Teams setup](https://hermes-agent.nousresearch.com/docs/user-guide/messaging/teams)
with an Azure Relay Hybrid Connection. Bot Framework never talks to anything
inside your network directly, and nothing inbound is opened on your
firewall — the on-prem side only ever makes outbound connections.

## Architecture

``` mermaid
flowchart TB
    User["Teams user"]

    subgraph MSFT["Microsoft cloud"]
        BF["Bot Framework<br/>(Teams channel)"]
        Func["Azure Function<br/>/api/messages<br/>(public HTTPS endpoint)"]
        Relay["Azure Relay<br/>Hybrid Connection"]
    end

    subgraph CORP["Corporate network — outbound only, no inbound firewall ports"]
        Listener["Relay listener<br/>.NET console app<br/>(dotnet run)"]
        Gateway["Hermes gateway<br/>hermes gateway run<br/>localhost:3978/api/messages"]
        LLM["LLM provider<br/>e.g. GitHub Copilot / Anthropic"]
    end

    User -->|"sends message"| BF
    BF -->|"HTTPS POST<br/>Authorization: Bot Framework JWT"| Func
    Func -->|"forwards request unchanged<br/>+ ServiceBusAuthorization SAS token"| Relay
    Listener -.->|"1. opens outbound control channel<br/>(Listen-scoped SAS key)"| Relay
    Relay -->|"2. relays the request over<br/>that existing channel"| Listener
    Listener -->|"plain HTTP POST"| Gateway
    Gateway <--> LLM
    Gateway -->|"reply sent directly via Bot Connector API<br/>(bypasses Function + Relay entirely)"| BF
    BF -->|"delivers reply"| User

    classDef msft fill:#e8f0fe,stroke:#4285f4,color:#1a1a1a;
    classDef corp fill:#eafbea,stroke:#34a853,color:#1a1a1a;
    class BF,Func,Relay msft;
    class Listener,Gateway,LLM corp;
```

<!-- ```
Microsoft Teams / Bot Framework
        │  HTTPS POST (Bot Framework JWT in Authorization header)
        ▼
Azure Function  (public HTTPS endpoint registered as the bot's
                  messaging endpoint: /api/messages)
        │  forwards the request unchanged, adds a Send-scoped
        │  SAS token in a separate "ServiceBusAuthorization" header
        ▼
Azure Relay Hybrid Connection  (namespace + hybrid connection;
                                 the piece that replaces the dev tunnel)
        │  relayed over the control channel the on-prem
        │  listener already opened outbound
        ▼
On-prem relay listener  (native process, outbound-only,
                          Microsoft.Azure.Relay HybridConnectionListener)
        │  plain HTTP POST to Hermes's webhook port
        ▼
Hermes gateway  (http://localhost:3978/api/messages)
``` -->

Two separate authentication layers stay intact end to end:

- **Bot Framework JWT** (`Authorization` header) — passed through untouched by
  the Function and the listener. Hermes's Teams adapter validates it exactly
  as it would for a dev tunnel or a real domain; neither relay hop inspects it.
- **Relay SAS token** (`ServiceBusAuthorization` header) — authenticates the
  Function to the Hybrid Connection so only your Function can push traffic
  through it. Scoped to "Send" only; the listener side uses a separate
  "Listen"-only key. Neither key can do the other's job.

Replies from Hermes back to Teams do **not** travel back through this relay
chain: Bot Framework's normal pattern is for the bot to send its reply as a
direct outbound call to the conversation's `serviceUrl` using the Bot
Connector REST API, which Hermes already does on its own outbound internet
access. This relay only carries the inbound "a message arrived" webhook call,
which keeps payloads small and avoids round-tripping the actual reply through
Azure Relay's tighter limits (see Known limits below).

The bot is registered through the **Azure Portal**, not the Teams CLI —
`teams app create` calls Microsoft Graph to create an Entra ID app
registration, which many corporate tenants block for self-service users.
Creating an Azure Bot resource in the Portal only needs Azure RBAC
Contributor on a resource group, which you already have from deploying the
Function and Relay. The resulting manifest is sideloaded into Teams manually.

The listener runs as a **plain native process** next to Hermes (`dotnet run`),
not in Docker — this assumes Hermes's own gateway also runs natively
(`hermes gateway run`), which is the common case on a Windows install.

## What's in this folder

| Path | Purpose |
| --- | --- |
| `infra/main.bicep` | Provisions the Relay namespace, Hybrid Connection, Listen/Send SAS rules, storage account, Application Insights, and the Function App. |
| `function/HermesTeamsRelayFunction/` | The Azure Function (.NET 8 isolated worker) that Bot Framework calls directly. |
| `listener/HermesTeamsRelayListener/` | The on-prem listener — a .NET Generic Host app. Run with `dotnet run` for a quick test, or install as a Windows Service for the long-term setup (step 5). |
| `teams-app/` | Manifest + placeholder icons for registering the bot via the Azure Portal + Teams sideload. |

## Setup

### 1. Provision Azure resources

```bash
az deployment group create \
  --resource-group <your-rg> \
  --template-file infra/main.bicep \
  --parameters namePrefix=hermesteams
```

Capture the outputs — you'll need `functionMessagingEndpoint`, `relayNamespaceHost`,
`hybridConnectionNameOutput`, and `functionAppName`.

The Bicep deliberately does **not** print the Listen-key secret to deployment
output (avoids it landing in CI logs). Fetch it separately:

```bash
az relay hyco authorization-rule keys list \
  --resource-group <your-rg> \
  --namespace-name hermesteams-relay \
  --hybrid-connection-name hermes-teams-messages \
  --name listen-only \
  --query primaryKey -o tsv
```

If you ever lose track of which resource group a resource landed in:

```bash
az resource list --name hermesteams-relay --query "[].resourceGroup" -o tsv
```

### 2. Deploy the Function code

```bash
cd function/HermesTeamsRelayFunction
func azure functionapp publish <functionAppName> --dotnet-isolated
```

`--dotnet-isolated` is worth passing explicitly — some Core Tools versions
fail to auto-detect the isolated-worker project shape ("Can't determine
project language from files") without it. Update Core Tools
(`npm install -g azure-functions-core-tools@4 --unsafe-perm true`) if it
still doesn't detect correctly.

The Send-key app settings are already wired up by the Bicep template —
nothing further to configure on the Function side.

### 3. Register the bot

Full walkthrough in `teams-app/README.md`. Summary:

1. Azure Portal → **Create a resource** → **Azure Bot**, same resource group
   as the Function/Relay resources. Pricing tier **F0**. Microsoft App ID:
   **Create new Microsoft App ID**, type **Multi Tenant** (or **Single
   Tenant** if your org requires it).
2. Resource → **Configuration**: copy the **Microsoft App ID**, then
   **Manage Password** → **New client secret** → copy the value immediately.
   Set **Messaging endpoint** to `<functionMessagingEndpoint>`.
3. Resource → **Channels** → **Microsoft Teams** → click through and
   **Apply**. Easy to skip by accident — if sideloading later fails with
   "Please make sure the bot is registered and Teams channel is enabled,"
   this is almost always why. Give it a minute or two to propagate.
4. Fill in `teams-app/manifest.json` (replace both `$MICROSOFT_APP_ID`
   placeholders with the Microsoft App ID from step 2), zip it with the
   icons in that folder, and sideload via Teams → **Apps** →
   **Manage your apps** → **Upload an app** → **Upload a custom app**.

Get your tenant ID and your own AAD object ID:

```bash
az account show --query tenantId -o tsv
az ad signed-in-user show --query id -o tsv
```

### 4. Configure Hermes's `.env`

Add alongside whatever LLM provider settings are already there:

```
TEAMS_CLIENT_ID=<Microsoft App ID>
TEAMS_CLIENT_SECRET=<client secret>
TEAMS_TENANT_ID=<tenant id>
TEAMS_ALLOWED_USERS=<your AAD object id>

RELAY_NAMESPACE=hermesteams-relay.servicebus.windows.net
RELAY_HC_NAME=hermes-teams-messages
RELAY_LISTEN_KEYNAME=listen-only
RELAY_LISTEN_KEY=<listen key from step 1>
```

Where this file lives depends on your install:

- Linux/macOS/WSL2 (git installer): `~/.hermes/.env`
- Native Windows install: `%LOCALAPPDATA%\hermes\.env` (i.e.
  `C:\Users\<you>\AppData\Local\hermes\.env`)

If you're not sure which applies, `hermes doctor` prints the detected paths.

### 5. Run the on-prem listener

The listener is a .NET Generic Host app, so the same binary runs either as a
quick console process (for the first test) or as an installed Windows
Service (for the long-term setup) — same code, no separate "service version."

**First, a quick manual test** to confirm it connects before installing anything:

```powershell
cd hermes-teams-relay\listener\HermesTeamsRelayListener
$env:RELAY_NAMESPACE="hermesteams-relay.servicebus.windows.net"
$env:RELAY_HC_NAME="hermes-teams-messages"
$env:RELAY_LISTEN_KEYNAME="listen-only"
$env:RELAY_LISTEN_KEY="<listen key>"
$env:HERMES_TARGET_URL="http://localhost:3978/api/messages"
dotnet run
```

(bash/zsh equivalent: `export VAR=value` instead of `$env:VAR=`.) Look for
`online — control channel established.` in the output — that confirms the
outbound connection to Azure Relay is up. `Ctrl+C` to stop it once confirmed.

**Then, install it as a Windows Service** so it survives reboots and doesn't
need a terminal window or a logged-in session. A service doesn't inherit
your PowerShell session's `$env:` variables, so set them machine-wide first
(one-time, needs an elevated/Administrator prompt):

```powershell
[Environment]::SetEnvironmentVariable("RELAY_NAMESPACE", "hermesteams-relay.servicebus.windows.net", "Machine")
[Environment]::SetEnvironmentVariable("RELAY_HC_NAME", "hermes-teams-messages", "Machine")
[Environment]::SetEnvironmentVariable("RELAY_LISTEN_KEYNAME", "listen-only", "Machine")
[Environment]::SetEnvironmentVariable("RELAY_LISTEN_KEY", "<listen key>", "Machine")
```

(Alternatively, skip machine env vars entirely and fill the same four values
directly into `appsettings.json` next to the published exe — whichever you
prefer keeping secrets in.)

Publish, then create and start the service (elevated prompt):

```powershell
dotnet publish -c Release -o C:\Services\HermesTeamsRelayListener

sc.exe create HermesTeamsRelayListener binPath= "C:\Services\HermesTeamsRelayListener\HermesTeamsRelayListener.exe" start= auto
sc.exe failure HermesTeamsRelayListener reset= 86400 actions= restart/5000/restart/5000/restart/5000
sc.exe start HermesTeamsRelayListener
```

(`binPath=` and the other `key= value` pairs need that literal space after
the `=` — that's `sc.exe`'s syntax, not a typo.) `sc.exe failure` makes it
auto-restart on crash instead of just staying dead. Check status any time
with `sc.exe query HermesTeamsRelayListener`, or via `services.msc` in the
GUI. Logs go to the Windows Event Log (Event Viewer → Windows Logs →
Application, source `HermesTeamsRelayListener`) instead of a console.

To remove it later: `sc.exe stop HermesTeamsRelayListener` then
`sc.exe delete HermesTeamsRelayListener`.

### 6. Start Hermes's gateway

```bash
hermes gateway run
```

### 7. Test end to end

Cheapest checks first, so you know which hop is broken if something's off:

```powershell
# 1. Hermes itself is alive
curl http://localhost:3978/health          # expect: ok

# 2. Full chain is reachable (Function -> Relay -> listener -> Hermes)
curl -i -X POST <functionMessagingEndpoint> -H "Content-Type: application/json" -d "{}"
# expect a 401 Unauthorized — that's Hermes correctly rejecting a request
# with no valid Bot Framework JWT. A timeout or 502 means the relay chain
# itself is broken, not auth.
```

Then the real test: open Teams, find "Hermes" under your apps, DM it
something like `hello`.

If step 2 looks fine but Teams doesn't respond:

- Check `TEAMS_ALLOWED_USERS` actually contains your AAD object ID — if it
  doesn't, Hermes silently drops the message with no error anywhere.
- Check the listener's console for an incoming request when you send the
  Teams message.
- Check Hermes's own console for errors right after.
- Check Function logs if you want Azure-side visibility:
  `az webapp log tail --name <functionAppName> --resource-group <your-rg>`.

If Teams *does* respond but with something like `⚠️ The model provider
failed after retries` — that's unrelated to this whole relay setup; it's
Hermes's LLM provider call failing. Check the gateway console for the raw
provider error, and run `hermes doctor` / `hermes model` to confirm your
provider auth and chosen model are still valid (model deprecations are a
common cause — e.g. a newly-released model version not yet supported by a
given provider integration).

## Security notes

- Rotate both SAS keys periodically (`az relay hyco authorization-rule keys renew`).
  Rotating the Send key only requires redeploying the Function's app settings;
  rotating the Listen key only requires restarting the listener with the new
  value.
- Microsoft's docs recommend Microsoft Entra ID auth for the Relay connection
  over SAS keys in production. This template uses SAS for simplicity; if your
  policy requires it, the Function and listener can both be switched to
  `TokenProvider.CreateAzureActiveDirectoryTokenProvider` with a system-assigned
  managed identity on the Function App side — ask if you want that variant.
- Compromising the relay path alone does not grant access to the bot: Hermes
  still independently validates the Bot Framework JWT, and `TEAMS_ALLOWED_USERS`
  still gates which AAD users the bot will act on.

## Known limits

- Azure Relay's control channel caps request/response bodies at roughly 64 KB.
  Ordinary Teams messages and Adaptive Card payloads are well under this;
  something like an inline base64 file attachment could exceed it. The
  Function logs a warning above 60 KB so you'll see it in Application Insights
  before it becomes a silent failure.
- The Relay hop has a hard 60-second timeout per request. Bot Framework's own
  webhook ack is fast, so this shouldn't bind in practice, but it's worth
  knowing if you ever see `BadGateway` responses under load.
- Up to 25 concurrent listener instances can bind to the same Hybrid
  Connection (useful if you ever want to run the listener replicated for
  availability — Azure Relay load-balances across them).

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `func azure functionapp publish` — "Can't find app with name ..." | Used the local project folder name instead of the deployed Function App name | Use the Bicep output `functionAppName` (`<namePrefix>-func`), check with `az functionapp list` |
| `func azure functionapp publish` — "Can't determine project language from files" | Core Tools didn't auto-detect the isolated-worker `.csproj` | Pass `--dotnet-isolated` explicitly, or update Core Tools |
| Teams "Upload an app" fails — "Please make sure the bot is registered and Teams channel is enabled" | Channels → Microsoft Teams → Apply was never clicked (or hadn't propagated yet) | Go back to the bot resource's Channels blade, Apply, wait a minute or two, retry |
| No `~/.hermes/` found | Native Windows install uses `%LOCALAPPDATA%\hermes\`, not the Linux/macOS path | Check `%LOCALAPPDATA%\hermes\`, or run `hermes doctor` for the real path |
| Teams reply: "⚠️ The model provider failed after retries" | Unrelated to the relay — LLM provider/model misconfigured (e.g. an unsupported model version via a given provider) | Check the gateway console for the raw error; `hermes doctor` / `hermes model` to fix provider/model config |
| Service installs but immediately stops (`sc.exe query` shows `STOPPED` right after `start`) | The `RELAY_*` values weren't set as *machine-wide* env vars, or `appsettings.json` was left blank — a service doesn't see your PowerShell session's `$env:` variables | Set them via `[Environment]::SetEnvironmentVariable(..., "Machine")` or fill `appsettings.json`, then `sc.exe start` again |
| Need to see what the service is actually doing | It logs to the Windows Event Log, not a console | Event Viewer → Windows Logs → Application, source `HermesTeamsRelayListener` |
