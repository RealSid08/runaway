using System;
using System.Linq;
using Runaway.Sim;

static class Program
{
    static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "calibrate";
        switch (mode)
        {
            case "calibrate": Calibrate(args.Skip(1).ToArray()); return 0;
            case "advise": return Headless.Run(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            case "sweep": Sweep.Run(int.Parse(args[1]), int.Parse(args[2]), args[3]); return 0;
            case "datagen": DataGen.Run(int.Parse(args[1]), args[2], args.Length > 3 ? int.Parse(args[3]) : 1000); return 0;
            default: Console.Error.WriteLine("modes: calibrate"); return 2;
        }
    }

    static void Calibrate(string[] a)
    {
        foreach (var policy in new[] { "none", "baseline" })
        {
            var sim = IncidentSim.Create(ScenarioConfig.Default());
            Console.WriteLine($"=== policy {policy}");
            int lastEv = 0;
            while (!sim.Finished)
            {
                if (policy == "baseline" && ((int)sim.Time % 60) == 0) BaselinePolicy.Act(sim);
                sim.Step();
                for (; lastEv < sim.Events.Count; lastEv++)
                {
                    var e = sim.Events[lastEv];
                    if (e.Kind == "order") continue;
                    Console.WriteLine($"  T+{e.Time/60,5:F1} {(e.Hidden?"*":" ")}{e.Kind,-12} {e.Text}");
                }
                if (((int)sim.Time % 300) == 0)
                {
                    var hot = string.Join(" ", sim.Containers.Select(c => $"{c.Id}:{(c.State==CellState.Normal? c.T.ToString("F0") : c.State.ToString().Substring(0,1))}"));
                    var rc = string.Join(" ", sim.Receptors.Select(r => $"{r.Id}={r.Ppm:F2}"));
                    Console.WriteLine($"  [T+{sim.Minutes:F0}] wind {sim.Wind.From:F0}/{sim.Wind.Speed:F1} stn {sim.Wind.StationFrom:F0} | {hot} | {rc}");
                }
            }
            var o = sim.Outcome();
            Console.WriteLine($"  OUTCOME lost={o.Lost} defl={o.Deflagrations} inj={o.Injuries} wd={o.Withdrawals} maxExp={o.MaxCrewExposure:F0} contained={o.ContainedMin} water={o.WaterKl:F0}kL");
            foreach (var r in o.Receptors) Console.WriteLine($"     {r.Name}: peak {r.PeakOutdoor:F2} ppm, indoor {r.PeakIndoor:F2}, dose {r.Dose:F0} ppm-min ({r.Action})");
        }
    }
}
