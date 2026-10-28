using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UsageTray;

enum SessionState { NeedsYou, Working, Idle, Unknown } // in list order

/// <summary>One running Claude Code or Codex session, as the session panel shows it.</summary>
/// <param name="Since">When it entered <paramref name="State"/>.</param>
/// <param name="Cpu">Percent of the whole machine; null until there is a previous reading.</param>
/// <param name="Gpu">Percent of the busiest GPU engine; null when GPU use is not measured.</param>
/// <param name="CanStop">Ending it loses nothing the agent cannot resume.</param>
sealed record AgentSession(string Title, string? Detail, SessionState State, string StateText, DateTimeOffset? Since,
    long Commit, double? Cpu, double? Gpu, ProcGroup Group, bool CanStop);

/// <summary>
/// Running agent sessions per provider, with memory, CPU and GPU of each session's process tree.
/// Claude Code status comes from ~/.claude/sessions; Codex status from the rollout file
/// (~/.codex/sessions) that the session's process holds open.
/// </summary>
static class SessionSource
{
    // Previous processor times per provider, keyed by pid and start (pids get reused).
    static readonly Dictionary<string, (DateTime At, Dictionary<(int, DateTime?), TimeSpan> Times)> LastCpu = [];

    public static IReadOnlyList<AgentSession> Read(string agent, bool gpu)
    {
        var groups = ProcessSource.Read(agent: agent);
        var cpu = CpuPercent(agent, groups);
        var load = gpu ? Meters.Gpu() : null;
        double? Gpu(ProcGroup g) => load is null ? null : Math.Min(100, g.Members.Sum(m => load.ByPid.GetValueOrDefault(m.Pid)));

        var holders = agent == "Codex" && groups.Count > 0 ? RolloutHolders() : [];
        return groups
            .Select(g => (agent == "Codex" ? Codex(g, holders) : Claude(g)) with { Cpu = cpu.GetValueOrDefault(g.RootPid), Gpu = Gpu(g) })
            .OrderBy(s => s.State)
            .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase) // stable between refreshes; memory moves too much
            .ToList();
    }

    static AgentSession Claude(ProcGroup g)
    {
        var s = g.Session;
        var since = s?.StatusSince;
        var (state, text) = s?.Status switch
        {
            "busy" => (SessionState.Working, L.T("working", "bezig")),
            "waiting" => (SessionState.NeedsYou, s.WaitingFor switch
            {
                "input needed" => L.T("question", "vraag"),
                null or "dialog open" => L.T("waiting for you", "wacht op jou"),
                var other => other,
            }),
            "idle" => (SessionState.Idle, L.T("idle", "stil") + (since is { } at ? " " + Fmt.Span(DateTimeOffset.Now - at) : "")),
            _ => (SessionState.Unknown, s?.Status ?? L.T("running", "actief")),
        };
        var detail = Fmt.Join(Project(s?.Cwd), s?.Entrypoint is null or "claude-desktop" ? null : s.Entrypoint);
        return new(g.Title, detail, state, text, since, g.Commit, null, null, g, !g.Protected);
    }

    static AgentSession Codex(ProcGroup g, Dictionary<string, HashSet<int>> holders)
    {
        var pids = g.Members.Select(m => m.Pid).ToHashSet();
        var threads = holders.Where(h => h.Value.Overlaps(pids)).Select(h => Rollout.Read(h.Key)).OfType<Rollout>()
            .OrderBy(t => t.Subagent).ThenByDescending(t => t.At).ToList();
        var server = g.CommandLine?.Contains(" app-server") == true;
        var host = Regex.Match(g.Detail, @"^codex · (\w+)").Groups[1].Value;

        var (state, text, since) = threads.Count == 0
            ? (SessionState.Unknown, L.T("running", "actief"), (DateTimeOffset?)null)
            : threads.Any(t => t.Busy)
                ? (SessionState.Working, L.T("working", "bezig"), threads.Where(t => t.Busy).Max(t => t.At))
                : (SessionState.Idle, L.T("idle", "stil") + " " + Fmt.Span(DateTimeOffset.Now - threads.Max(t => t.At)!.Value), threads.Max(t => t.At));
        var main = threads.FirstOrDefault();
        var title = main?.Name ?? g.Title;
        var detail = Fmt.Join(Project(main?.Cwd), threads.Count > 1 ? L.T($"{threads.Count} threads", $"{threads.Count} threads") : null,
            host.Length > 0 ? host : null);
        // An app-server serves an editor or the Codex app; ending it would break that app.
        return new(title, detail, state, text, since, g.Commit, null, null, g, !g.Protected && !server);
    }

    /// <summary>Repository or folder name; a Claude worktree shows its repository.</summary>
    static string? Project(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return null;
        var worktree = cwd.IndexOf(@"\.claude\worktrees\", StringComparison.OrdinalIgnoreCase);
        return Path.GetFileName((worktree > 0 ? cwd[..worktree] : cwd).TrimEnd('\\', '/'));
    }

    /// <summary>CPU use per group (by root pid) since the previous reading for this provider.</summary>
    static Dictionary<int, double?> CpuPercent(string agent, IReadOnlyList<ProcGroup> groups)
    {
        var now = DateTime.Now;
        var times = groups.SelectMany(g => g.Members).ToDictionary(m => (m.Pid, m.Started), m => m.Cpu);
        var result = new Dictionary<int, double?>();
        if (LastCpu.TryGetValue(agent, out var last) && now - last.At > TimeSpan.FromMilliseconds(200) && now - last.At < TimeSpan.FromMinutes(1))
        {
            var span = (now - last.At) * Environment.ProcessorCount;
            foreach (var g in groups)
            {
                var used = TimeSpan.Zero;
                foreach (var m in g.Members)
                    if (last.Times.TryGetValue((m.Pid, m.Started), out var before)) used += m.Cpu - before;
                    else if (m.Started > last.At) used += m.Cpu; // started since
                result[g.RootPid] = Math.Clamp(100 * (used / span), 0, 100);
            }
        }
        LastCpu[agent] = (now, times);
        return result;
    }

    // --- Codex rollouts ----------------------------------------------------------------------

    static readonly string CodexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>Rollouts written in the last week, and the processes that hold each open.</summary>
    static Dictionary<string, HashSet<int>> RolloutHolders()
    {
        var result = new Dictionary<string, HashSet<int>>();
        var since = DateTime.Now.AddDays(-7);
        for (var day = DateTime.Today; day >= DateTime.Today.AddDays(-30); day = day.AddDays(-1))
        {
            var dir = Path.Combine(CodexHome, "sessions", $"{day:yyyy}", $"{day:MM}", $"{day:dd}");
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles("rollout-*.jsonl"))
                    if (file.LastWriteTime >= since && Holders(file.FullName) is { Count: > 0 } pids)
                        result[file.FullName] = pids;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Folder moved or locked: skip it.
            }
        }
        return result;
    }

    /// <summary>Processes that have the file open, from the Restart Manager.</summary>
    static HashSet<int> Holders(string path)
    {
        var pids = new HashSet<int>();
        if (RmStartSession(out var session, 0, new StringBuilder(64)) != 0) return pids;
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, 0, 0, null) != 0) return pids;
            uint count = 0;
            var infos = Array.Empty<RmProcessInfo>();
            for (var tries = 0; tries < 3; tries++)
            {
                var status = RmGetList(session, out var needed, ref count, infos, out _);
                if (status == 0) break;
                if (status != 234 /* ERROR_MORE_DATA */) return pids;
                infos = new RmProcessInfo[count = needed];
            }
            for (var i = 0; i < Math.Min(count, (uint)infos.Length); i++) pids.Add(infos[i].ProcessId);
        }
        finally
        {
            RmEndSession(session);
        }
        return pids;
    }

    /// <param name="Busy">A turn started and has not ended.</param>
    /// <param name="At">When the last turn started or ended.</param>
    sealed record Rollout(string? Name, string? Cwd, bool Subagent, bool Busy, DateTimeOffset? At)
    {
        public static Rollout? Read(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var meta = JsonNode.Parse(reader.ReadLine() ?? "")?["payload"];
                var id = meta?["id"]?.ToString();

                // The turn events are near the end; a turn writes far less than this between them.
                stream.Seek(Math.Max(0, stream.Length - 256 * 1024), SeekOrigin.Begin);
                reader.DiscardBufferedData();
                bool busy = false;
                DateTimeOffset? at = null;
                for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
                {
                    var type = Regex.Match(line, @"^\{""timestamp"":""([^""]+)"",(?:""ordinal"":\d+,)?""type"":""event_msg"",""payload"":\{""type"":""(task_started|task_complete|turn_aborted)""");
                    if (!type.Success) continue;
                    busy = type.Groups[2].Value == "task_started";
                    at = DateTimeOffset.TryParse(type.Groups[1].Value, out var t) ? t : at;
                }
                at ??= new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                return new(id is null ? null : ThreadName(id), meta?["cwd"]?.ToString(), meta?["thread_source"]?.ToString() == "subagent", busy, at);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        /// <summary>The thread's name from ~/.codex/session_index.jsonl; the last entry wins.</summary>
        static string? ThreadName(string id)
        {
            try
            {
                string? name = null;
                foreach (var line in File.ReadLines(Path.Combine(CodexHome, "session_index.jsonl")))
                    if (line.Contains(id) && JsonNode.Parse(line) is JsonObject entry && entry["id"]?.ToString() == id)
                        name = entry["thread_name"]?.ToString() is { Length: > 0 } n ? n : name;
                return name;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RmProcessInfo // RM_PROCESS_INFO
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus, TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint applications, nint processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[] infos, out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint session);
}
