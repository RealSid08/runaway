using System;

namespace Runaway.Sim
{
    /// <summary>
    /// Graph features for the escalation forecaster, built only from observations. Shared byte-for-byte
    /// by the headless data generator and the in-engine inference path.
    /// </summary>
    public static class Features
    {
        public const int Nodes = 12;
        public const int F = 12;

        public static void Build(Observation obs, float[] x, float[] adj)
        {
            var windFrom = obs.BestWindFrom;
            var dw = V2.FromBearing(windFrom + 180);
            var spd = obs.StationSpeed;
            for (int i = 0; i < Nodes; i++)
            {
                var c = obs.Containers[i];
                int o = i * F;
                double est = c.Estimate;
                x[o + 0] = (float)(est / 400.0);
                x[o + 1] = (float)Math.Max(-1, Math.Min(3, (c.RatePerMin ?? 0) / 20.0));
                x[o + 2] = c.Thermal.HasValue ? 1f : 0f;
                double conflict = 0;
                if (c.Bms.HasValue && c.Thermal.HasValue) conflict = (14 + (c.Thermal.Value - 14) / 0.6 - c.Bms.Value) / 200.0;
                x[o + 3] = (float)Math.Max(-2, Math.Min(2, conflict));
                x[o + 4] = (float)(c.Gas / 100.0);
                x[o + 5] = c.Visible == "smoke" ? 1f : 0f;
                x[o + 6] = c.Visible == "fire" ? 1f : 0f;
                x[o + 7] = c.Visible == "burnt_out" ? 1f : 0f;
                x[o + 8] = c.CooledBy != null ? 1f : 0f;
                double rad = 0;
                for (int j = 0; j < Nodes; j++)
                {
                    if (j == i) continue;
                    var s = obs.Containers[j];
                    double I = s.Visible == "fire" ? 1.0 : s.Visible == "smoke" ? 0.18 : 0;
                    if (I <= 0) continue;
                    var wf = Phys.WindFactor(Site.ContainerPos(j), Site.ContainerPos(i), dw, spd);
                    rad += Phys.Radiant(I, Site.GapCached(i, j), wf);
                }
                x[o + 9] = (float)Math.Min(3, rad / 0.25);
                x[o + 10] = (float)(spd / 10.0);
                x[o + 11] = (float)Math.Min(1.5, obs.Time / 5400.0);
            }
            for (int i = 0; i < Nodes; i++)
            {
                double sum = 0;
                for (int j = 0; j < Nodes; j++)
                {
                    double w = 0;
                    if (i != j)
                    {
                        var wf = Phys.WindFactor(Site.ContainerPos(j), Site.ContainerPos(i), dw, spd);
                        w = Math.Exp(-Site.GapCached(i, j) / 4.0) * wf;
                    }
                    adj[i * Nodes + j] = (float)w;
                    sum += w;
                }
                if (sum > 0) for (int j = 0; j < Nodes; j++) adj[i * Nodes + j] = (float)(adj[i * Nodes + j] / sum);
            }
        }
    }

    /// <summary>Rule-of-thumb response used as the evaluation baseline and to diversify training data.</summary>
    public static class BaselinePolicy
    {
        /// <summary>Cool the hottest uninvolved enclosures nearest the fire, using whatever the sensors say.</summary>
        public static void Act(IncidentSim sim)
        {
            var obs = sim.Obs;
            bool anyInvolved = false;
            foreach (var c in obs.Containers) if (c.Visible != "normal") anyInvolved = true;
            var ranked = new System.Collections.Generic.List<(int idx, double score)>();
            for (int i = 0; i < 12; i++)
            {
                var c = obs.Containers[i];
                if (c.Visible != "normal") continue;
                double near = 0;
                for (int j = 0; j < 12; j++)
                {
                    var s = obs.Containers[j];
                    if (s.Visible == "fire" || s.Visible == "smoke") near = Math.Max(near, 1.0 / (Site.GapCached(i, j) + 1));
                }
                double score = (anyInvolved ? near * 100 : 0) + c.Estimate;
                ranked.Add((i, score));
            }
            ranked.Sort((a, b) => b.score.CompareTo(a.score));
            int k = 0;
            foreach (var u in sim.Units)
            {
                if (u.StreamPower <= 0 || !u.Available) continue;
                if (u.Target >= 0 && sim.Containers[u.Target].State == CellState.Normal) { k++; continue; }
                while (k < ranked.Count && sim.Units.Exists(x => x.Target == ranked[k].idx)) k++;
                if (k >= ranked.Count) break;
                sim.Apply(new Order { Type = OrderType.Cool, Unit = u.Id, Container = Site.ContainerId(ranked[k].idx), Source = "baseline" });
                k++;
            }
            // public protection by the book: shelter anything the sensors say is affected
            foreach (var r in obs.Receptors)
                if (r.Measured.HasValue && r.Measured.Value > Phys.Aegl1 && r.Action == Protection.None)
                    sim.Apply(new Order { Type = OrderType.Shelter, Receptor = r.Id, Source = "baseline" });
        }
    }
}
