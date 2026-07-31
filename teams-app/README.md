# Teams app package (Azure Portal registration + sideload)

The bot is registered as an **Azure Bot** resource through the Azure Portal
(not the `teams app create` CLI) because it only needs Azure RBAC Contributor
on a resource group, not an Entra ID app-registration role. This manifest is
then sideloaded manually via **Upload a custom app**.

## 1. Create the Azure Bot resource

1. [portal.azure.com](https://portal.azure.com) → **Create a resource** → **Azure Bot**
2. Same resource group as your `hermesteams-*` Relay/Function resources.
3. Bot handle: `hermes-teams-bot` (anything unique). Pricing tier: **F0** (free).
4. Microsoft App ID: **Create new Microsoft App ID**. Type: **Multi Tenant**
   (or **Single Tenant** if your org requires it — either works with Hermes).
5. **Review + create** → **Create**.
6. Once deployed, open the resource → **Configuration**:
   - Copy the **Microsoft App ID** — this fills `$MICROSOFT_APP_ID` below
     and becomes `TEAMS_CLIENT_ID` in Hermes's `.env`.
   - **Manage Password** → **New client secret** → copy the value
     immediately (shown once) — this becomes `TEAMS_CLIENT_SECRET`.
   - Set **Messaging endpoint** to your Function's URL:
     `https://hermesteams-func.azurewebsites.net/api/messages`
     (swap in your actual Function hostname).
7. **Channels** blade → **Microsoft Teams** → click through and **Apply**.
   This step is easy to miss — if sideloading later fails with "Please make
   sure the bot is registered and Teams channel is enabled," this is why.
   Give it a minute or two to propagate after applying.

Get your tenant ID (becomes `TEAMS_TENANT_ID`):

```bash
az account show --query tenantId -o tsv
```

Put all three into Hermes's `.env` alongside the relay values, plus
`TEAMS_ALLOWED_USERS` (your own AAD object ID — `az ad signed-in-user show
--query id -o tsv`).

## 2. Fill in this manifest

Replace every `$MICROSOFT_APP_ID` in `manifest.json` with the Microsoft App
ID from step 1 (same value for both the top-level `id` and `bots[0].botId`).
Also replace the `developer` block's placeholder URLs — Teams validates
that they're well-formed, not that they're real, but don't ship
`example.com` if you're publishing beyond just your own sideload.

`color.png` (192×192) and `outline.png` (32×32, transparent) are already
here as placeholders — swap them for real branding if you want.

## 3. Package it

```powershell
# Windows PowerShell, from teams-app/
Compress-Archive -Path manifest.json, color.png, outline.png -DestinationPath ../hermes-teams-app.zip -Force
```

```bash
# macOS/Linux
cd teams-app && zip ../hermes-teams-app.zip manifest.json color.png outline.png
```

## 4. Sideload it

Teams → **Apps** → **Manage your apps** → **Upload an app** → **Upload a
custom app** → select `hermes-teams-app.zip` → **Add**.

DM the bot to confirm the whole chain (Teams → Function → Relay → listener
→ Hermes gateway) works end to end.
