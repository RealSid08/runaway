using System;
using System.Collections.Generic;
using System.Linq;

namespace Runaway.Sim
{
    public sealed class Assumptions
    {
        public double? WindFrom;                                  // override the wind the belief uses
        public double? WindSpeed;
        public Dictionary<string, double> Temps = new Dictionary<string, double>(); // override module temperatures
    }

    public sealed class ContainerRisk
    {
        public string Id;
        public double Baseline;   // P(runaway within horizon) with current orders only
        public double WithPlan;
    }

    public sealed class ReceptorRisk
    {
        public string Id;
        public double BaselinePeak, PlanPeak;       // indoor-adjusted peak ppm, mean over rollouts
        public double BaselineDose, PlanDose;
    }

    public sealed class RolloutReport
    {
        public int Samples;
        public double HorizonMin;
        public string WindUsed;
        public List<ContainerRisk> Containers = new List<ContainerRisk>();
        public List<ReceptorRisk> Receptors = new List<ReceptorRisk>();
        public double BaselineLost, PlanLost;
        public double BaselineDeflagration, PlanDeflagration;
        public double BaselineMaxExposure, PlanMaxExposure;
        public List<Caution> Cautions = new List<Caution>();
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// What-if testing from the commander's belief, never from ground truth. The belief is rebuilt
    /// from observations, so a faulty sensor misleads the rollout exactly as it would mislead a person.
    /// </summary>
    public static class Rollouts
    {
        public static IncidentSim Belief(IncidentSim truth, Assumptions a, Rng rng)
        {
            var obs = truth.Obs;
            var b = truth.Clone();
            b.Lightweight = true;
            b.Events = new List<SimEvent>();
            b.Rng = rng.Clone();
            var cfg = b.Config;
            // the commander does not know the future
            cfg.ShiftStartMin = 1e9; cfg.StationStuckMin = 1e9; cfg.BmsStuckContainer = -1;
            cfg.DurationMin = 1e9;
            var windFrom = a?.WindFrom ?? obs.BestWindFrom;
            var windSpeed = a?.WindSpeed ?? obs.StationSpeed;
            cfg.WindFrom = windFrom; cfg.WindSpeed = windSpeed;
            b.Wind.From = windFrom; b.Wind.Speed = windSpeed; b.Wind.Gust = 0;

            for (int i = 0; i < 12; i++)
            {
                var o = obs.Containers[i];
                var c = b.Containers[i];
                c.FaultHeat = 0;
                c.Gas = o.Gas;
                switch (o.Visible)
                {
                    case "smoke":
                        c.State = CellState.Venting;
                        c.T = Math.Max(Phys.OnsetTemp, o.Estimate);
                        c.VentLeft = rng.Range(60, 360);
                        break;
                    case "fire":
                        c.State = CellState.Fire;
                        c.Intensity = 0.85;   // a visible fire is assumed well developed
                        c.Fuel = rng.Range(0.45, 0.95);
                        c.T = Phys.FireTemp;
                        break;
                    case "burnt_out":
                        c.State = CellState.BurntOut;
                        break;
                    default:
                        c.State = CellState.Normal;
                        c.T = o.Estimate;
                        c.VentLeft = rng.Range(150, 420);
                        // a steady rise with no burning neighbour points to an internal fault
                        if (o.RatePerMin.HasValue && o.RatePerMin.Value > 1.0 && !obs.Containers.Any(x => x.Visible == "fire" || x.Visible == "smoke"))
                            c.FaultHeat = (o.RatePerMin.Value / 60.0 + Phys.PassiveLoss * (c.T - cfg.Ambient)) / Math.Exp((c.T - 60.0) / 30.0);
                        break;
                }
                if (a != null && a.Temps.TryGetValue(c.Id, out var t) && c.State == CellState.Normal) c.T = t;
            }
            return b;
        }

        public static RolloutReport Compare(IncidentSim truth, IList<Order> plan, Assumptions a, double horizonMin = 20, int samples = 16)
        {
            var rep = new RolloutReport { Samples = samples, HorizonMin = horizonMin };
            var obs = truth.Obs;
            var windFrom = a?.WindFrom ?? obs.BestWindFrom;
            rep.WindUsed = a?.WindFrom != null ? $"assumed from {windFrom:F0} deg" : obs.SmokeToward.HasValue ? $"smoke drift (from {windFrom:F0} deg)" : $"site anemometer (from {windFrom:F0} deg)";
            rep.Cautions = Doctrine.Check(truth, plan ?? new List<Order>());

            var baseInv = new double[12]; var planInv = new double[12];
            var rPeakB = new double[truth.Receptors.Count]; var rPeakP = new double[truth.Receptors.Count];
            var rDoseB = new double[truth.Receptors.Count]; var rDoseP = new double[truth.Receptors.Count];
            for (int s = 0; s < samples; s++)
            {
                var r = new Rng((ulong)(s + 1) * 1000003UL + (ulong)truth.Time);
                for (int pass = 0; pass < 2; pass++)
                {
                    var b = Belief(truth, a, r);
                    var startDose = b.Receptors.Select(x => x.Dose).ToArray();
                    foreach (var x in b.Receptors) x.PeakIndoor = 0;
                    if (pass == 1 && plan != null) foreach (var o in plan) { var oc = o.Clone(); oc.Source = "test"; b.Apply(oc); }
                    var steps = (int)(horizonMin * 60 / Phys.Dt);
                    for (int k = 0; k < steps; k++) b.Step();
                    var inv = pass == 0 ? baseInv : planInv;
                    for (int i = 0; i < 12; i++) if (b.Containers[i].Involved) inv[i] += 1.0 / samples;
                    var lost = b.Containers.Count(c => c.Involved);
                    for (int k = 0; k < b.Receptors.Count; k++)
                    {
                        (pass == 0 ? rPeakB : rPeakP)[k] += b.Receptors[k].PeakIndoor / samples;
                        (pass == 0 ? rDoseB : rDoseP)[k] += (b.Receptors[k].Dose - startDose[k]) / samples;
                    }
                    if (pass == 0) { rep.BaselineLost += lost / (double)samples; rep.BaselineDeflagration += (b.Deflagrations > truth.Deflagrations ? 1.0 : 0) / samples; rep.BaselineMaxExposure += b.Units.Max(u => u.Exposure) / samples; }
                    else { rep.PlanLost += lost / (double)samples; rep.PlanDeflagration += (b.Deflagrations > truth.Deflagrations ? 1.0 : 0) / samples; rep.PlanMaxExposure += b.Units.Max(u => u.Exposure) / samples; }
                }
            }
            for (int i = 0; i < 12; i++)
            {
                if (obs.Containers[i].Visible != "normal") continue;
                rep.Containers.Add(new ContainerRisk { Id = Site.ContainerId(i), Baseline = baseInv[i], WithPlan = planInv[i] });
            }
            for (int k = 0; k < truth.Receptors.Count; k++)
                rep.Receptors.Add(new ReceptorRisk { Id = truth.Receptors[k].Id, BaselinePeak = rPeakB[k], PlanPeak = rPeakP[k], BaselineDose = rDoseB[k], PlanDose = rDoseP[k] });
            return rep;
        }
    }
}
