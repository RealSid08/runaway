using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Runaway.Sim;

/// <summary>Runs the rule-based policies over many randomised incidents and writes per-run outcomes.</summary>
static class Sweep
{
    public static void Run(int seedBase, int n, string outPath)
    {
        var rows = new object[n * 2];
        Parallel.For(0, n, k =>
        {
            int seed = seedBase + k;
            foreach (var (policy, idx) in new[] { ("none", 0), ("baseline", 1) })
            {
                var sim = IncidentSim.Create(ScenarioConfig.Randomized(seed));
                while (!sim.Finished)
                {
                    if (policy == "baseline" && ((int)sim.Time % 60) == 0) BaselinePolicy.Act(sim);
                    sim.Step();
                }
                var o = sim.Outcome();
                rows[k * 2 + idx] = new { seed, policy, lost = o.Lost, contained = o.ContainedMin, injuries = o.Injuries, withdrawals = o.Withdrawals, worstIndoor = o.WorstIndoor, dose = o.TotalDose };
            }
        });
        File.WriteAllText(outPath, JsonConvert.SerializeObject(rows, Formatting.Indented));
        foreach (var p in new[] { "none", "baseline" })
        {
            var r = rows.Select(x => (dynamic)x).Where(x => x.policy == p).ToList();
            Console.WriteLine($"{p}: n={r.Count} lost={r.Average(x => (double)x.lost):F2} contained={r.Count(x => x.contained != null) * 100.0 / r.Count:F0}% injuries={r.Average(x => (double)x.injuries):F2} worstIndoor={r.Average(x => (double)x.worstIndoor):F2}");
        }
    }
}
