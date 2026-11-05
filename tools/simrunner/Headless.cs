using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json;
using Runaway.Advisor;
using Runaway.Sim;

sealed class OnnxForecaster : IForecaster
{
    readonly InferenceSession _s;
    public OnnxForecaster(string path) { _s = new InferenceSession(path); }
    public float[] Predict(Observation obs)
    {
        var x = new float[Features.Nodes * Features.F];
        var a = new float[Features.Nodes * Features.Nodes];
        Features.Build(obs, x, a);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("x", new DenseTensor<float>(x, new[] { 1, Features.Nodes, Features.F })),
            NamedOnnxValue.CreateFromTensor("adj", new DenseTensor<float>(a, new[] { 1, Features.Nodes, Features.Nodes })),
        };
        using (var r = _s.Run(inputs)) return r.First().AsTensor<float>().ToArray();
    }
}

/// <summary>Headless advisor-in-the-loop run: the simulated commander accepts every card.</summary>
static class Headless
{
    public static async Task<int> Run(string[] a)
    {
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        int seed = a.Length > 0 ? int.Parse(a[0]) : 7;
        string policy = a.Length > 1 ? a[1] : "accept";       // accept | baseline | none
        string outPath = a.Length > 2 ? a[2] : null;
        int port = a.Length > 3 ? int.Parse(a[3]) : 7390 + (seed % 50);
        string effort = a.Length > 4 ? a[4] : "medium";
        var cfg = seed == 7 ? ScenarioConfig.Default() : ScenarioConfig.Randomized(seed);
        var sim = IncidentSim.Create(cfg);
        var lessons = new LessonStore(Path.Combine(repo, "eval/lessons-headless.json"));
        var tools = new AdvisorTools { Sim = sim, Forecaster = new OnnxForecaster(Path.Combine(repo, "Runaway/Assets/Runaway/Resources/Models/escalation.onnx")), Lessons = lessons };
        var gate = new object();
        using var api = new ApiServer(port, (path, body) => { lock (gate) return Task.FromResult(tools.Route(path, body)); });
        var advisor = new CodexAdvisor(new AdvisorSettings { McpDir = Path.Combine(repo, "advisor"), WorkDir = Path.Combine(Path.GetTempPath(), "runaway-advisor-" + seed), Port = port, Effort = effort });
        var triggers = new DecisionTriggers();
        var decisions = new List<DecisionRecord>();
        int lastEv = 0;
        while (!sim.Finished)
        {
            lock (gate) sim.Step();
            for (; lastEv < sim.Events.Count; lastEv++) { var e = sim.Events[lastEv]; if (e.Kind != "order") Console.WriteLine($"T+{e.Time / 60,5:F1} {(e.Hidden ? "*" : " ")}{e.Text}"); }
            if (policy == "baseline" && ((int)sim.Time % 60) == 0) lock (gate) BaselinePolicy.Act(sim);
            if (policy != "accept") continue;
            string trig; lock (gate) trig = triggers.Evaluate(sim);
            if (trig == null) continue;
            tools.Trigger = trig;
            var recent = string.Join("\n", decisions.TakeLast(3).Select(d => $"T+{d.Time / 60:F0}: {d.Card?.Headline} [{d.Response}]"));
            if (recent.Length == 0) recent = "(none)";
            Console.WriteLine($"--- decision {decisions.Count + 1} at T+{sim.Minutes:F1}: {trig}");
            var res = await advisor.Plan(trig, recent);
            var rec = new DecisionRecord { Number = decisions.Count + 1, Time = sim.Time, Trigger = trig, AdvisorSeconds = res.Seconds, ToolCalls = res.ToolCalls };
            if (!res.Ok) { rec.Response = "failed"; rec.Error = res.Error; Console.WriteLine("   advisor failed: " + res.Error); decisions.Add(rec); continue; }
            rec.Card = res.Card;
            lock (gate)
            {
                foreach (var o in res.Card.ToOrders()) { var r = sim.Apply(o); if (r.Ok) rec.Applied.Add(o); else Console.WriteLine("   rejected: " + r.Error); }
            }
            rec.Response = "accepted";
            if (res.Card.LessonsCited.Count > 0) lessons.MarkCited(res.Card.LessonsCited);
            decisions.Add(rec);
            Console.WriteLine($"   [{res.Seconds:F0}s, tools: {string.Join(",", res.ToolCalls)}] {res.Card.Headline}");
            foreach (var x in res.Card.Actions) Console.WriteLine($"     - {x.Describe(sim)}: {x.Reason}");
            Console.WriteLine($"     why: {res.Card.Rationale}");
            foreach (var x in res.Card.SensorConcerns) Console.WriteLine($"     sensor: {x}");
            foreach (var x in res.Card.ExpectedEffect) Console.WriteLine($"     effect: {x.Label}: {x.Without} -> {x.With}");
        }
        var o2 = sim.Outcome();
        Console.WriteLine($"OUTCOME lost={o2.Lost} defl={o2.Deflagrations} inj={o2.Injuries} wd={o2.Withdrawals} contained={o2.ContainedMin:F0} worstIndoor={o2.WorstIndoor:F2} dose={o2.TotalDose:F0}");
        foreach (var r in o2.Receptors) Console.WriteLine($"   {r.Name}: outdoor {r.PeakOutdoor:F2} indoor {r.PeakIndoor:F2} dose {r.Dose:F0} ({r.Action})");
        if (outPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            File.WriteAllText(outPath, JsonConvert.SerializeObject(new { seed, policy, outcome = o2, decisions, events = sim.Events.Where(e => e.Kind != "order") }, Formatting.Indented));
        }
        return 0;
    }
}
