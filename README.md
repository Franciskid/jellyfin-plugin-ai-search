# AI Search for Jellyfin

Ask your movie library a question, in your own words, and get real answers.

> *"something tense and short for tonight, like Sicario"*
> *"films about Native Americans"*
> *"a feel-good movie I haven't seen yet, nothing too long"*

AI Search adds a little ✨ button to the Jellyfin web client. Tap it and you get
three ways in: **Search** (type what you're in the mood for), **Surprise me**
(let it pick), and **Create collection** (turn a vibe into a saved playlist). It
answers with a handful of movies **from your own library**, each with a one-line
reason why it fits. It knows what you've already watched and what you marked as
favorite. Click a result and you land on its detail page, ready to play.

Not sure what to ask for? Hit **Help me choose** and it interviews you with a few
quick questions — and if you've already half-typed an idea, those questions adapt
to it. Every search is kept in a private, per-user **history** you can reopen or
replay, and any set of results can be saved to a playlist in one tap.

No third party is involved unless you point the plugin at one. Your prompt goes
from your browser to your Jellyfin server, and from there to whatever AI
backend **you** configured: a local Ollama, your own platform, or a hosted API
if that's what you prefer.

I built this for my own library (about 3000 movies, mostly with French
metadata), so everything below is tested on a real, medium-sized, non-English
collection.

---

<img width="1502" height="825" alt="image" src="https://github.com/user-attachments/assets/14dde4ed-9252-47e4-9358-802a783e1584" />


## How it works

1. A small script injected into the Jellyfin web client renders the search bar
   and talks to the plugin using your existing Jellyfin session. No extra
   login, and no API keys ever reach the browser.
2. The plugin, server-side, decides which movies are worth showing the AI.
   Ideally the ~40 that actually match the meaning of your request, out of the
   whole library.
3. A language model picks the best few from that shortlist and writes a short
   reason for each. It can only choose from the list it was given, so it
   cannot recommend a movie you don't own.
4. You get posters, titles, years and reasons.

Step 2 is where most of the work went, so it has its own section below.

## Ways to use it

**Search** is the plain one: type a mood, a vibe, a "like *X* but shorter", and
get your handful of matches with reasons.

**Surprise me** is for when you can't be bothered to type. Instead of matching a
query, the plugin hands the model a broad *random* slice of your library (not the
usual top-rated suspects) and asks it to pull together a delightfully varied,
unexpected mix — still nudged by your favorites and watch history, so it's random
in a way that's tuned to *you*, not random noise. Great for rescuing forgotten
gems you own and never think to search for.

**Create collection** runs a normal search, then offers to save the whole set as
a Jellyfin playlist. Name it, hit save, and it shows up in your library like any
other collection. (You can also save the results of *any* search after the fact —
there's a "Save to playlist" button next to the results.)

**Help me choose** is the fun one. If you click it with the box empty, you get a
short generic interview — mood, length, seen-it-or-not — and your answers become
the search. But if you've already typed something, the plugin sends *that* to the
model first and asks it to write the two or three questions that would actually
narrow *your* request down. Ask for "something like Whiplash" and it might ask
about tone, era, and how familiar you want it; ask for "a French thriller" and
you'll get different questions entirely. You tap through them, and your picks get
folded back into the final search. It's a small thing that turns a vague itch
into a precise query without making you do the wording.

**History** keeps your recent searches (per user, stored on the server) with a
little poster sneak-peek. Reopen one to see its results again, or replay it to
run it fresh. Clear it any time.

**Movies or TV Shows.** A small switch flips the whole popup between searching
your movies and searching your series *and individual episodes* — so "the
episode where they end up in space" is a fair question. TV needs its own index
(see below); until it's built, TV search still works, just less precisely.

**Two quiet knobs.** A subtle *Personalize* toggle decides whether your taste
(favorites + watch history) flavors the results, or whether you get a neutral
search that ignores who you are. And behind the scenes the plugin keeps a short,
self-updating **taste profile** — the model periodically distills what you seem
to like from your favorites and history, and feeds that summary into future
searches so they sharpen as your library and habits evolve. It's never shown or
sent anywhere but your own AI backend, and the Personalize toggle turns it off.

## Two modes

| | **Direct** | **Platform** |
|---|---|---|
| What you need | Any OpenAI-compatible endpoint (Ollama, OpenAI, LiteLLM, OpenRouter...) | A self-hosted AI platform implementing the recommend contract |
| Who finds the candidates | The plugin itself, on your Jellyfin server | The platform, from its own index of your library |
| Personalization | Watch state + favorites, read locally | Watch state + favorites, read live per query |
| Extra perks | Nothing to install beyond the plugin | Query history, usage accounting, an index shared with other tools |
| Pick it if... | You want this working in ten minutes | You already run such a platform |

**Most people want Direct mode.** Platform mode exists because I run a small
self-hosted AI platform at home that already indexes my library and tracks
usage, and I wanted the plugin to go through it. If you don't have anything
like that, Direct mode is the one for you.

## The semantic index

The naive way to do "AI search" is to hand a list of your movies to a model and
hope. That's how the first version of this plugin worked, and it was
disappointing: you can't send 3000 movies (token limits), so you send the 300
top-rated ones, and then the model can't recommend the perfect obscure western
because it never saw it.

So the plugin now builds a **local semantic index**. Every movie becomes a
short text (title, genres, tags, director, lead actors, plot overview) and goes
through an *embedding model*, which turns meaning into numbers. Your search
goes through the same model, and the plugin compares the numbers, in memory, to
find the movies closest to what you asked. Those go to the language model,
together with their synopses.

Some practical numbers, from my 3000-movie library:

- **Size:** the whole index is a single 13 MB file. It loads into memory at
  startup and a search scans it in a few milliseconds. There is no database to
  run.
- **First build:** every movie gets embedded once. On my server (Ollama running
  the embedding model on CPU) that took about 40 minutes. On OpenAI with
  `text-embedding-3-small` it takes a couple of minutes and costs about $0.02.
- **Upkeep:** a nightly scheduled task ("Build AI Search index") re-embeds only
  movies whose metadata changed. On a normal night that's zero movies, and the
  run finishes in about a minute.
- **Model changes:** the index remembers which model built it. Change the
  embedding model (or its prefixes) and the next build re-embeds everything, so
  stale vectors can't linger.
- **Fallback:** no index yet, embeddings endpoint down, or index built with a
  different model? Search still works. The plugin falls back to sending a
  catalog slice (top rated / random / mix, your pick). Worse results, but
  nothing breaks.

### Choosing an embedding model

- My library and my queries are mostly French, so I use **bge-m3** (on Ollama,
  about 1.2 GB). It's multilingual and needs no special configuration. If your
  library isn't purely English, I'd start there too.
- English-only library, hosted API: OpenAI's `text-embedding-3-small` is cheap
  and good.
- `nomic-embed-text` works, but *only* if you fill in its required prefixes on
  the config page: query prefix `search_query: ` and document prefix
  `search_document: `. I learned that the hard way. Without them the results
  quietly degrade into noise, with no error anywhere.
- A note from my own setup: on my GPU (a GTX 1660 SUPER) bge-m3 returned broken
  embeddings (NaN), while two-word test strings worked fine, which made it very
  confusing to debug. Running the model on CPU fixed it:
  `printf 'FROM bge-m3\nPARAMETER num_gpu 0\n' | ollama create bge-m3-cpu -f -`.
  I haven't looked into whether other cards do this. With 3000 movies, CPU
  embedding is fast enough that I didn't need to.

## Getting started

### Install

The easy way: add the plugin repository in **Dashboard → Plugins →
Repositories**:

```
https://raw.githubusercontent.com/Franciskid/jellyfin-plugin-ai-search/main/manifest.json
```

then install **AI Search** from the catalog and restart Jellyfin.

Or build it yourself (Docker required, no local .NET needed):

```sh
./build.sh
# copy dist/AiSearch/ into <jellyfin-config>/plugins/AiSearch_<version>/ and restart
```

Then open **Dashboard → Plugins → AI Search**.

### Recipe: fully local with Ollama (what I recommend)

```
Mode:                     Direct
OpenAI-compatible URL:    http://<ollama-host>:11434
Endpoint API key:         (leave empty)
Embedding model:          bge-m3        (ollama pull bge-m3 first)
Model:                    pick a chat model from the dropdown
```

Save, then click **Build index now**. When the status line shows your library
size, search away. Nothing ever leaves your network.

### Recipe: OpenAI

```
Mode:                     Direct
OpenAI-compatible URL:    https://api.openai.com
Endpoint API key:         sk-...
Embedding model:          text-embedding-3-small
Model:                    gpt-4o-mini (or whatever the dropdown offers)
```

Same dance: save, then Build index now. Prompts and movie metadata will be sent
to OpenAI; that's the trade for zero local compute.

### Recipe: OpenRouter (or any chat-only provider)

OpenRouter doesn't serve embeddings. Point the chat side at OpenRouter, and
fill the **Embedding endpoint URL / key** fields with something that does
serve them. A local Ollama works well: chat goes to OpenRouter, embeddings
stay home.

### Recipe: Platform mode

```
Mode:               Platform
Platform API URL:   https://api.your-platform.example
Application API key: (issued by your platform)
```

The platform owns the index; the plugin just sends the prompt and your user id.
The contract is documented at the bottom of this file.

## Every option, explained

**Shared**

| Option | What it does |
|---|---|
| Enable AI search | Master switch. Also hides the search bar in the web client |
| Mode | Direct or Platform (see above) |
| Model | The chat model that picks and explains. The dropdown is loaded from your endpoint: save the URL/key first, then "Reload models" |
| Candidates retrieved per query | How many semantically-matched movies the model chooses from (default 40). More means broader but slower and pricier |
| Max results | How many recommendations come back (default 6) |
| Include movies you've already watched | Off by default, since the point is usually discovery |
| Request timeout | Seconds before giving up on the AI backend |

**Direct mode: semantic index**

| Option | What it does |
|---|---|
| Use semantic retrieval | The good stuff. Uncheck to always use the fallback instead |
| Also index TV shows & episodes | Enables the "TV Shows" scope by embedding series + every episode. Off by default — episode counts can be large, so the first build after enabling can run a while. Rebuild the index after changing it |
| Embedding model | e.g. `bge-m3`. Empty disables the index |
| Embedding endpoint URL / key | Only when embeddings live somewhere other than the chat endpoint |
| Query / document prefix | Only for models that demand them (nomic). Leave empty for bge-m3 |
| Build index now | Builds or refreshes immediately. The status line shows progress and the last error, if any |

**Direct mode: fallback (used until the index exists)**

| Option | What it does |
|---|---|
| Candidate movies sent to the model | Size of the catalog slice (default 350) |
| How candidates are chosen | Top rated / random sample / mix |

## Privacy and cost

- The web client never sees your endpoint URLs or API keys. Those stay in the
  plugin configuration on the server.
- What the AI backend sees per search: your prompt, and title/year/genre plus a
  short synopsis for the ~40 candidate movies. During index builds it sees each
  movie's metadata document. If the backend is your own Ollama, "sees" means
  "never leaves your LAN".
- Watch history and favorites are summarized as title lists in the prompt.
  Again, only to the backend you configured yourself.
- Local models cost nothing. On hosted APIs, a search is a few thousand input
  tokens (for 40 movie sent to the model (so with titles, actors, synopsis etc) it
  goes up to 7000 tokens, which for mistral-medium which i am using equals to about 0.01$,
  there is probably some optimization to do, and a better model wouldn't need so much
  info about a movie i guess so that would shrik the payload),
  and the index build was a one-time $0.02 for my library.

## Troubleshooting

- **"No models returned" in the dropdown**: save the URL/key first, then
  Reload models. The dropdown queries your endpoint's `/v1/models`.
- **Index stays at 0**: check the status line under "Build index now"; it
  shows the last error verbatim (wrong URL, missing model, auth...). The
  scheduled task's log (Dashboard → Scheduled Tasks) has the same info.
- **Results feel random**: you're probably on the fallback path. Build the
  index, and if you use nomic, set its prefixes (or switch to bge-m3).
- **Ollama returns 500 on embeddings but works on short strings**: that's what
  my GPU did. See the note in "Choosing an embedding model" and try running
  the embedding model on CPU.
- **Search bar missing**: the plugin injects a script tag into the web
  client's `index.html` at startup. Check the Jellyfin log for `AiSearch:`
  lines. A custom web build may need the tag added manually:
  `<script src="/AiSearch/ClientScript" defer></script>`.

## For the curious: endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /AiSearch/Recommend` | Jellyfin session | Run a search (normal or surprise) for the calling user |
| `POST /AiSearch/Interview` | Jellyfin session | Ask the model for "Help me choose" questions tailored to a prompt |
| `GET /AiSearch/History` | Jellyfin session | The caller's recent searches |
| `DELETE /AiSearch/History[/{id}]` | Jellyfin session | Delete one search, or clear all |
| `POST /AiSearch/Playlist` | Jellyfin session | Save a set of results as a playlist for the caller |
| `GET /AiSearch/Health` | Jellyfin session | Enabled/configured state for the client script |
| `GET /AiSearch/Models` | Jellyfin session | Model list proxied from your backend |
| `GET /AiSearch/IndexStatus` | Admin | Semantic index state (the config page uses this) |
| `POST /AiSearch/RebuildIndex` | Admin | Kick a background index build |
| `GET /AiSearch/ClientScript` | none | The injected UI script (contains no secrets) |

`Recommend` also accepts `mode` (`normal`/`surprise`), a per-request
`includeWatched`, and `excludeItemIds` (used by "More like this" to avoid
repeats; those follow-up calls aren't recorded in history).

### Platform mode contract

The plugin POSTs to `{PlatformApiUrl}/api/media/recommend` with a bearer key:

```jsonc
{
  "prompt": "a slow-burn western",
  "model": "some-model-alias",        // optional
  "maxResults": 6,
  "maxRetrieve": 40,
  "includeWatched": false,
  "locale": "en",                      // or "fr"
  "user": { "id": "<jellyfin-user-guid>", "name": "<username>" },
  "client": { "name": "jellyfin-ai-search", "version": "<plugin version>" }
}
```

and expects:

```jsonc
{
  "answer": "one short sentence",
  "model": "model-actually-used",
  "usedProfile": true,
  "recommendations": [
    { "itemId": "<jellyfin-item-guid>", "title": "...", "year": 2017, "reason": "..." }
  ]
}
```

Any backend implementing this works.

## Compatibility

- Jellyfin **10.11** (targetAbi `10.11.0.0`), .NET 9. I run it on 10.11.8 and
  verified it also loads and serves on 10.11.1.
- The UI injection targets the standard Jellyfin web client. French and English
  UI languages are auto-detected and passed to the model so answers match.

## Credits and license

Developed by [Franciskid](https://github.com/Franciskid).

Licensed under the [GPL-3.0](LICENSE), like most Jellyfin plugins.


### Pics

<img width="789" height="558" alt="image" src="https://github.com/user-attachments/assets/7f9eaa93-8973-4506-a705-e51bf76d2e69" />

<img width="796" height="724" alt="image" src="https://github.com/user-attachments/assets/29fdc356-fcf9-424f-bfea-558805ff1472" />

<img width="803" height="713" alt="image" src="https://github.com/user-attachments/assets/dda1c2d6-9489-4511-a001-fc20ab243fcc" />

<img width="797" height="608" alt="image" src="https://github.com/user-attachments/assets/8a21057e-62c7-4f72-85fe-98a83450ad7b" />

