using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Runaway.Advisor
{
    public sealed class AdvisorSettings
    {
        public string Codex = "/opt/homebrew/bin/codex";
        public string Uv = "/opt/homebrew/bin/uv";
        public string McpDir;                 // repo/advisor
        public string WorkDir;                // scratch dir for schema and outputs
        public string Model = "gpt-6.1-sol";
        public string Effort = "medium";
        public int Port = 7377;
        public int TimeoutSeconds = 240;
    }

    public sealed class AdvisorResult
    {
        public bool Ok;
        public string Error;
        public Recommendation Card;
        public LessonDraft Lessons;
        public double Seconds;
        public List<string> ToolCalls = new List<string>();
        public string Raw;
    }

    /// <summary>Runs Codex headless with only the incident MCP tools available and a strict output schema.</summary>
    public sealed class CodexAdvisor
    {
        readonly AdvisorSettings _s;
        /// <summary>Tool calls of the run in progress, for live display. Lock before reading.</summary>
        public List<string> Live { get; private set; } = new List<string>();
        public CodexAdvisor(AdvisorSettings s) { _s = s; Directory.CreateDirectory(s.WorkDir); }

        public Task<AdvisorResult> Plan(string trigger, string recentDecisions, CancellationToken ct = default)
            => Run(Prompts.Planner + "\n\n" + Prompts.Decision(trigger, recentDecisions), Recommendation.Schema, true, ct, json => new AdvisorResult { Card = JsonConvert.DeserializeObject<Recommendation>(json) });

        public Task<AdvisorResult> Debrief(string debriefJson, CancellationToken ct = default)
            => Run(Prompts.Debrief + "\n\nExercise record:\n" + debriefJson, LessonDraft.Schema, false, ct, json => new AdvisorResult { Lessons = JsonConvert.DeserializeObject<LessonDraft>(json) });

        async Task<AdvisorResult> Run(string prompt, string schema, bool withTools, CancellationToken ct, Func<string, AdvisorResult> parse)
        {
            var sw = Stopwatch.StartNew();
            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            var schemaPath = Path.Combine(_s.WorkDir, $"schema-{id}.json");
            var outPath = Path.Combine(_s.WorkDir, $"out-{id}.json");
            var runDir = Path.Combine(_s.WorkDir, "cwd");
            Directory.CreateDirectory(runDir);
            File.WriteAllText(schemaPath, schema);

            var psi = new ProcessStartInfo(_s.Codex)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = runDir,
            };
            var args = new List<string>
            {
                "exec", "--json", "--ephemeral", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check",
                "-m", _s.Model, "-c", $"model_reasoning_effort=\"{_s.Effort}\"",
                "--sandbox", "read-only", "-C", runDir,
                "--output-schema", schemaPath, "-o", outPath,
            };
            foreach (var f in new[] { "shell_tool", "unified_exec", "memories", "plugins", "apps", "image_generation", "browser_use", "in_app_browser", "computer_use", "view_image", "skill_search", "tool_suggest" })
            { args.Add("--disable"); args.Add(f); }
            args.Add("-c"); args.Add("web_search=\"disabled\"");
            args.Add("-c"); args.Add("approval_policy=\"never\"");
            if (withTools)
            {
                var mcpScript = Path.Combine(_s.McpDir, "incident_mcp.py");
                args.Add("-c"); args.Add($"mcp_servers.incident.command=\"{_s.Uv}\"");
                args.Add("-c"); args.Add($"mcp_servers.incident.args=[\"run\",\"--quiet\",\"--project\",\"{_s.McpDir}\",\"python\",\"{mcpScript}\",\"http://127.0.0.1:{_s.Port}\"]");
                args.Add("-c"); args.Add($"mcp_servers.incident.env={{RUNAWAY_API=\"http://127.0.0.1:{_s.Port}\"}}");
                args.Add("-c"); args.Add("mcp_servers.incident.tool_timeout_sec=90");
                args.Add("-c"); args.Add("mcp_servers.incident.startup_timeout_sec=30");
                args.Add("-c"); args.Add("mcp_servers.incident.default_tools_approval_mode=\"approve\"");
            }
            args.Add("-");
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
            psi.Environment["NO_COLOR"] = "1";

            var result = new AdvisorResult();
            Live = result.ToolCalls;
            var stdout = new StringBuilder();
            using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                p.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (stdout) stdout.AppendLine(e.Data);
                    var tool = ParseToolCall(e.Data);
                    if (tool != null) lock (result.ToolCalls) result.ToolCalls.Add(tool);
                };
                p.ErrorDataReceived += (_, e) => { };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.StandardInput.WriteAsync(prompt);
                p.StandardInput.Close();
                var exited = await Task.Run(() =>
                {
                    var deadline = DateTime.UtcNow.AddSeconds(_s.TimeoutSeconds);
                    while (!p.HasExited)
                    {
                        if (ct.IsCancellationRequested || DateTime.UtcNow > deadline) return false;
                        Thread.Sleep(100);
                    }
                    return true;
                });
                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    return new AdvisorResult { Ok = false, Error = ct.IsCancellationRequested ? "cancelled" : "advisor timed out", Seconds = sw.Elapsed.TotalSeconds };
                }
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(outPath))
                    return new AdvisorResult { Ok = false, Error = $"codex exited {p.ExitCode}: {Tail(stdout.ToString())}", Seconds = sw.Elapsed.TotalSeconds };
            }
            var json = File.ReadAllText(outPath).Trim();
            try { File.Delete(outPath); File.Delete(schemaPath); } catch { }
            try
            {
                var parsed = parse(json);
                parsed.Card?.Tidy();
                if (parsed.Lessons != null) foreach (var l in parsed.Lessons.Lessons) { l.Title = Recommendation.Plain(l.Title)?.TrimEnd('.'); l.Text = Recommendation.Plain(l.Text); }
                parsed.Ok = true;
                parsed.Seconds = sw.Elapsed.TotalSeconds;
                parsed.ToolCalls = result.ToolCalls;
                parsed.Raw = json;
                return parsed;
            }
            catch (Exception e)
            {
                return new AdvisorResult { Ok = false, Error = "unreadable advisor output: " + e.Message, Raw = json, Seconds = sw.Elapsed.TotalSeconds };
            }
        }

        static string Tail(string s) => s.Length > 600 ? s.Substring(s.Length - 600) : s;

        /// <summary>Extracts MCP tool names from codex --json events so the UI can show what the advisor checked.</summary>
        static string ParseToolCall(string line)
        {
            if (!line.StartsWith("{")) return null;
            try
            {
                var j = JObject.Parse(line);
                var item = j["item"] as JObject;
                if ((string)j["type"] != "item.started" || item == null) return null;
                var type = (string)item["type"];
                if (type == "mcp_tool_call") return (string)item["tool"];
                if (type == "command_execution") return "shell:" + (string)item["command"];
                return null;
            }
            catch { return null; }
        }
    }

    /// <summary>Loopback HTTP API the MCP server calls. Requests are marshalled onto the simulation thread.</summary>
    public sealed class ApiServer : IDisposable
    {
        readonly HttpListener _http = new HttpListener();
        readonly Func<string, string, Task<string>> _handle;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        public int Port { get; }

        public ApiServer(int port, Func<string, string, Task<string>> handle)
        {
            Port = port;
            _handle = handle;
            _http.Prefixes.Add($"http://127.0.0.1:{port}/");
            _http.Start();
            Task.Run(Loop);
        }

        async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(async () =>
                {
                    string body = "";
                    try
                    {
                        using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
                        var res = await _handle(ctx.Request.Url.AbsolutePath, body);
                        var bytes = Encoding.UTF8.GetBytes(res);
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    }
                    catch (Exception e)
                    {
                        var bytes = Encoding.UTF8.GetBytes(new JObject { ["error"] = e.Message }.ToString());
                        ctx.Response.StatusCode = 500;
                        try { await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length); } catch { }
                    }
                    finally { try { ctx.Response.Close(); } catch { } }
                });
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _http.Stop(); _http.Close(); } catch { }
        }
    }

    /// <summary>Persistent, inspectable lessons. Nothing is retrained; the advisor reads and cites them.</summary>
    public sealed class LessonStore
    {
        readonly string _path;
        public List<Lesson> All { get; private set; } = new List<Lesson>();

        public LessonStore(string path)
        {
            _path = path;
            if (File.Exists(path))
            {
                try { All = JsonConvert.DeserializeObject<List<Lesson>>(File.ReadAllText(path)) ?? new List<Lesson>(); } catch { All = new List<Lesson>(); }
            }
        }

        public List<Lesson> Add(IEnumerable<Lesson> drafts, string runId)
        {
            var added = new List<Lesson>();
            int n = All.Count;
            foreach (var d in drafts)
            {
                n++;
                d.Id = $"L{n:D2}";
                d.SourceRun = runId;
                All.Add(d);
                added.Add(d);
            }
            Save();
            return added;
        }

        public void MarkCited(IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                var l = All.FirstOrDefault(x => x.Id == id);
                if (l != null) l.Cited++;
            }
            Save();
        }

        void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, JsonConvert.SerializeObject(All, Formatting.Indented));
        }
    }
}
