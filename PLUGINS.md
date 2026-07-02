# Jellyfin AI Plugins — build spec (internal design notes)

> **Internal design notes, not user documentation** — see [README.md](README.md)
> for the public-facing docs. Parts of this spec predate the implementation
> (e.g. the platform now owns a semantic index; the plugin no longer ships
> candidate lists in platform mode).

Custom Jellyfin plugins that add **AI-powered UX** on top of a self-hosted
AI platform. The heavy lifting (reasoning, tool use, personalization) already
lives in the platform's agents; the plugins stay **thin** — they add UI to the
Jellyfin web client, forward the request to an external service, and render
the formatted result.

> Status: draft. **Plugin 1 (AI Search & Recommendation)** is specified from our
> discussion. Other plugins we talked about are stubbed at the bottom — tell me
> their details and I'll flll them in.

---

## Background: how Jellyfin plugins work (so the thin-plugin approach is clear)

- Jellyfin plugins are **server-side .NET assemblies** loaded by the Jellyfin
  server. There is **no officially-supported client/web plugin API**.
- Two capabilities a plugin can use here:
  1. **Custom server API** — register an ASP.NET Core controller. It gets routed
     under the Jellyfin server (e.g. `POST /AiSearch/Recommend`), can require the
     caller's Jellyfin auth (`[Authorize]`), and can read the authenticated user.
     This is where the plugin talks to the external agent service **server-side**
     (so the service URL + any service credential never reach the browser).
  2. **Web UI injection** — ship a small JS/CSS bundle that adds the search bar +
     results panel to the Jellyfin web client. Jellyfin has no formal hook, so
     the common pattern is to inject a `<script>` into the served `index.html`
     (write into the web root at startup, or serve the asset from the plugin and
     add the tag). The injected script uses the browser's **existing Jellyfin
     session token** to call the plugin's own `/AiSearch/*` endpoint — no second
     login.

So each plugin = **[client JS: UI] → [plugin controller: auth + proxy] →
[external agent service] → (agent uses its Jellyfin tools) → back**.

---

## Plugin 1 — AI Search & Recommendation

### Goal / UX

Add an **AI search bar** to the Jellyfin web client. The user types a natural
question ("something tense and short for tonight, like Sicario") and gets back a
small set of **movies from their own library**, each with a one-line reason,
chosen from the prompt **and** the user's profile (watch history, favorites,
ratings). Clicking a result opens its Jellyfin detail page to play — "just like a
real search," only smarter.

The plugin itself only: renders the input + results, sends the prompt, shows the
answer with correct formatting (poster, title, year, reason, play link).

### Architecture

```
Jellyfin web client
  └─ [injected JS] AI search bar ──POST /AiSearch/Recommend (Jellyfin token)──┐
                                                                              ▼
Jellyfin server ── AiSearch plugin controller ([Authorize], reads current user)
                                                                              │
                       POST {AGENT_SERVICE_URL}/recommend  (service credential)
                                                                              ▼
                       External homelab agent service (already has the agent +
                       Jellyfin/Jellyseerr tools; personalizes on watch history)
                                                                              │
                                        recommendations (library item ids + why)
                                                                              ▼
                       controller returns JSON ──► injected JS renders results
```

Recommended split (keeps the plugin thin, per the design): the **agent service**
owns the intelligence and the personalization data-gathering (it already has the
Jellyfin tools), so the plugin passes the prompt + the Jellyfin **user id** and
the agent fetches history / resolves titles to library items itself. The plugin
only needs Jellyfin endpoints for a couple of presentation details (posters,
deep-link) — see below.

### Components to build

1. **`Jellyfin.Plugin.AiSearch`** (.NET) — plugin skeleton:
   - `Plugin.cs` (`BasePlugin<PluginConfiguration>`, a stable GUID, name).
   - `PluginConfiguration` — `AgentServiceUrl`, `AgentServiceApiKey`,
     `TimeoutSeconds`, `MaxResults`, `Enabled`.
   - Config page (`configPage.html`) so the URL/key are set in the Jellyfin
     dashboard, not hard-coded.
2. **`AiSearchController`** — `POST /AiSearch/Recommend`, `[Authorize]`.
   Reads the authenticated user (`IUserManager` / claims), forwards to the agent
   service, returns the normalized result. (Add `GET /AiSearch/Health`.)
3. **Client bundle** (`ai-search.js` + `.css`) — injected into the web client;
   adds the search field to the home/search view, calls `/AiSearch/Recommend`
   via the web client's `ApiClient` (reuses the session token), renders results.

### Endpoints to connect

#### A. Plugin ↔ agent service (the external homelab service)

The one contract you implement on the service side.

```
POST {AGENT_SERVICE_URL}/recommend
Headers: Authorization: Bearer {AGENT_SERVICE_API_KEY}
         Content-Type: application/json
Body:
{
  "prompt": "something tense and short for tonight, like Sicario",
  "user":   { "id": "<jellyfin-user-guid>", "name": "alice" },
  "context": { "maxResults": 6, "locale": "en" }
}
```

```
200 OK
{
  "answer": "Three tight, tense thrillers you haven't watched:",
  "recommendations": [
    {
      "itemId": "<jellyfin-item-guid>",   // if the agent resolved it in-library
      "title": "Wind River",
      "year": 2017,
      "reason": "Same cold, procedural tension as Sicario; you rated Hell or High Water highly.",
      "score": 0.92,
      "inLibrary": true
    }
  ],
  "usedProfile": true                       // whether watch history factored in
}
```

Notes:
- Prefer the agent returning **`itemId`** (resolved against the Jellyfin
  library) so the plugin can deep-link/play directly. If it can only return a
  title, the plugin resolves it via the Jellyfin search endpoint (B).
- Auth service→plugin: a static bearer API key in plugin config is fine for a
  homelab. (If the service is the AI platform, it currently gates on Keycloak —
  so either expose a dedicated API-key endpoint on the service, or run a small
  adapter in front of the agent. Flag: **confirm how the agent service will be
  invoked / authenticated.**)
- The agent gets the user's watch history through its **own** Jellyfin tools
  (the platform's media-mcp already exposes Jellyfin server info / sessions /
  library — extend it with the personalization reads in C).

#### B. Jellyfin REST API — used by the plugin (browser, session token)

Auth: the web client already holds a token; send
`Authorization: MediaBrowser Token="<token>"` (or `X-Emby-Token: <token>`).

| Purpose | Endpoint |
| --- | --- |
| Current user id (to send to the service) | `GET /Users/Me` |
| Resolve a title → library item (fallback if no `itemId`) | `GET /Items?userId={uid}&searchTerm={q}&IncludeItemTypes=Movie&Recursive=true&Limit=5&Fields=ProductionYear,Overview` |
| Item detail (poster tag, overview) | `GET /Users/{uid}/Items/{itemId}` |
| Poster image for a result | `GET /Items/{itemId}/Images/Primary?maxWidth=300&tag={tag}` |
| Open/play the result (deep link) | web route `#!/details?id={itemId}&serverId={serverId}` |

#### C. Jellyfin REST API — used by the agent service for personalization

(The agent, not the browser, calls these — with a privileged server token.)

| Signal | Endpoint |
| --- | --- |
| Watched movies | `GET /Items?userId={uid}&Filters=IsPlayed&IncludeItemTypes=Movie&Recursive=true&Fields=Genres,ProductionYear,UserData` |
| Favorites | `GET /Items?userId={uid}&Filters=IsFavorite&Recursive=true` |
| In-progress / resume | `GET /Users/{uid}/Items/Resume?Limit=20` |
| Per-item user data (playcount, rating, last played) | `item.UserData` on the above |
| Full catalog to recommend from | `GET /Items?userId={uid}&IncludeItemTypes=Movie&Recursive=true&Fields=Genres,Overview,ProductionYear,CommunityRating` |
| Richer history (if installed) | Playback Reporting plugin: `GET /user_usage_stats/{uid}/...` |

### Configuration (plugin dashboard)

```
AgentServiceUrl     https://<your-platform-host>/... (or a dedicated service)
AgentServiceApiKey  <secret>
TimeoutSeconds      20
MaxResults          6
Enabled             true
```

### Security & privacy

- The service credential + URL live in **plugin config / server-side only**;
  never shipped to the browser.
- The browser→plugin call is authenticated by the **user's own Jellyfin token**;
  the controller must `[Authorize]` and pass the *authenticated* user id (do not
  trust a user id from the request body).
- Watch history / profile is sensitive — keep it on the homelab; if the agent
  uses a **local** model for this, note it (matches the platform's `private`
  policy for user data).
- Fail soft: on service timeout/error, show "AI search unavailable" and fall
  back to normal Jellyfin search — never block the UI.

### Open questions (need your call)

1. **Agent service endpoint + auth** — is it the AI platform (needs an API-key
   route, since chat is Keycloak-gated) or a separate small service wrapping the
   agent? What URL + auth?
2. Does the agent **resolve to Jellyfin `itemId`s** itself, or return titles for
   the plugin to resolve?
3. **Local vs hosted model** for the recommendation (privacy of watch history)?
4. Where should the search bar live — home screen, global search, a new tab?

---

## Other plugins (we talked about — to be detailed)

Stubs to fill once you confirm each one's behavior + endpoints:

- **Plugin 2 — _TBD_**: <one-line purpose>
- **Plugin 3 — _TBD_**: <one-line purpose>

For each, I'll capture: goal/UX, the plugin↔service contract, and the Jellyfin
API endpoints it touches — same shape as Plugin 1.
