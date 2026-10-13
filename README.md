# AI Search for Jellyfin

Ever wanted to search your Jellyfin library for "something tense at sea" or "that Kaamelott
episode with the Grail" instead of guessing a title? That's what this plugin does. You describe
what you want, it finds the titles in your library that fit, and it tells you why each one showed
up.

It works in two places. Jellyfin's normal search gets an extra "AI Search" row under the usual
title results. And there's an AI search button that opens a popup where you can be more
descriptive, and a language model picks and explains the best matches.

`Jellyfin 12.0+` · `any OpenAI-compatible API` · `runs fully offline with Ollama` · `no database`

## What it looks like

Search "jaws" and Jellyfin finds the film by its title, like always. Underneath, the AI Search row
finds the other shark films, with a short reason under each one.

<img width="1200" alt="Searching jaws: the title match, then the AI Search row with shark films and a reason under each" src="docs/images/search-row-title-match.jpg" />

When nothing matches by title, the row still has answers:

<img width="1200" alt="Searching stranded alone in space: no title match, the AI Search row lists The Martian, Gravity, Moon and others" src="docs/images/search-row-no-title-match.jpg" />

And the popup, for longer requests:

<img width="450" alt="The AI search popup answering a tense survival story at sea" src="docs/images/popup-results.jpg" />

## How it works

When you set it up, the plugin reads every movie and show in your library (title, genres, cast,
synopsis) and turns each one into an embedding, a list of numbers that captures what it's about.
That happens once, then every night for whatever changed. It's all kept in one file in Jellyfin's
data folder.

When you search, your words get turned into numbers the same way, and the plugin looks for the
titles that are closest. On a library of 18,000 items that takes about 2 ms.

In Jellyfin's search page, the closest titles go into the AI Search row. Jellyfin's own results
never wait for it, the row loads on its own a moment later. If you've set up a chat model, it then
writes a one-line reason for each title.

In the popup, the 40 closest titles go to the chat model along with their synopsis and cast (and
your favorites and watch history, if you want it personal). It picks the best ones and explains
why. It can only pick from that list, so it never makes up a film you don't have.

If you already run a service that indexes your library, the plugin can ask it for matches instead
of building its own index.

## What you can do

- Search by mood, theme, plot, actor or director, in your own words
- Get an AI Search row in Jellyfin's normal search, on top of the title results
- "Surprise me" for a random mix picked for you
- "Help me choose", where the model asks you two or three quick questions
- Save any set of results as a playlist
- Search movies, series or single episodes
- Keep the last 12 searches per user
- Use it in French or English
- Skip the language model entirely and just get the closest matches

## Install

In Jellyfin, go to **Dashboard → Plugins → Repositories** and add:

```
https://raw.githubusercontent.com/Franciskid/jellyfin-plugin-ai-search/main/manifest.json
```

Then install **AI Search** from the catalog, restart Jellyfin, and open **Dashboard → Plugins →
AI Search**.

## Setting it up

You need two things: a chat model (for the popup's picks and reasons) and an embedding model (to
build the index). Anything that speaks the OpenAI API works.

**Everything local, with Ollama.** Nothing leaves your network.

```
Chat endpoint URL:    http://<ollama-host>:11434/v1/chat/completions
Models endpoint URL:  http://<ollama-host>:11434/v1/models
Retrieval:            Local index
Embeddings endpoint:  http://<ollama-host>:11434/v1/embeddings
Embedding model:      bge-m3      (run "ollama pull bge-m3" first)
```

**With OpenAI.**

```
Chat endpoint URL:    https://api.openai.com/v1/chat/completions
Chat API key:         sk-...
Model:                gpt-4o-mini
Retrieval:            Local index
Embeddings endpoint:  https://api.openai.com/v1/embeddings
Embedding model:      text-embedding-3-small
```

Save, then click **Build index now**. On 3000 movies it takes a few minutes with OpenAI (around
$0.05) and about 40 minutes with Ollama on a CPU. After that, the nightly update usually takes a
minute.

A few tips:

- `bge-m3` is a good choice if your metadata isn't all in English.
- If you use `nomic-embed-text`, fill in the two prefixes on the settings page
  (`search_query: ` and `search_document: `), or the results will be poor.
- You can mix providers, for example a hosted chat model with a local Ollama for embeddings.
- If you'd rather plug in your own search service, pick **Remote service** as the retrieval
  source. What it needs to answer is in the [technical reference](docs/reference.md).

## Settings worth knowing

Most settings explain themselves on the settings page. A few you might want to change:

- **Libraries AI Search ignores**: tick a library (home videos, kids, whatever) and it stays out
  of AI Search. It still shows up in Jellyfin's normal title search.
- **Language**: French or English for everybody, or Auto to follow each user's Jellyfin language.
- **Show matches in their own "AI Search" row**: turn it off and the matches get mixed into
  Jellyfin's own rows instead.
- **Native search only**: hides the AI button and popup, and keeps only the AI Search row in
  Jellyfin's search.
- **Use a language model**: turn it off to skip the chat model and just get the closest titles.
- **Log searches**: keeps a few days of searches in a file, if you want to see what people look
  for.

TV and mobile apps can't show an extra row, so they get the AI matches mixed in after the title
results. They wait at most 2 seconds for them, so searching never feels slow.

## Privacy

Your API keys and endpoints stay on the server, the browser never sees them. When you search in
the popup, the chat model gets your search and some details about 40 titles (plus favorites and
watch history if personalization is on). When the index is built, the embedding model gets each
title's metadata. With Ollama for both, all of it stays at home.

## When something's off

| What you see | What to do |
|---|---|
| The AI button or the AI Search row doesn't show up | Refresh the page with Ctrl+Shift+R (Cmd+Shift+R on a Mac) so the browser loads the new script. |
| The index stays at 0 | Look at the line under "Build index now", it shows the error. |
| Building the index says it got "empty (null or NaN) values" | The model isn't an embedding model. A reranker, or llama.cpp started with `--pooling rank`, won't work. Use something like `bge-m3` with `--embedding --pooling mean`. |
| The model complains about `max_tokens` | Update to 1.3.0.1 or later, the plugin handles it on its own. |
| Results feel random | Check the index is built. With `nomic-embed-text`, check the prefixes. |

## Compatibility

Versions 1.2.0.0 and later need Jellyfin 12. Jellyfin 10.11 servers keep getting 1.1.1.0 from the
catalog automatically.

## More screenshots

From a real library of about 3000 films, in the popup.

"american movie about american frontier":

<img width="588" height="698" alt="Theme query and its results" src="https://github.com/user-attachments/assets/61e6c8bf-f703-4ee3-8463-bb19ae8b0de9" />

"south park episode with cartman managing a park":

<img width="593" height="538" alt="Episode search result" src="https://github.com/user-attachments/assets/a40f3314-88df-497f-8c95-3bcd54b453c7" />

Surprise me:

<img width="593" height="682" alt="Surprise me results" src="https://github.com/user-attachments/assets/60921318-3230-4557-9d31-ec35372b5d58" />

## For developers

The HTTP API, the contract for a remote search service and the search log format are in
[docs/reference.md](docs/reference.md).

## License

Made by [Franciskid](https://github.com/Franciskid). GPL-3.0, like most Jellyfin plugins.
