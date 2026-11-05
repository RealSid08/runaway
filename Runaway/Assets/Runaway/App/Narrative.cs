using System;
using System.Collections.Generic;
using System.Linq;
using Runaway.Sim;

namespace Runaway.App
{
    /// <summary>Plain-language description of what the commander can currently see. Observations only.</summary>
    public static class Narrative
    {
        static readonly string[] Points = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };

        public static string Compass(double bearing) => Points[(int)Math.Round(Angle.Wrap(bearing) / 45.0) % 8];

        static string Join(IList<string> ids)
        {
            if (ids.Count == 1) return ids[0];
            if (ids.Count == 2) return ids[0] + " and " + ids[1];
            return string.Join(", ", ids.Take(ids.Count - 1)) + " and " + ids.Last();
        }

        public static List<(string text, bool warn)> Now(IncidentSim sim, float[] risk)
        {
            var obs = sim.Obs;
            var lines = new List<(string, bool)>();
            var fire = obs.Containers.Where(c => c.Visible == "fire").Select(c => c.Id).ToList();
            var venting = obs.Containers.Where(c => c.Visible == "smoke").Select(c => c.Id).ToList();
            var burnt = obs.Containers.Where(c => c.Visible == "burnt_out").Select(c => c.Id).ToList();

            if (sim.ContainedAt >= 0) lines.Add(("The spread has stopped. No other enclosure is heating up.", false));

            if (fire.Count > 0) lines.Add(($"{Join(fire)} {(fire.Count == 1 ? "is" : "are")} on fire.", true));
            if (venting.Count > 0) lines.Add(($"{Join(venting)} {(venting.Count == 1 ? "is" : "are")} venting gas and could ignite.", true));
            if (fire.Count == 0 && venting.Count == 0 && burnt.Count == 0)
            {
                var hot = obs.Containers.OrderByDescending(c => c.Estimate).First();
                lines.Add(hot.Estimate > 45
                    ? ($"The battery in {hot.Id} is overheating. It reads {hot.Estimate:F0}°C and the normal is under 40.", true)
                    : ("All enclosures read normal temperatures.", false));
            }
            if (burnt.Count > 0 && fire.Count == 0 && venting.Count == 0) lines.Add(($"{Join(burnt)} {(burnt.Count == 1 ? "has" : "have")} burnt out.", false));

            if (fire.Count + venting.Count > 0 && sim.ContainedAt < 0)
            {
                var heating = obs.Containers.Where(c => c.Visible == "normal" && (c.Estimate > 60 || (risk != null && risk[c.Index * 2 + 1] > 0.35f)))
                    .OrderByDescending(c => c.Estimate).Select(c => c.Id).Take(3).ToList();
                if (heating.Count > 0) lines.Add(($"{Join(heating)} {(heating.Count == 1 ? "is" : "are")} heating up and could catch fire next.", true));
                else lines.Add(("No neighbouring enclosure is heating up yet.", false));
            }

            if (obs.SmokeToward.HasValue && fire.Count + venting.Count > 0)
            {
                var to = obs.SmokeToward.Value;
                string toward = null;
                foreach (var r in sim.Receptors)
                    if (Math.Abs(Angle.Delta(to, V2.Bearing(r.Pos))) < 22) toward = r.Name;
                lines.Add(($"Smoke is blowing {Compass(to)}{(toward != null ? $" toward {toward}" : "")}.", toward != null));
                var smokeFrom = Angle.Wrap(to + 180);
                if (Math.Abs(Angle.Delta(obs.StationFrom, smokeFrom)) > 25)
                    lines.Add(("The site wind sensor disagrees with the smoke and may have failed.", true));
            }
            return lines;
        }

        public static string Wind(Observation obs)
        {
            var from = obs.BestWindFrom;
            return $"Wind from the {Compass(from)}, {obs.StationSpeed:F0} m/s";
        }
    }
}
