# AI Search for Jellyfin

Describe what you want to watch, and the plugin searches your Jellyfin library for a match.
It finds relevant titles, then asks a language model to pick the best ones and explain each choice.
The same retrieval also plugs into Jellyfin's own search: in the web client, meaning-based matches
show up in their own "AI Search" row under the ordinary title matches, each with the reason it
was found, without slowing those title matches down.

`Jellyfin 12.0+` · `.NET 10` · `no database` · `any OpenAI-compatible endpoint` · `runs fully offline with Ollama`

Versions 1.2.0.0 and later require Jellyfin 12.0 or newer. Jellyfin 10.11 servers keep getting version
1.1.1.0 from the plugin catalog automatically; the repository's manifest lists both versions and
Jellyfin picks the one that matches the running server.

```
"something tense and short for tonight, like Sicario"
"films about Native Americans"
"a movie with both DiCaprio and Brad Pitt"
"the Game of Thrones episode with a wedding"
```

<img width="1200" height="748" alt="AI Search in the Jellyfin web client" src="https://github.com/user-attachments/assets/db1fdfa3-b91a-4ba0-b75e-da396df542f5" />

---

## Why an index

The first version of this plugin sent a list of titles to the model and asked it to choose. That
limits what it can find. If you have 3000 movies but only send the 300 top-rated titles, the model
never gets a chance to recommend the other 2700.

The plugin now builds an index that lets it search by meaning. An embedding model converts each
title's metadata into numbers that can be compared with your query. The plugin searches the whole
index and sends the closest 40 matches to the language model, along with their cast and synopsis.
That gives the model enough context to make useful choices.

## Pipeline

```mermaid
flowchart LR
  A["Query<br/>(Jellyfin session)"] --> B{"Retrieval"}
  B -->|local| C["Vector index<br/>(in memory)"]
  B -->|remote| D["Search endpoint"]
  C --> E["Prompt builder<br/>+ chat model"]
  D --> E
  E --> F["Library items<br/>+ reasons"]
```

1. The injected client script calls the plugin with the user's existing Jellyfin session. No API
   key and no endpoint URL reach the browser.
2. The plugin collects the titles the user can access, along with their watch history and
   favorites. Jellyfin's per-user access restrictions apply here.
3. The local index or a remote search service returns the closest matches.
4. The plugin removes titles the user cannot access, titles already shown, and watched titles
   unless the user has chosen to include them.
5. The prompt builder writes one line per candidate: `index | title (year) | genres | cast | synopsis`.
6. The model answers with strict JSON and selects candidates **by index only**. It cannot return a
   title that you do not own.
7. The plugin maps the indices back to Jellyfin items and returns posters, titles, years and reasons.

If the index or search service is unavailable, the plugin sends a selection from the library
instead. If the model rejects `response_format`, it retries without that option. It also handles
JSON replies wrapped in Markdown code fences.

## Design decisions

| Decision | Reason |
|---|---|
| Brute-force scan, no vector database | Searching 18000 vectors with 1024 dimensions takes milliseconds. At this library size, a separate database would add setup and maintenance without much benefit. |
| Plain float loop, no SIMD package | The scan is already fast enough. Keeping dependencies simple makes the plugin easier to run across Jellyfin platforms. |
| Vectors normalized once, at index time | Cosine similarity becomes a dot product. The query path does no division. |
| One binary file in Jellyfin's data folder | The index loads at startup and survives a restart. Restarting does not require rebuilding the embeddings. |
| Immutable snapshot, swapped atomically | A rebuild replaces one reference. Searches keep using the previous index until the new one is ready. |
| SHA-256 content hash per document | The nightly build re-embeds only the titles whose metadata changed. If nothing changed, no new embeddings are needed. |
| The hash covers the model id and the prefix | Change the embedding model and every vector is invalidated on the next build. Vectors from two models cannot mix. |
| The model selects by index | The model cannot invent a title. It also cannot return a title the user is not allowed to see, because the candidate list is built from that user's own item list. |
| Retrieval failure falls back to a catalog slice | If the index or embedding service is unavailable, the model can still choose from a smaller selection of library titles. |

**Index file format (v1):**

```
"AISEARCH" | version | model | built-at ticks | dimensions | count
then per entry: item GUID (16 B) | content hash | float32 × dimensions
```

The file is written to a temporary path and renamed, so an interrupted save cannot corrupt the
current index.

## Measured on a real library

3000 movies and 15000 episodes, mostly with French metadata.

| | |
|---|---|
| Index size | about 100 MB, one file |
| Index scan | about 2 ms for 15000 entries × 768 dims (SIMD, one contiguous matrix) |
| Jellyfin search in the web client | about 0.1-0.25 s, same as without the plugin (the AI row loads on its own) |
| AI Search row | about 40-170 ms warm, model-written reasons about 3-4 s later |
| AI popup without the model | about 0.1-0.3 s (the model call, when on, comes on top) |
| First build, Ollama + bge-m3 on CPU | about 40 minutes |
| First build, OpenAI `text-embedding-3-small` | a few minutes, about $0.05 |
| Nightly incremental build | about 1 minute, usually 0 re-embeds |
| Tokens per search | about 7000 input, for 40 candidates with synopsis |
| Cost per search, `mistral-medium` | about $0.01 |
| Cost per search, local Ollama | none |

## Features

| Feature | What it does |
|---|---|
| **Search** | Free-text query. Matches on mood, theme, plot, cast or director. |
| **Surprise me** | No query. The plugin sends the model a random slice of the library. With personalization enabled, it also uses your favorites and history to guide the picks. Useful for finding something you would not think to search for. |
| **Help me choose** | The model uses what you have typed to ask 2 or 3 multiple-choice questions and narrow down the search. Your answers go back into the search. With an empty box you receive generic questions on mood, length and watch state. |
| **Create collection** | Saves the result set as a Jellyfin playlist. Any result set can also be saved after the fact. |
| **History** | The last 12 searches per user, stored server-side. Reopen the results or replay the search. |
| **Movies / TV switch** | Searches series *and* individual episodes. If the query names a series you own, the scan is restricted to that series. |
| **Personalize** | Feeds favorites, watch history and a taste profile into the prompt. Turn it off for a neutral search. |
| **Taste profile** | The model summarizes your favorites and history in 2 to 4 sentences. The profile refreshes in the background after 7 days or when your viewing preferences change. It is stored on your server and sent only to the backend you configured. |
| **Retrieval only** | Turn the language model off. The plugin then returns the retrieval matches ranked by similarity, without explanations or a chat model call. Embedding costs still depend on your provider. |

Answers follow the web client language. French and English are detected and passed to the model.

## Jellyfin search integration

Since 1.2.0.0, the plugin also registers itself as one of Jellyfin's own search providers
(`ISearchProvider`, internal, priority 50). It runs alongside Jellyfin's built-in title search
(`SqlSearchProvider`) for searches from TV and mobile apps or direct `GET /Items?searchTerm=` and
`GET /Search/Hints` calls; the web client's search page gets the same matches in a separate row
instead (see [The AI Search row](#the-ai-search-row-web-client)). No AI popup involved: this works even
with **Native search only** (below) turned on, and needs no language model, only a retrieval
source (local index or remote service).

How it merges with title search, for other apps:

- The built-in provider scores an exact title match 100, a prefix match 80, a word-prefix match
  75, and a "contains" match 50.
- This plugin's matches always score below 50 (`1` to `49`, based on retrieval rank), so a
  meaning-based match never outranks an actual title match. Jellyfin merges every provider's
  results by the best score per item, then loads and sorts them, so your matches land after title
  matches, in whichever Movies / Shows / Episodes section the web client groups them into.
- Covers **Movie**, **Series** and **Episode** items, the same three types the popup and the local
  index already handle. A search restricted to other types (music, books, people, ...) gets no
  matches from this plugin.

Scoping and access control:

- Respects the search's own `includeItemTypes` / `excludeItemTypes` and `mediaTypes` filters, and
  `parentId` (scopes to that whole subtree, e.g. one library).
- Jellyfin trusts an internal search provider to filter by the calling user itself; this plugin
  does that by loading each candidate through the user's own library view and checking Jellyfin's
  standard visibility and parental-control rule (`BaseItem.IsVisibleStandalone`) before returning
  it. An API-key call with no user context skips that per-user check, same as Jellyfin's own
  providers do.
- Jellyfin only answers a search once every provider has, so anything this plugin adds to the
  merged results delays the title matches too. That is why the web client gets the meaning
  matches separately (next section). Other apps (TV, mobile, API callers) get them merged in,
  within a capped wait ("Longest wait for meaning-based matches in other apps", 2 seconds by
  default). A slower backend call is not dropped: it finishes in the background and its answer is
  cached for the next search. On a timeout or any failure this plugin contributes no matches;
  title search is unaffected.

### The AI Search row (web client)

In the web client, meaning matches never touch Jellyfin's own search request. The client script
marks its search requests with an `X-AiSearch-Row` header, the plugin's search provider steps
aside for them, and Jellyfin's title results come back as fast as without the plugin. The script
then asks the plugin for the meaning matches on its own (`GET /AiSearch/Matches`) and shows them
in a separate **AI Search** row under Jellyfin's results, "related to ... by meaning, not by
title":

- Titles Jellyfin's title search already shows are left out of the row.
- A one-word search that already matches titles (typing "kaame" while looking for Kaamelott) is a
  title search: no row, and no backend call. One-word searches with no title match, and searches
  of two words or more, get the row.
- Each result says why it is there. Right away, the plugin cites the words of your search found
  in the title, synopsis, genres, tags, cast, studio or series name (accent- and plural-tolerant),
  with a synopsis excerpt; when nothing matches word for word (often the case across languages)
  it says the match is by meaning. Once the search settles, if a chat model is configured, it
  writes a one-line reason per title (`POST /AiSearch/Explain`, one call per settled search,
  cached), which replaces the instant one.
- Turn the row off to get the matches merged into Jellyfin's rows instead, like other apps.

Speed:

- Query vectors (local index) and remote answers are cached and shared: identical searches running
  at the same time, a retyped term, or "show more" cost one backend call.
- Embedding servers such as Ollama unload an idle model after a few minutes, and reloading it used
  to land on the first search of a session. The web client now asks the server to warm the backend
  up when the search page or the AI popup opens (`POST /AiSearch/Warmup`), so the model loads while
  you type. It does nothing if the backend was used in the last three minutes.
- For other apps, an `X-AiSearch-Matches` response header on `/Items` and `/Search/Hints` lists the
  ids only the meaning search found, and a `Server-Timing: aisearch;dur=...` entry shows the time
  the plugin took. The search log records each result's `origin`: `title`, `semantic` or `both`.

Options (**Dashboard → Plugins → AI Search**, "4. Jellyfin search"):

| Option | Default | Effect |
|---|---|---|
| Add meaning-based matches to Jellyfin's search | On | Turns the feature on or off. |
| Only for searches that look like a sentence | Off | Skips short keyword-style searches (see the minimum word count below), where meaning-based matches tend to be noisy. |
| Minimum words to run | 3 | Used only when the option above is on. |
| Max meaning-based matches per search | 20 | Upper bound on how many of this plugin's matches are added to one search. |
| Longest wait for meaning-based matches in other apps (ms) | 2000 | 250-10000. Caps how long a search from a TV/mobile app can wait on the backend; a slower call finishes in the background for the next search. |
| Show meaning-based matches in their own "AI Search" row | On | The web client row described above. Off merges the matches into Jellyfin's rows. |
| Explain each AI Search result with the language model | On | Needs the chat model from section 1. Off keeps the instant word-match reasons. |
| Native search only | Off | See below. |

### Native search only

Turning this on hides the AI search bar and popup entirely, leaving Jellyfin's own search (now
with meaning-based matches, if enabled above) as the only search experience.

- The client script stays loaded only for the AI Search row (when that option is on); it shows no
  button or popup. With the row off too, `index.html` is served without the script tag, a copy
  already present is stripped as the page is served, and the on-disk injected copy is removed at
  startup.
- These endpoints, which only ever serve the popup, are refused with a bare 404: `Models`,
  `Recommend`, `Interview`, `History` (GET and DELETE), `Playlist`.
- These stay reachable regardless of this option, since they serve the dashboard or native search:
  `ClientScript`, `Health`, `Warmup`, `Matches`, `Explain`, `EmbeddingModels`, `IndexStatus`,
  `RebuildIndex`, `SearchLog`.

## Search log

Turned off by default. When **Log searches** is on, the plugin records every native Jellyfin
search (from the filter above, whenever a request carries a non-empty `searchTerm`) and every AI
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

## Install

Add the repository in **Dashboard → Plugins → Repositories**:

```
https://raw.githubusercontent.com/Franciskid/jellyfin-plugin-ai-search/main/manifest.json
```

Install **AI Search** from the catalog, restart Jellyfin, then open **Dashboard → Plugins → AI Search**.

## Configure

Choose a chat service to make recommendations and a search source to find matching titles.

### Fully local, with Ollama

```
Chat endpoint URL:    http://<ollama-host>:11434/v1/chat/completions
Chat API key:         (empty)
Models endpoint URL:  http://<ollama-host>:11434/v1/models
Model:                any chat model from the dropdown
Retrieval:            Local index
Embeddings endpoint:  http://<ollama-host>:11434/v1/embeddings
Embedding model:      bge-m3          (run "ollama pull bge-m3" first)
```

Save, then click **Build index now**. Nothing leaves your network.

### OpenAI

```
Chat endpoint URL:    https://api.openai.com/v1/chat/completions
Chat API key:         sk-...
Models endpoint URL:  https://api.openai.com/v1/models
Model:                gpt-4o-mini
Retrieval:            Local index
Embeddings endpoint:  https://api.openai.com/v1/embeddings
Embedding model:      text-embedding-3-small
```

OpenAI receives title metadata when the index is built. During a search, it also receives your
query and the metadata of the selected candidates, plus favorites and history if personalization
is enabled.

### Chat-only providers

If your chat provider does not offer embeddings, you can use a local Ollama instance for those.
Set the chat endpoint to your provider and the embeddings endpoint to Ollama. The chat provider
still receives your search prompt and candidate metadata.

### Remote retrieval

```
Chat endpoint URL:    https://ai.your-service.example/api/media/chat
Models endpoint URL:  https://ai.your-service.example/api/media/models
Retrieval:            Remote service
Search endpoint URL:  https://ai.your-service.example/api/media/search
Search API key:       (issued by your service)
```

The service maintains the index and returns matching titles. The plugin then calls the language
model and checks the results against the user's library. Use this when a service already indexes your library, or
when you need to cap usage per user centrally. The contract is at the bottom of this file.

### Which embedding model

- **Multilingual libraries:** `bge-m3` on Ollama, about 1.2 GB. Multilingual, no prefix configuration.
- **English only, hosted:** `text-embedding-3-small`. A low-cost hosted option.
- **`nomic-embed-text`:** works, but you must fill in the prefixes on the config page: query prefix
  `search_query: ` and document prefix `search_document: `. Missing prefixes can lead to poor results without an error message.

## Options

**Language model**

| Option | Effect |
|---|---|
| Enable AI search | Turns the feature on or off. Turning it off also hides the search bar. |
| Use a language model | Off: return retrieval matches ranked by similarity, with no reasons. No chat endpoint is then required. |
| Chat endpoint URL | An OpenAI-compatible chat-completions URL. Used verbatim. |
| Chat API key | Bearer key. Leave empty if the endpoint needs none. |
| Model | The model that chooses titles and explains its choices. Save the URL first, then reload the dropdown. |
| Models endpoint URL | Lists models for the dropdown. Optional. OpenAI, bare-array and Ollama shapes are all accepted. |
| "Help me choose" model | Optional separate model for the follow-up questions. |

**Retrieval**

| Option | Effect |
|---|---|
| Retrieval source | Local index, or remote service. |
| Search endpoint URL / key | Remote only. Returns ranked candidates. |
| Embeddings endpoint URL / key | Local only. An OpenAI-compatible `/v1/embeddings` URL. Empty disables the index. |
| Embedding model | Local only. Loaded into a dropdown from the embedding models endpoint. |
| Also index TV shows & episodes | Local only. Embeds every series and every episode. The first build takes a while. |
| Query / document prefix | Local only. For models that require them. Leave empty for bge-m3. |
| Build index now | Builds or refreshes the index. The status line shows progress and the last error verbatim. |

**Fallback, used until the index exists**

| Option | Effect |
|---|---|
| Candidate titles sent to the model | Size of the catalog slice. Default 350. |
| How candidates are chosen | Top rated, random sample, or half of each. |

**Shared**

| Option | Effect |
|---|---|
| Candidates retrieved per query | How many candidates the model chooses from. Default 40, maximum 200. More candidates give the model more choice, but take longer and can cost more. |
| Max results | How many recommendations come back. |
| Include titles you already watched | Off by default. |
| Request timeout | Seconds before the backend call is abandoned. Default 45. |

**Jellyfin search** (see [Jellyfin search integration](#jellyfin-search-integration) above)

| Option | Default | Effect |
|---|---|---|
| Add meaning-based matches to Jellyfin's search | On | Uses the retrieval source above; no language model needed. |
| Only for searches that look like a sentence | Off | Skips short keyword-style searches. |
| Minimum words to run | 3 | Only checked when the option above is on. |
| Max meaning-based matches per search | 20 | Upper bound per search. |
| Longest wait for meaning-based matches in other apps (ms) | 2000 | How long a TV/mobile app search may wait on the backend. |
| Show meaning-based matches in their own "AI Search" row | On | Web client row under Jellyfin's results. |
| Explain each AI Search result with the language model | On | One-line reason per result, from the chat model. |
| Native search only | Off | Hides the AI search bar/popup and its endpoints. |

**Search log** (see [Search log](#search-log) above)

| Option | Default | Effect |
|---|---|---|
| Log searches | Off | Records native and AI popup searches to a local file. |
| Keep logs for (days) | 7 | 1-365. Older files are deleted at startup and hourly. |

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

### Remote retrieval contract

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

## Privacy and cost

- Endpoint URLs and API keys stay in the server-side plugin configuration. The web client never
  sees them.
- For each search, the chat service receives your prompt and the title, year, genres, cast and a
  short synopsis for about 40 candidates. With personalization enabled, it also receives favorites
  and watch history as title lists, along with your taste profile.
- During an index build, the embedding service receives the metadata for each title that needs
  a new embedding.
- If both services use local Ollama, this data stays on your network.
- History and taste profiles are JSON files under Jellyfin's data folder, one per user.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "No models returned" | Save the URL and key first, then reload the dropdown. |
| Index stays at 0 | Read the status line under "Build index now". It shows the last error verbatim. The scheduled task log holds the same information. |
| Results feel random | The plugin may be searching without an index. Build the index and check for errors. With nomic, also check the prefixes. |
| Ollama returns 500 on embeddings, but short strings work | Seen on a GTX 1660 SUPER: bge-m3 returned NaN vectors on the GPU. Run the embedding model on CPU: `printf 'FROM bge-m3\nPARAMETER num_gpu 0\n' \| ollama create bge-m3-cpu -f -` |
| Search bar missing | The plugin adds one script tag to the web client's `index.html` as Jellyfin serves it, so nothing on disk has to be writable. Check the log for `AiSearch:` lines and hard-refresh the browser. A web client served by something other than Jellyfin needs the tag by hand: `<script src="/AiSearch/ClientScript" defer></script>` |

## Limits

- The index holds one vector per title. Each synopsis is represented by one set of numbers, so very
  specific plot details can be missed.
- Episode search needs the TV index. Until it is built, TV search uses a smaller selection from the library.
- History keeps 12 entries per user.
- The client script is injected into the shipped web client. A custom or externally hosted web
  client needs the script tag added by hand.

## Compatibility

Version 1.2.0.0 requires Jellyfin **12.0 or newer** (`targetAbi 12.0.0.0`), .NET 10. The plugin
builds against the 12.0.0 package, so one DLL loads on every 12.0.x and 12.1.x server so far.

Jellyfin **10.11** servers are not affected by this upgrade: the plugin catalog's manifest lists
both versions, and Jellyfin installs whichever one matches the running server, so a 10.11 install
keeps getting version 1.1.1.0 (`targetAbi 10.11.0.0`, .NET 9) automatically.

## Screens

**Theme query.** "american movie about american frontier"

<img width="588" height="698" alt="Theme query and its results" src="https://github.com/user-attachments/assets/61e6c8bf-f703-4ee3-8463-bb19ae8b0de9" />

**Cast query.** One title matches, so one title comes back.

<img width="597" height="556" alt="Cast query with a single match" src="https://github.com/user-attachments/assets/8c5cb127-f120-442f-95d1-819370cf4a8e" />

**Episode search.** "south park episode with cartman managing a park"

<img width="593" height="538" alt="Episode search result" src="https://github.com/user-attachments/assets/a40f3314-88df-497f-8c95-3bcd54b453c7" />

**Surprise me.** A varied mix from across the library.

<img width="593" height="682" alt="Surprise me results" src="https://github.com/user-attachments/assets/60921318-3230-4557-9d31-ec35372b5d58" />

**One reason per result.**

<img width="586" height="672" alt="Results with a one-line reason each" src="https://github.com/user-attachments/assets/169bac75-95a3-4e8d-ba4e-9ad173acea45" />

## License

Developed by [Franciskid](https://github.com/Franciskid). Licensed under the [GPL-3.0](LICENSE),
like most Jellyfin plugins.
