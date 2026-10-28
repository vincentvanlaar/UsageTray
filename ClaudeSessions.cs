using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UsageTray;

/// <param name="LocalId">The desktop app's id for the session ("local_…") when the desktop app runs it.</param>
/// <param name="Entrypoint">How it was started, e.g. "claude-desktop", "cli", "sdk-cli".</param>
/// <param name="Status">"idle", "busy" or "waiting", since <paramref name="StatusSince"/>.</param>
/// <param name="WaitingFor">What a waiting session needs, e.g. "input needed" or "dialog open".</param>
/// <param name="Cwd">The folder it works in.</param>
sealed record ClaudeSession(string? Title, string? LocalId, string? Entrypoint, string? Status, DateTimeOffset? StatusSince,
    string? WaitingFor = null, string? Cwd = null);

/// <summary>
/// Running Claude Code sessions, from the file Claude Code keeps per process in
/// ~/.claude/sessions/&lt;pid&gt;.json (undocumented, like the usage endpoint).
/// </summary>
static class ClaudeSessions
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

    /// <returns>Sessions by pid.</returns>
    public static Dictionary<int, ClaudeSession> Read(IReadOnlyDictionary<int, Proc> procs)
    {
        var sessions = new Dictionary<int, ClaudeSession>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid)
                    || !procs.TryGetValue(pid, out var proc) || proc.Started is not { } started) continue;
                try
                {
                    // A file outlives a session that crashed; its process start tells a reused pid apart.
                    if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject doc
                        || doc["procStart"]?.ToString() != started.ToFileTimeUtc().ToString()) continue;

                    string? S(string name) => doc[name]?.ToString() is { Length: > 0 } s ? s : null;
                    var localId = S("hostSessionId") is { } id && Regex.IsMatch(id, "^local_[0-9a-f-]+$") ? id : null;
                    DateTimeOffset? since = doc["statusUpdatedAt"] is JsonValue v && v.TryGetValue<long>(out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                        : null;
                    sessions[pid] = new(S("name"), localId, S("entrypoint"), S("status"), since, S("waitingFor"), S("cwd"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Being rewritten or just removed: no title for this one.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No sessions folder: no titles.
        }
        return sessions;
    }
}
