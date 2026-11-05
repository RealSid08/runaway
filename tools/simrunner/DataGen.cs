using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Runaway.Sim;

/// <summary>Generates graph snapshots and future-involvement labels for the escalation forecaster.</summary>
static class DataGen
{
    sealed class Sample { public float[] X, A; public float[] Y; public float[] M; }

    public static void Run(int runs, string outDir, int seedBase)
    {
        Directory.CreateDirectory(outDir);
        var all = new List<Sample>[runs];
        Parallel.For(0, runs, k => all[k] = OneRun(seedBase + k));
        var samples = all.SelectMany(x => x).ToList();
        using (var fx = new BinaryWriter(File.Create(Path.Combine(outDir, "x.bin"))))
        using (var fa = new BinaryWriter(File.Create(Path.Combine(outDir, "a.bin"))))
        using (var fy = new BinaryWriter(File.Create(Path.Combine(outDir, "y.bin"))))
        using (var fm = new BinaryWriter(File.Create(Path.Combine(outDir, "m.bin"))))
        using (var fr = new BinaryWriter(File.Create(Path.Combine(outDir, "run.bin"))))
        {
            for (int k = 0; k < runs; k++)
                foreach (var s in all[k])
                {
                    foreach (var v in s.X) fx.Write(v);
                    foreach (var v in s.A) fa.Write(v);
                    foreach (var v in s.Y) fy.Write(v);
                    foreach (var v in s.M) fm.Write(v);
                    fr.Write(k);
                }
        }
        File.WriteAllText(Path.Combine(outDir, "meta.json"),
            $"{{\"n\":{samples.Count},\"nodes\":{Features.Nodes},\"f\":{Features.F},\"targets\":[\"involved_10min\",\"involved_20min\"],\"runs\":{runs},\"seed_base\":{seedBase}}}");
        int pos = samples.Sum(s => (int)s.Y.Where((v, i) => i % 2 == 0 && s.M[i / 2] > 0).Sum());
        int tot = samples.Sum(s => (int)s.M.Sum());
        Console.WriteLine($"samples={samples.Count} nodes_masked_in={tot} positives10={pos}");
    }

    static List<Sample> OneRun(int seed)
    {
        var cfg = ScenarioConfig.Randomized(seed);
        cfg.DurationMin = 112;
        var sim = IncidentSim.Create(cfg);
        var rng = new Rng((ulong)seed * 977UL + 3UL);
        var kind = rng.Next();
        string policy = kind < 0.25 ? "none" : kind < 0.6 ? "baseline" : "random";
        double nextAct = rng.Range(60, 240);
        var snaps = new List<(double t, float[] x, float[] a, bool[] visible)>();
        while (!sim.Finished)
        {
            if (sim.Time >= nextAct)
            {
                if (policy == "baseline") BaselinePolicy.Act(sim);
                else if (policy == "random") RandomAct(sim, rng);
                nextAct = sim.Time + rng.Range(60, 360);
            }
            sim.Step();
            if (((int)sim.Time % 60) == 0 && sim.Time <= 90 * 60)
            {
                var x = new float[Features.Nodes * Features.F];
                var a = new float[Features.Nodes * Features.Nodes];
                Features.Build(sim.Obs, x, a);
                var vis = sim.Obs.Containers.Select(c => c.Visible != "normal").ToArray();
                snaps.Add((sim.Time, x, a, vis));
            }
        }
        var res = new List<Sample>();
        foreach (var s in snaps)
        {
            var y = new float[Features.Nodes * 2];
            var m = new float[Features.Nodes];
            for (int i = 0; i < Features.Nodes; i++)
            {
                var c = sim.Containers[i];
                m[i] = s.visible[i] ? 0 : 1;
                double inv = c.VentStart >= 0 ? c.VentStart : double.MaxValue;
                y[i * 2 + 0] = inv <= s.t + 600 ? 1 : 0;
                y[i * 2 + 1] = inv <= s.t + 1200 ? 1 : 0;
            }
            // keep quiet snapshots rarer so the data is not dominated by easy negatives
            bool interesting = s.visible.Any(v => v) || y.Any(v => v > 0) || s.x.Where((v, idx) => idx % Features.F == 0).Any(v => v > 0.2f);
            if (!interesting && (s.t % 300) != 0) continue;
            res.Add(new Sample { X = s.x, A = s.a, Y = y, M = m });
        }
        return res;
    }

    static void RandomAct(IncidentSim sim, Rng rng)
    {
        foreach (var u in sim.Units)
        {
            if (!u.Available || u.StreamPower <= 0) continue;
            var p = rng.Next();
            if (p < 0.55)
            {
                int target;
                var involved = Enumerable.Range(0, 12).Where(i => sim.Obs.Containers[i].Visible != "normal").ToList();
                if (involved.Count > 0 && rng.Next() < 0.7)
                {
                    var src = involved[rng.Int(involved.Count)];
                    var near = Enumerable.Range(0, 12).Where(i => i != src).OrderBy(i => Site.GapCached(src, i) + rng.Range(0, 4)).Take(4).ToList();
                    target = near[rng.Int(near.Count)];
                }
                else target = rng.Int(12);
                sim.Apply(new Order { Type = OrderType.Cool, Unit = u.Id, Container = Site.ContainerId(target), Source = "random" });
            }
            else if (p < 0.7)
                sim.Apply(new Order { Type = OrderType.Stage, Unit = u.Id, Staging = Site.StagingIds[rng.Int(4)], Source = "random" });
        }
        if (rng.Next() < 0.15)
        {
            var r = sim.Receptors[rng.Int(sim.Receptors.Count)];
            sim.Apply(new Order { Type = rng.Next() < 0.6 ? OrderType.Shelter : OrderType.Evacuate, Receptor = r.Id, Source = "random" });
        }
    }
}
