# AI Search: technical reference

The README covers what the plugin does and how to set it up. This page has the details you only
need if you build something around it: the HTTP endpoints, the contract for a remote search
service, and the search log format.

## HTTP API

| Endpoint | Auth | Purpose | Native search only |
|---|---|---|---|
| `POST /AiSearch/Recommend` | Session | Run a search for the calling user | Refused (404) |
| `POST /AiSearch/Interview` | Session | Model-written questions for "Help me choose" | Refused (404) |
| `GET /AiSearch/History` | Session | The caller's recent searches | Refused (404) |
| `DELETE /AiSearch/History[/{id}]` | Session | Delete one search, or clear all | Refused (404) |
| `POST /AiSearch/Playlist` | Session | Save a result set as a playlist | Refused (404) |
| `GET /AiSearch/Health` | Session | Enabled, configured, popup and AI row state, for the client script | Reachable |
| `POST /AiSearch/Warmup` | Session | Load a cold retrieval backend before the user's query | Reachable |
| `GET /AiSearch/Matches?term=` | Session | Meaning matches for the AI Search row, with instant reasons (`parentId`, `locale` optional) | Reachable |
| `POST /AiSearch/Explain` | Session | Model-written reason per matched title (`term`, `itemIds`, `locale`) | Reachable |
| `GET /AiSearch/ClientScript` | none | The injected UI script. Contains no secrets. | Reachable |
| `GET /AiSearch/Models` | Admin | Chat models, proxied from the models endpoint | Refused (404) |
| `GET /AiSearch/EmbeddingModels` | Admin | Embedding models, for the config dropdown | Reachable |
| `GET /AiSearch/IndexStatus` | Admin | Index size, model, last build, last error | Reachable |
| `POST /AiSearch/RebuildIndex` | Admin | Start a background build. Only one build runs at a time. | Reachable |
| `GET /AiSearch/SearchLog` | Admin | Page through the search log (see [Search log](#search-log)) | Reachable |

`Recommend` accepts `mode` (`normal` or `surprise`), `scope` (`movies` or `tv`), `locale`,
`personalize`, a per-request `includeWatched`, and `excludeItemIds`. The "show more" button sends
the ids it already displayed. Those follow-up calls reuse the first call's conversation id and are
not recorded in history.

## Remote search service contract

Three endpoints, each a full URL, each authenticated with a bearer key.

**Search** (required). `POST {SearchEndpointUrl}` returns ranked candidates and makes no model call.

```jsonc
// request
{
  "query": "a slow-burn western",
  "scope": "movies",                   // or "tv"
  "maxResults": 40,
  "includeWatched": false,
  "excludeItemIds": ["<id>", "..."],
  "user": { "id": "<jellyfin-user-guid>", "name": "<username>" }
}
// response
{
  "model": "embedding-model-used",
  "retrieved": 40,
  "results": [
    { "itemId": "<jellyfin-item-guid>", "title": "...", "year": 2017,
      "mediaType": "movie", "genres": ["..."], "overview": "...", "score": 0.55 }
  ]
}
```

Only `itemId` is required. The plugin checks the returned ids against the user's own library,
so a remote service cannot grant access to other content.

**Chat** (required). `POST {ChatEndpointUrl}`, a standard chat completion. Any
`/v1/chat/completions` fits.

**Models** (optional). `GET {ModelsEndpointUrl}` returns `{ "data": [{ "id": "..." }] }`. The
embedding dropdown may add `?capability=embedding` when the service supports it.

## Native search only

This hides the AI search button and popup, and leaves Jellyfin's own search (with the AI Search
row, if it is on) as the only way to search.

- The client script stays loaded only for the AI Search row and shows no button. With the row off
  too, `index.html` is served without the script tag, and a copy left over from before is removed.
- These popup-only endpoints answer with an empty 404: `Models`, `Recommend`, `Interview`,
  `History` (GET and DELETE), `Playlist`.
- These keep working, since the dashboard or Jellyfin's search need them: `ClientScript`,
  `Health`, `Warmup`, `Matches`, `Explain`, `EmbeddingModels`, `IndexStatus`, `RebuildIndex`,
  `SearchLog`.

## Search log

Turned off by default. When **Log searches** is on, the plugin records every native Jellyfin
search (any request with a non-empty `searchTerm`) and every AI
popup search to a local, line-delimited JSON file, one compact JSON object per line, for an
external collector or another admin tool to read.

- Files live at `{Jellyfin data folder}/aisearch/searchlog/{yyyy-MM-dd}.jsonl` (UTC date), one
  file per day.
- **Keep logs for (days)** (default 7, 1-365) controls retention: files older than that are
  deleted at startup and every hour after.
- Recorded per entry: a strictly increasing id, the request timestamp/endpoint/method/status/
  duration, the search term, the caller's user id/name, whether it was an API key, client name/
  version/device, remote address and User-Agent, every other query parameter (with anything that
  looks like a credential - API key, token, auth, password - left out), the `parentId` scope if
  any, result counts, and up to 250 result rows (item id, name, type, year, series/season/episode,
  its `origin` (`title`, `semantic` or `both`) with the semantic similarity and rank, and, for AI
  popup entries, the model's one-line reason). Native search entries also carry the plugin's
  timing split (`backendMs`, `scanMs`, `lookupMs`, `cached`). AI popup entries also carry the mode
  (`normal`/`surprise`) and the model's answer text. Files are plain UTF-8 without a byte-order mark.

**`GET /AiSearch/SearchLog`** (admin only) reads it back:

| Query parameter | Meaning |
|---|---|
| `from` | Inclusive lower timestamp bound (ISO 8601, UTC). Omit for unbounded. |
| `to` | Exclusive upper timestamp bound (ISO 8601, UTC). Omit for unbounded. |
| `afterId` | Only return entries with an id greater than this, for paging. |
| `limit` | Max entries to return, 1-5000, default 1000. |

```jsonc
// response
{
  "entries": [ /* the stored JSON objects, unchanged */ ],
  "hasMore": false,
  "nextAfterId": 12345,        // id of the last entry returned, or null if entries is empty
  "loggingEnabled": true,
  "retentionDays": 7,
  "oldestTimestamp": "2026-09-12T00:00:00.000Z"  // or null if no log files exist
}
```

To page through everything: call once with no `afterId`, then repeat with `afterId` set to the
previous response's `nextAfterId` until `hasMore` is `false`.
