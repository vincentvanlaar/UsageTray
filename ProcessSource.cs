using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace UsageTray;

/// <param name="CanEnd">This app may terminate it (not a guarantee that nothing else stops it).</param>
/// <param name="Cpu">Processor time used since it started.</param>
sealed record Proc(int Pid, int ParentPid, string Name, long Commit, DateTime? Started, string? CommandLine, bool CanEnd, TimeSpan Cpu);

/// <summary>One process as shown in the flyout; <see cref="Started"/> guards against PID reuse when ending it.</summary>
sealed record ProcRow(int Pid, DateTime? Started, string Name, string Detail, long Commit, bool Protected, TimeSpan Cpu = default);

/// <summary>An app: a root process plus its descendants. <see cref="Members"/> has the root first.</summary>
/// <param name="Link">Opens the app's own view of it (Claude sessions: claude://… in the desktop app).</param>
/// <param name="Sessions">Claude Code sessions this app started. They have their own rows, but can end with it.</param>
/// <param name="Agent">"Claude" or "Codex" when the app is an agent session.</param>
sealed record ProcGroup(string Title, string Detail, long Commit, IReadOnlyList<ProcRow> Members, string? Link = null, int Sessions = 0,
    string? Agent = null, ClaudeSession? Session = null, string? CommandLine = null)
{
    public int RootPid => Members[0].Pid;
    public bool Protected => Members[0].Protected;
}

/// <summary>
/// Running processes grouped per app and sorted by commit (private bytes), the number that
/// matters for memory pressure: idle processes are paged out, so their working set looks small.
/// </summary>
static class ProcessSource
{
    // Children of these start their own group instead of joining the parent's.
    static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "sihost", "svchost", "services", "wininit", "winlogon", "userinit" };

    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
        { "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "fontdrvhost" };

    // Chromium browsers: one row with a renderer count instead of a window title.
    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "brave", "vivaldi", "opera" };

    /// <param name="agent">Only the sessions of this agent ("Claude" or "Codex"), all of them.</param>
    public static IReadOnlyList<ProcGroup> Read(int top = 40, string? agent = null)
    {
        var procs = Snapshot();
        var sessions = ClaudeSessions.Read(procs);

        // A parent PID only counts when that process is older than the child (PIDs get reused).
        Proc? Parent(Proc p) => procs.TryGetValue(p.ParentPid, out var a) && a.Pid != p.Pid && a.Started <= p.Started ? a : null;

        bool IsSession(Proc p) => sessions.ContainsKey(p.Pid) || IsCli(p) || IsCodex(p);

        // Agent sessions are apps of their own, apart from whatever launched them. A session
        // started from inside another one (a `claude -p` run by a tool) stays with that one.
        bool OwnGroup(Proc p)
        {
            if (!IsSession(p)) return false;
            var guard = 0;
            for (var a = Parent(p); a is not null && guard++ < 64; a = Parent(a))
                if (IsSession(a)) return false;
            return true;
        }

        var kids = new Dictionary<int, List<Proc>>();
        var roots = new List<Proc>();
        foreach (var p in procs.Values)
        {
            if (Parent(p) is { } parent && !Launchers.Contains(parent.Name) && !OwnGroup(p))
            {
                if (!kids.TryGetValue(parent.Pid, out var list)) kids[parent.Pid] = list = [];
                list.Add(p);
            }
            else roots.Add(p);
        }

        List<Proc> Tree(Proc root)
        {
            var all = new List<Proc> { root };
            for (var i = 0; i < all.Count; i++)
                if (kids.TryGetValue(all[i].Pid, out var list)) all.AddRange(list);
            return all;
        }

        var trees = roots.Select(Tree).ToList();
        var groupOf = new Dictionary<int, int>();
        foreach (var tree in trees)
            foreach (var p in tree)
                groupOf[p.Pid] = tree[0].Pid;

        // Ending Claude desktop or a terminal also ends the sessions it started.
        var launched = new Dictionary<int, int>();
        foreach (var r in roots.Where(IsSession))
            if (Parent(r) is { } parent && !Launchers.Contains(parent.Name) && groupOf.TryGetValue(parent.Pid, out var group))
                launched[group] = launched.GetValueOrDefault(group) + 1;

        return trees
            .Where(t => agent is null || IsSession(t[0]) && AgentOf(t[0]) == agent)
            .OrderByDescending(t => t.Sum(p => p.Commit))
            .Take(agent is null ? top : int.MaxValue)
            .Select(t => Build(t, IsSession(t[0]), sessions.GetValueOrDefault(t[0].Pid), launched.GetValueOrDefault(t[0].Pid), Parent(t[0])))
            .ToList();
    }

    static ProcGroup Build(List<Proc> all, bool isSession, ClaudeSession? s, int launched, Proc? parent)
    {
        var root = all[0];
        var browser = Browsers.Contains(root.Name);
        var members = all
            .Select(p => new ProcRow(p.Pid, p.Started, p.Name, p == root && browser ? "browser" : Describe(p), p.Commit, IsProtected(p), p.Cpu))
            .ToList();

        var title = root.Name;
        string? detail, link = null;
        if (isSession && IsCodex(root))
        {
            var server = root.CommandLine!.Contains(" app-server");
            title = server ? L.T("Codex app-server", "Codex app-server") : L.T("Codex session", "Codex-sessie");
            detail = Fmt.Join("codex", server ? parent?.Name : null);
        }
        else if (isSession)
        {
            title = s?.Title ?? L.T("Claude Code session", "Claude Code-sessie");
            var status = s is { Status: "idle", StatusSince: { } at } ? L.T("idle ", "stil ") + Fmt.Span(DateTimeOffset.Now - at) : s?.Status;
            detail = Fmt.Join("claude", s?.Entrypoint is "claude-desktop" ? null : s?.Entrypoint, status);
            if (s?.LocalId is { } id) link = "claude://claude.ai/epitaxy/" + id;
        }
        else if (IsDesktop(root)) (title, detail) = ("Claude desktop", null);
        else if (browser) detail = $"{members.Count(m => m.Detail == "renderer")} renderers";
        else detail = WindowTitle(root.Pid);

        return new(title, Fmt.Join(detail, all.Count > 1 ? $"{all.Count} proc" : null) ?? "", all.Sum(p => p.Commit), members, link, launched,
            isSession ? AgentOf(root) : null, s, root.CommandLine);
    }

    /// <summary>Ends the given processes; skips protected ones and PIDs that were reused since the snapshot.</summary>
    /// <returns>How many could not be ended.</returns>
    public static int End(IEnumerable<ProcRow> rows)
    {
        var failed = 0;
        var ending = new List<Process>();
        foreach (var row in rows.Where(r => !r.Protected))
        {
            Process? p = null;
            try
            {
                p = Process.GetProcessById(row.Pid);
                if (p.StartTime != row.Started) { p.Dispose(); continue; }
                p.Kill();
                ending.Add(p);
            }
            catch (Win32Exception)
            {
                failed++; // access denied
                p?.Dispose();
            }
            catch (Exception)
            {
                p?.Dispose(); // already gone, often because its parent was just ended
            }
        }

        // Kill only starts the end. Wait a moment, so the list that loads next no longer shows them.
        var deadline = DateTime.Now.AddSeconds(1);
        foreach (var p in ending)
            using (p)
                p.WaitForExit(Math.Max(0, (int)(deadline - DateTime.Now).TotalMilliseconds));
        return failed;
    }

    static bool IsProtected(Proc p) => !p.CanEnd || p.Pid == Environment.ProcessId || Critical.Contains(p.Name);

    static bool IsClaude(Proc p) => p.CommandLine is not null && p.Name.Equals("claude", StringComparison.OrdinalIgnoreCase);

    // The Store package and the per-user installer put the desktop app here; the Claude Code CLI lives elsewhere.
    static bool IsDesktop(Proc p) => IsClaude(p) && Exe(p.CommandLine!) is var exe
        && (exe.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)
            || exe.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase));

    static bool IsCli(Proc p) => IsClaude(p) && !IsDesktop(p);

    // The Codex CLI (a terminal session, or the app-server behind an editor or the Codex app), not the Codex app's own window.
    static bool IsCodex(Proc p) => p.CommandLine is { } cmd && p.Name.Equals("codex", StringComparison.OrdinalIgnoreCase)
        && !cmd.Contains("--type=") && (cmd.Contains(" app-server") || !Exe(cmd).Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase));

    static string AgentOf(Proc p) => IsCodex(p) ? "Codex" : "Claude";

    static string Exe(string cmd) => cmd.StartsWith('"') ? cmd[1..Math.Max(1, cmd.IndexOf('"', 1))] : cmd.Split(' ')[0];

    /// <summary>Chromium/Electron process role when there is one, else the start of the arguments.</summary>
    static string Describe(Proc p)
    {
        if (p.CommandLine is not { } cmd) return "";
        var type = Regex.Match(cmd, @"--type=([\w-]+)").Groups[1].Value;
        if (type == "renderer") return cmd.Contains("--extension-process") ? "extension" : type;
        if (type == "utility")
        {
            var sub = Regex.Match(cmd, @"--utility-sub-type=([\w.]+)").Groups[1].Value;
            return sub.Length > 0 ? sub[(sub.LastIndexOf('.') + 1)..] : type;
        }
        if (type.Length > 0) return type;

        // Arguments without the directories of paths, so the script or file name is what shows.
        var args = cmd[Math.Min(cmd.Length, Exe(cmd).Length + (cmd.StartsWith('"') ? 2 : 0))..];
        args = Regex.Replace(args, @"[A-Za-z]:\\(?:[^""\\:*?<>|]+\\)+", "").Replace("\"", "").Trim();
        return args.Length > 80 ? args[..80] : args;
    }

    static string? WindowTitle(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainWindowTitle is { Length: > 0 } title ? title : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Dictionary<int, Proc> Snapshot()
    {
        var procs = new Dictionary<int, Proc>();
        var buffer = Marshal.AllocHGlobal(QueryBuffer);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == 0) continue;
                    try
                    {
                        DateTime? started = null;
                        try { started = p.StartTime; }
                        catch (Exception) { /* protected process: memory numbers only */ }

                        // The WSL/Hyper-V VM workers hold their memory as working set, not private bytes.
                        var commit = p.ProcessName.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase)
                            ? Math.Max(p.PrivateMemorySize64, p.WorkingSet64)
                            : p.PrivateMemorySize64;
                        var (parent, commandLine) = started is null ? (0, null) : Query(p.Id, buffer);
                        var cpu = TimeSpan.Zero;
                        if (started is not null)
                            try { cpu = p.TotalProcessorTime; }
                            catch (Exception) { /* no rights: no CPU number */ }
                        procs[p.Id] = new(p.Id, parent, p.ProcessName, commit, started, commandLine,
                            started is not null && CanTerminate(p.Id), cpu);
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited while we were looking.
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return procs;
    }

    const int QueryBuffer = 0x10100; // UNICODE_STRING header + its 64 KB maximum

    /// <summary>Parent PID and command line, through one limited-query handle.</summary>
    static (int Parent, string? CommandLine) Query(int pid, IntPtr buffer)
    {
        using var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle.IsInvalid) return (0, null);

        var parent = NtQueryInformationProcess(handle, 0 /* ProcessBasicInformation */, buffer, Marshal.SizeOf<BasicInformation>(), out _) == 0
            ? (int)Marshal.PtrToStructure<BasicInformation>(buffer).InheritedFromUniqueProcessId
            : 0;
        string? commandLine = null;
        if (NtQueryInformationProcess(handle, 60 /* ProcessCommandLineInformation */, buffer, QueryBuffer, out _) == 0
            && Marshal.PtrToStructure<UnicodeString>(buffer) is { Buffer: not 0 } text)
            commandLine = Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
        return (parent, commandLine);
    }

    static bool CanTerminate(int pid)
    {
        using var handle = OpenProcess(0x0001 /* PROCESS_TERMINATE */, false, pid);
        return !handle.IsInvalid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicInformation // PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll")]
    static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, IntPtr buffer, int length, out int returned);
}
