using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Runaway.Sim;

namespace Runaway.Advisor
{
    /// <summary>Decides when the incident has changed enough to ask the advisor again. Observations only.</summary>
    public sealed class DecisionTriggers
    {
        readonly HashSet<string> _seen = new HashSet<string>();
        double _last = -1e9;
        public int Count;
        public double CooldownMin = 4;
        public double QuietMin = 15;

        public string Evaluate(IncidentSim sim)
        {
            var obs = sim.Obs;
            var now = sim.Time;
            var urgent = new List<string>();
            var normal = new List<string>();

            foreach (var e in sim.Events.Where(e => e.Time > now - Phys.Dt - 0.1 && !e.Hidden))
            {
                if (e.Kind == "venting" || e.Kind == "fire" || e.Kind == "deflagration" || e.Kind == "injury") urgent.Add(e.Text);
                else if (e.Kind == "withdrawn") urgent.Add(e.Text);
                else if (e.Kind == "arrival" && Once("arr:" + e.Subject))
                {
                    var u = sim.UnitById(e.Subject);
                    if (u?.Id == "P1") urgent.Add("Pumper 1 is the first crew on scene. Time for a first plan.");
                    else if (u?.Id == "A1") normal.Add("Aerial 1 has arrived. Its thermal camera can now check temperatures and see where the smoke goes.");
                    else if (u?.Id == "H1") normal.Add("HAZMAT 1 has arrived. It can measure gas and the smoke reaching nearby homes.");
                    else if (u?.Id == "P3") normal.Add("Pumper 3 has arrived. Its tanker means a fourth hose can run.");
                    else if (u != null && !u.Injured && e.Text.Contains("rehab")) normal.Add(e.Text);
                }
            }

            if (obs.SmokeToward.HasValue)
            {
                var smokeFrom = Angle.Wrap(obs.SmokeToward.Value + 180);
                if (Math.Abs(Angle.Delta(obs.StationFrom, smokeFrom)) > 25 && Once("wind_conflict"))
                    urgent.Add($"Smoke is blowing {Angle.Compass(obs.SmokeToward.Value)}, but the site wind sensor still says the wind is from the {Angle.Compass(obs.StationFrom)}.");
            }
            foreach (var c in obs.Containers)
            {
                if (c.Visible == "normal" && c.Estimate > 100 && (c.RatePerMin ?? 1) > 0 && Once("hot:" + c.Id))
                    normal.Add($"{c.Id} is heating up. It is at {c.Estimate:F0}°C and rising.");
                if (c.SensorConflict && Once("conflict:" + c.Id))
                    urgent.Add($"{c.Id}'s battery sensor reads {c.Bms:F0}°C, but the thermal camera shows about {14 + (c.Thermal - 14) / 0.6:F0}°C.");
            }
            foreach (var u in sim.Units)
                if (u.Available && u.Exposure > 0.7 * Phys.ExposureLimit && Once("exp:" + u.Id + ":" + (int)(u.RehabUntil)))
                    normal.Add($"{u.Name} crew has taken {u.Exposure / Phys.ExposureLimit * 100:F0}% of their safe smoke exposure.");
            foreach (var r in obs.Receptors)
                if (r.Measured.HasValue && r.Measured.Value > 0.5 && r.Action == Protection.None && Once("hf:" + r.Id))
                    urgent.Add($"Toxic smoke has reached {r.Name}. HAZMAT measures {r.Measured.Value:F1} ppm.");

            if (sim.Events.Any(e => e.Kind == "arrival" && e.Subject == "P1") == false) return null; // nobody to command yet
            var since = (now - _last) / 60.0;
            string reason = null;
            if (urgent.Count > 0 && since >= 1.5) reason = string.Join(" ", urgent.Concat(normal).Take(3));
            else if (normal.Count > 0 && since >= CooldownMin) reason = string.Join(" ", normal.Take(3));
            else if (since >= QuietMin && sim.Containers.Any(c => c.State == CellState.Fire || c.State == CellState.Venting)) reason = "Regular check that the plan is still right.";
            else if (urgent.Count + normal.Count > 0) { foreach (var x in urgent.Concat(normal)) _pending.Add(x); }

            if (reason == null && _pending.Count > 0 && since >= CooldownMin) { reason = string.Join(" ", _pending.Take(3)); }
            if (reason != null) { _pending.Clear(); _last = now; Count++; }
            return reason;
        }
        readonly List<string> _pending = new List<string>();

        bool Once(string key) => _seen.Add(key);
    }

    public sealed class DecisionRecord
    {
        public int Number;
        public double Time;
        public string Trigger;
        public Recommendation Card;
        public List<Caution> Cautions = new List<Caution>();
        public List<string> ToolCalls = new List<string>();
        public double AdvisorSeconds;
        public string Response = "pending";        // accepted | edited | dismissed | expired | failed
        public List<Order> Applied = new List<Order>();
        public string Error;
        [JsonIgnore] public IncidentSim Snapshot;
        public Outcome Counterfactual;             // outcome if the advice had been taken in full (or ignored, when accepted)
        public string CounterfactualLabel;
    }

    public static class Counterfactual
    {
        /// <summary>Replays from a decision snapshot with different orders at that point and every later order unchanged.</summary>
        public static Outcome Replay(IncidentSim snapshot, IList<Order> atDecision, IEnumerable<Order> later, double untilSeconds)
        {
            var s = snapshot.Clone();
            s.Lightweight = false;
            foreach (var o in atDecision) s.Apply(o.Clone());
            var queue = later.Where(o => o.Time > snapshot.Time).OrderBy(o => o.Time).Select(o => o.Clone()).ToList();
            int k = 0;
            while (!s.Finished && s.Time < untilSeconds)
            {
                while (k < queue.Count && queue[k].Time <= s.Time + 1e-6) { s.Apply(queue[k]); k++; }
                s.Step();
            }
            return s.Outcome();
        }
    }

    public static class DebriefBuilder
    {
        public static string Build(IncidentSim sim, IList<DecisionRecord> decisions, string condition)
        {
            var o = sim.Outcome();
            var j = new JObject
            {
                ["condition"] = condition,
                ["outcome"] = JObject.FromObject(new
                {
                    enclosures_lost = o.Lost,
                    deflagrations = o.Deflagrations,
                    crew_injuries = o.Injuries,
                    crew_withdrawals = o.Withdrawals,
                    contained_at_min = o.ContainedMin,
                    communities = o.Receptors.Select(r => new { r.Name, peak_outdoor_ppm = Math.Round(r.PeakOutdoor, 2), peak_indoor_ppm = Math.Round(r.PeakIndoor, 2), dose_ppm_min = Math.Round(r.Dose, 0), action = r.Action }),
                }),
                ["hidden_ground_truth"] = new JArray(sim.Events.Where(e => e.Hidden).Select(e => $"T+{e.Time / 60:F0} {e.Text}")),
                ["timeline"] = new JArray(sim.Events.Where(e => !e.Hidden && e.Kind != "order").Select(e => $"T+{e.Time / 60:F0} {e.Text}")),
                ["decisions"] = new JArray(decisions.Select(d => new JObject
                {
                    ["time"] = $"T+{d.Time / 60:F0}",
                    ["trigger"] = d.Trigger,
                    ["advice"] = d.Card?.Headline,
                    ["actions"] = new JArray((d.Card?.Actions ?? new List<PlanAction>()).Select(a => a.Describe(sim))),
                    ["sensor_concerns"] = new JArray(d.Card?.SensorConcerns ?? new List<string>()),
                    ["commander"] = d.Response,
                    ["applied"] = new JArray(d.Applied.Select(x => x.ToString())),
                    ["counterfactual"] = d.Counterfactual == null ? null : (JToken)$"{d.CounterfactualLabel}: {d.Counterfactual.Lost} enclosures lost, worst indoor HF {d.Counterfactual.WorstIndoor:F1} ppm",
                })),
                ["commander_orders_outside_advice"] = new JArray(sim.OrderLog.Where(x => x.Source == "commander").Select(x => $"T+{x.Time / 60:F0} {x}")),
            };
            return j.ToString(Formatting.Indented);
        }
    }
}
