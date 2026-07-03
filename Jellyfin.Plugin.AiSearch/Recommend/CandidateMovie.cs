using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AiSearch.Recommend;

/// <summary>
/// A movie the model may choose from, plus a pre-formatted cast line. The cast
/// is resolved from live Jellyfin metadata at prompt time (nothing is stored) —
/// it's what lets the model answer people queries ("movies with X and Y")
/// instead of guessing the cast from its own memory.
/// </summary>
/// <param name="Item">The library item (the model picks it by list index).</param>
/// <param name="Cast">Formatted cast/crew, or empty when not gathered (fallback path).</param>
public sealed record CandidateMovie(BaseItem Item, string Cast);
