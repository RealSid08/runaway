using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Runaway.Sim;

namespace Runaway.Advisor
{
    public interface IForecaster
    {
        /// <summary>Returns [12, 2] probabilities of runaway within 10 and 20 minutes.</summary>
        float[] Predict(Observation obs);
    }

    /// <summary>
    /// The advisor's view of the incident. Every tool reads observations only; ground truth stays
    /// inside the simulation. The same code serves the Unity app and headless evaluation.
    /// </summary>
    public sealed class AdvisorTools
    {
        public IncidentSim Sim;
        public IForecaster Forecaster;
        public LessonStore Lessons;
        public string Trigger;
        public List<string> TestLog = new List<string>();

        static string T(double s) => $"T+{s / 60:F0}";
        static double R(double v, int d = 1) => Math.Round(v, d);

        public string Situation()
        {
            var obs = Sim.Obs;
            var j = new JObject
            {
                ["incident_time"] = $"T+{obs.Time / 60:F1} min",
                ["decision_trigger"] = Trigger,
                ["streams"] = new JObject { ["in_use"] = obs.StreamsInUse, ["capacity"] = obs.StreamCapacity, ["note"] = "Hydrant supply supports 3 streams; Pumper 3's tanker adds a fourth once on scene." },
                ["wind"] = WindJson(obs),
                ["enclosures"] = new JArray(obs.Containers.Select(c => new JObject
                {
                    ["id"] = c.Id,
                    ["row"] = c.Index < 6 ? "A (north row)" : "B (south row)",
                    ["visible"] = c.Visible,
                    ["bms_temp_c"] = c.Bms.HasValue ? (JToken)R(c.Bms.Value) : null,
                    ["bms_trend_c_per_min"] = c.RatePerMin.HasValue ? (JToken)R(c.RatePerMin.Value) : null,
                    ["thermal_camera_skin_c"] = c.Thermal.HasValue ? (JToken)R(c.Thermal.Value) : null,
                    ["gas_detector_pct_lel"] = R(c.GasRaw),
                    ["gas_verified_pct_lel"] = c.GasVerified.HasValue ? (JToken)R(c.GasVerified.Value) : null,
                    ["cooled_by"] = c.CooledBy,
                })),
                ["units"] = new JArray(Sim.Units.Select(u => new JObject
                {
                    ["id"] = u.Id,
                    ["name"] = u.Name,
                    ["kind"] = u.Kind.ToString().ToLowerInvariant(),
                    ["status"] = UnitStatusText(u),
                    ["assigned_to"] = u.Target >= 0 ? Site.ContainerId(u.Target) : null,
                    ["eta_min"] = u.OnScene ? null : (JToken)R((u.ArriveAt - Sim.Time) / 60, 0),
                    ["crew_exposure_pct"] = R(u.Exposure / Phys.ExposureLimit * 100, 0),
                    ["position"] = u.OnScene ? $"{u.Pos.X:F0} m E, {u.Pos.Z:F0} m N" : "en route",
                })),
                ["communities"] = new JArray(obs.Receptors.Select(r =>
                {
                    var rc = Sim.ReceptorById(r.Id);
                    var rel = rc.Pos;
                    return new JObject
                    {
                        ["id"] = r.Id,
                        ["name"] = r.Name,
                        ["people"] = rc.People,
                        ["distance_m"] = R(rel.Length, 0),
                        ["bearing_from_site_deg"] = R(V2.Bearing(rel), 0),
                        ["protective_action"] = r.Action.ToString().ToLowerInvariant(),
                        ["air_monitoring_hf_ppm"] = r.Measured.HasValue ? (JToken)R(r.Measured.Value, 2) : null,
                    };
                })),
                ["geometry"] = "Two rows of six enclosures (A1-A6 north at z=+4 m, B1-B6 south at z=-4 m), 7 m pitch west to east (1 is west). End-to-end gap within a row is 0.9 m; the aisle between rows is 5.6 m. Gates: north, west, south, east.",
                ["recent_events"] = new JArray(Sim.Events.Where(e => !e.Hidden && e.Kind != "order").Reverse().Take(10).Reverse().Select(e => $"{T(e.Time)} {e.Text}")),
                ["thresholds"] = "HF: 1 ppm discomfort (AEGL-1, 1 h), 24 ppm serious effects (AEGL-2, 1 h). Crew exposure 100% forces withdrawal for 15 min rehab. Deflagration radius about 17 m.",
            };
            return j.ToString(Formatting.None);
        }

        static JObject WindJson(Observation obs) => new JObject
        {
            ["anemometer_from_deg"] = R(obs.StationFrom, 0),
            ["anemometer_speed_ms"] = R(obs.StationSpeed),
            ["smoke_drifting_toward_deg"] = obs.SmokeToward.HasValue ? (JToken)R(obs.SmokeToward.Value, 0) : null,
            ["note"] = obs.SmokeToward.HasValue ? "Smoke drift is reported by Aerial 1's crew from the ladder." : "No smoke drift report yet; Aerial 1 reports it once on scene and a plume is visible.",
        };

        static string UnitStatusText(Unit u)
        {
            if (u.Injured) return "injured, out of service";
            switch (u.Status)
            {
                case UnitStatus.EnRoute: return u.Target >= 0 ? "en route, pre-assigned" : "en route";
                case UnitStatus.Staged: return $"staged at {Site.StagingName(u.Staging)}";
                case UnitStatus.Moving: return u.Target >= 0 ? "repositioning to hose position" : "moving to staging";
                case UnitStatus.SettingUp: return "setting up stream";
                case UnitStatus.Cooling: return "flowing water";
                case UnitStatus.Withdrawn: return "withdrawn to rehab";
            }
            return u.Status.ToString();
        }

        public string SensorCheck()
        {
            var obs = Sim.Obs;
            var rows = new JArray();
            foreach (var c in obs.Containers)
            {
                string bms = c.Bms.HasValue ? $"{c.Bms.Value:F0} C" : "no reading";
                string implied = c.Thermal.HasValue ? $"{14 + (c.Thermal.Value - 14) / 0.6:F0} C" : "camera not available";
                string flag = null;
                if (c.SensorConflict) flag = "BMS and thermal camera disagree beyond sensor noise";
                else if (c.Bms.HasValue && c.RatePerMin.HasValue && Math.Abs(c.RatePerMin.Value) < 0.05 && c.Visible == "normal")
                {
                    bool nearFire = obs.Containers.Any(o => (o.Visible == "fire" || o.Visible == "smoke") && Site.GapCached(o.Index, c.Index) < 7);
                    if (nearFire) flag = "BMS reading is flat next to an involved enclosure";
                }
                rows.Add(new JObject
                {
                    ["id"] = c.Id,
                    ["bms"] = bms,
                    ["thermal_skin"] = c.Thermal.HasValue ? $"{c.Thermal.Value:F0} C" : null,
                    ["module_temp_implied_by_camera"] = implied,
                    ["gas_detector_vs_hazmat"] = c.GasVerified.HasValue ? $"{c.GasRaw:F0} vs {c.GasVerified.Value:F0} %LEL" : $"{c.GasRaw:F0} %LEL (unverified, detector noise is about 6)",
                    ["flag"] = flag,
                });
            }
            string wind = "No independent wind check yet.";
            if (obs.SmokeToward.HasValue)
            {
                var smokeFrom = Angle.Wrap(obs.SmokeToward.Value + 180);
                var d = Math.Abs(Angle.Delta(obs.StationFrom, smokeFrom));
                wind = d > 25
                    ? $"Disagreement: anemometer says from {obs.StationFrom:F0}, smoke drift implies from {smokeFrom:F0} ({d:F0} deg apart)."
                    : $"Anemometer and smoke drift agree within {d:F0} deg.";
            }
            var j = new JObject
            {
                ["enclosures"] = rows,
                ["wind"] = wind,
                ["camera_note"] = "The thermal camera reads enclosure skin. Module temperature is roughly ambient + (skin - ambient) / 0.6. Ambient is 14 C.",
            };
            return j.ToString(Formatting.None);
        }

        public string Forecast()
        {
            var obs = Sim.Obs;
            var p = Forecaster?.Predict(obs);
            var j = new JObject
            {
                ["model"] = "Graph neural network over the 12 enclosures, trained on 4,000 simulated incidents. Uses observations only. Calibration error under 1% on held-out incidents.",
                ["wind_assumed"] = $"from {obs.BestWindFrom:F0} deg ({(obs.SmokeToward.HasValue ? "smoke drift" : "anemometer")})",
            };
            var arr = new JArray();
            for (int i = 0; i < 12; i++)
            {
                var c = obs.Containers[i];
                if (c.Visible != "normal") { arr.Add(new JObject { ["id"] = c.Id, ["status"] = c.Visible }); continue; }
                arr.Add(new JObject
                {
                    ["id"] = c.Id,
                    ["p_runaway_10min"] = p == null ? null : (JToken)R(p[i * 2], 2),
                    ["p_runaway_20min"] = p == null ? null : (JToken)R(p[i * 2 + 1], 2),
                });
            }
            j["enclosures"] = arr;
            return j.ToString(Formatting.None);
        }

        public string TestPlan(string body)
        {
            var req = JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var actions = req["actions"]?.ToObject<List<PlanAction>>() ?? new List<PlanAction>();
            var orders = actions.Select(a => a.ToOrder()).Where(o => o != null).ToList();
            var a = new Assumptions();
            if (req["assume_wind_from_deg"] != null && req["assume_wind_from_deg"].Type != JTokenType.Null) a.WindFrom = req["assume_wind_from_deg"].Value<double>();
            if (req["assume_temps"] is JObject temps) foreach (var kv in temps) a.Temps[kv.Key.ToUpperInvariant()] = kv.Value.Value<double>();
            var horizon = req["horizon_min"]?.Type == JTokenType.Integer || req["horizon_min"]?.Type == JTokenType.Float ? Math.Max(5, Math.Min(40, req["horizon_min"].Value<double>())) : 20;
            var rep = Rollouts.Compare(Sim, orders, a, horizon, 16);
            TestLog.Add(string.Join(", ", actions.Select(x => x.Describe(Sim))));
            var j = new JObject
            {
                ["horizon_min"] = horizon,
                ["samples"] = rep.Samples,
                ["belief"] = $"Built from observations only. Wind: {rep.WindUsed}. Unknown faults and future wind changes are not modelled unless you assume them.",
                ["enclosures_involved_at_horizon"] = new JObject { ["current_orders"] = R(rep.BaselineLost), ["with_plan"] = R(rep.PlanLost) },
                ["p_runaway_by_enclosure"] = new JArray(rep.Containers.Where(c => c.Baseline > 0.02 || c.WithPlan > 0.02).Select(c => new JObject { ["id"] = c.Id, ["current_orders"] = R(c.Baseline, 2), ["with_plan"] = R(c.WithPlan, 2) })),
                ["community_peak_hf_ppm_indoors"] = new JArray(rep.Receptors.Select(r => new JObject { ["id"] = r.Id, ["current_orders"] = R(r.BaselinePeak, 2), ["with_plan"] = R(r.PlanPeak, 2) })),
                ["p_new_deflagration"] = new JObject { ["current_orders"] = R(rep.BaselineDeflagration, 2), ["with_plan"] = R(rep.PlanDeflagration, 2) },
                ["max_crew_exposure_pct"] = new JObject { ["current_orders"] = R(rep.BaselineMaxExposure / Phys.ExposureLimit * 100, 0), ["with_plan"] = R(rep.PlanMaxExposure / Phys.ExposureLimit * 100, 0) },
                ["cautions"] = new JArray(rep.Cautions.Select(c => (c.Blocking ? "BLOCKED: " : "CAUTION: ") + c.Message)),
            };
            return j.ToString(Formatting.None);
        }

        public string ReadLessons()
        {
            var j = new JObject
            {
                ["lessons"] = new JArray((Lessons?.All ?? new List<Lesson>()).Select(l => new JObject { ["id"] = l.Id, ["title"] = l.Title, ["lesson"] = l.Text, ["applies_when"] = l.When })),
                ["note"] = "Lessons were written after earlier exercises. Cite an id only if it changed what you recommend.",
            };
            return j.ToString(Formatting.None);
        }

        public string Route(string path, string body)
        {
            switch (path)
            {
                case "/situation": return Situation();
                case "/sensors": return SensorCheck();
                case "/forecast": return Forecast();
                case "/test_plan": return TestPlan(body);
                case "/lessons": return ReadLessons();
                default: return new JObject { ["error"] = $"unknown endpoint {path}" }.ToString();
            }
        }
    }
}
