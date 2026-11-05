using System;
using System.Collections.Generic;
using System.Linq;

namespace Runaway.Sim
{
    public sealed class OrderResult
    {
        public bool Ok;
        public string Error;
        public static OrderResult Fail(string e) => new OrderResult { Ok = false, Error = e };
        public static readonly OrderResult Success = new OrderResult { Ok = true };
    }

    /// <summary>
    /// Authoritative, deterministic incident model. Rendering, UI and the advisor only read it
    /// and send orders; every rule lives here so headless runs, rollouts and replays agree.
    /// </summary>
    public sealed class IncidentSim
    {
        public ScenarioConfig Config;
        public double Time;
        public Container[] Containers;
        public List<Unit> Units;
        public List<Receptor> Receptors;
        public WindState Wind;
        public List<SimEvent> Events = new List<SimEvent>();
        public List<Order> OrderLog = new List<Order>();
        public Rng Rng;
        public Rng ObsRng;
        public bool Finished;
        public double ContainedAt = -1;
        public double LastInvolvementAt;
        public int Deflagrations;
        public bool Lightweight;          // rollouts skip observation building
        public Observation Obs;
        public ObservationHistory History = new ObservationHistory();

        public double Minutes => Time / 60.0;
        public double Duration => Config.DurationMin * 60.0;

        public static IncidentSim Create(ScenarioConfig cfg)
        {
            var s = new IncidentSim { Config = cfg.Clone() };
            s.Rng = new Rng((ulong)cfg.Seed * 2654435761UL + 1UL);
            s.ObsRng = new Rng((ulong)cfg.Seed * 40503UL + 99UL);
            s.Containers = new Container[12];
            for (int i = 0; i < 12; i++)
            {
                var c = new Container
                {
                    Index = i,
                    Id = Site.ContainerId(i),
                    Pos = Site.ContainerPos(i),
                    T = cfg.Ambient + s.Rng.Range(4, 9),
                    BurnSeconds = s.Rng.Range(48, 72) * 60,
                    VentLeft = s.Rng.Range(150, 420),
                };
                s.Containers[i] = c;
            }
            var init = s.Containers[Math.Max(0, Math.Min(11, cfg.InitialContainer))];
            init.T = cfg.InitialTemp;
            init.FaultHeat = cfg.FaultHeat;

            s.Units = Site.DefaultUnits(cfg);
            foreach (var u in s.Units) u.Pos = Site.StagingPos("north") + new V2(0, 260);
            s.Receptors = Site.DefaultReceptors();
            s.Wind = new WindState { From = cfg.WindFrom, Speed = cfg.WindSpeed, StationFrom = cfg.WindFrom, StationSpeed = cfg.WindSpeed };
            s.LastInvolvementAt = 0;
            s.Log("start", $"The battery in {init.Id} is overheating. The site operator has raised the alarm.", init.Id);
            s.BuildObservation();
            return s;
        }

        public IncidentSim Clone()
        {
            var s = (IncidentSim)MemberwiseClone();
            s.Config = Config.Clone();
            s.Containers = Containers.Select(c => c.Clone()).ToArray();
            s.Units = Units.Select(u => u.Clone()).ToList();
            s.Receptors = Receptors.Select(r => r.Clone()).ToList();
            s.Wind = Wind.Clone();
            s.Events = Events.Select(e => e.Clone()).ToList();
            s.OrderLog = OrderLog.Select(o => o.Clone()).ToList();
            s.Rng = Rng.Clone();
            s.ObsRng = ObsRng.Clone();
            s.History = History.Clone();
            return s;
        }

        public Unit UnitById(string id) => Units.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase));
        public Receptor ReceptorById(string id) => Receptors.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        public int StreamCapacity => Units.Any(u => u.Id == "P3" && u.OnScene) ? 4 : 3;
        public int StreamsInUse => Units.Count(u => u.UsesStream && u.StreamPower > 0);

        void Log(string kind, string text, string subject = null, bool hidden = false)
        {
            if (Lightweight) return;
            Events.Add(new SimEvent { Time = Time, Kind = kind, Text = text, Subject = subject, Hidden = hidden });
        }

        // ------------------------------------------------------------------ orders

        public OrderResult Apply(Order o)
        {
            var r = Validate(o);
            if (!r.Ok) return r;
            o.Time = Time;
            OrderLog.Add(o);
            switch (o.Type)
            {
                case OrderType.Cool:
                {
                    var u = UnitById(o.Unit);
                    var ci = Site.ContainerIndex(o.Container);
                    u.Target = ci;
                    var pos = CoolingPosition(ci, u);
                    if (u.OnScene) { u.Path = Site.Route(u.Pos, pos); u.Status = UnitStatus.Moving; }
                    else u.Path = new List<V2> { pos };
                    Log("order", $"{u.Name} sent to cool {o.Container}.", u.Id);
                    break;
                }
                case OrderType.Stage:
                {
                    var u = UnitById(o.Unit);
                    u.Target = -1;
                    u.Staging = o.Staging ?? "north";
                    var pos = Site.StagingPos(u.Staging);
                    if (u.OnScene && u.Status != UnitStatus.Withdrawn) { u.Path = Site.Route(u.Pos, pos); u.Status = UnitStatus.Moving; }
                    Log("order", $"{u.Name} sent to the {Site.StagingName(u.Staging)}.", u.Id);
                    break;
                }
                case OrderType.InteriorAttack:
                {
                    var u = UnitById(o.Unit);
                    var c = Containers[Site.ContainerIndex(o.Container)];
                    Log("order", $"{u.Name} opens {c.Id} to fight the fire from inside.", u.Id);
                    c.InteriorAttacked = true;
                    if (c.Gas >= 25)
                    {
                        Deflagrate(c, "The doors were opened onto built-up gas");
                    }
                    else
                    {
                        c.Intensity *= 0.6;
                        c.BurnSeconds *= 0.55;
                        Log("tactic", $"The fire inside {c.Id} is being knocked down.", c.Id);
                    }
                    u.Target = c.Index;
                    u.Status = UnitStatus.Cooling;
                    u.Pos = c.Pos + new V2(0, c.Index < 6 ? 6 : -6);
                    break;
                }
                case OrderType.Shelter:
                {
                    var rc = ReceptorById(o.Receptor);
                    if (rc.Action == Protection.None) rc.Action = Protection.Shelter;
                    Log("protect", $"{rc.Name} told to shelter indoors.", rc.Id);
                    break;
                }
                case OrderType.Evacuate:
                {
                    var rc = ReceptorById(o.Receptor);
                    if (rc.Action == Protection.None || rc.Action == Protection.Shelter)
                    {
                        rc.Action = Protection.Evacuating;
                        rc.EvacDoneAt = Time + Phys.EvacMinutes * 60;
                    }
                    Log("protect", $"{rc.Name} told to evacuate.", rc.Id);
                    break;
                }
            }
            return OrderResult.Success;
        }

        /// <summary>Hard validity only: things that physically cannot happen. Judgement calls live in Doctrine.</summary>
        public OrderResult Validate(Order o)
        {
            switch (o.Type)
            {
                case OrderType.Cool:
                case OrderType.InteriorAttack:
                {
                    var u = UnitById(o.Unit);
                    if (u == null) return OrderResult.Fail($"Unknown unit '{o.Unit}'.");
                    if (u.Kind == UnitKind.Hazmat) return OrderResult.Fail("HAZMAT 1 carries monitoring gear, not a hose stream.");
                    if (u.Injured) return OrderResult.Fail($"{u.Id} crew is injured and out of service.");
                    if (u.Status == UnitStatus.Withdrawn) return OrderResult.Fail($"{u.Id} is in rehab until T+{u.RehabUntil / 60:F0}.");
                    var ci = Site.ContainerIndex(o.Container);
                    if (ci < 0) return OrderResult.Fail($"Unknown enclosure '{o.Container}'.");
                    if (o.Type == OrderType.InteriorAttack)
                    {
                        if (!u.OnScene) return OrderResult.Fail($"{u.Id} is not on scene yet.");
                        if (Containers[ci].State != CellState.Fire) return OrderResult.Fail($"{o.Container} is not burning.");
                    }
                    if (o.Type == OrderType.Cool && !(u.UsesStream && u.Target >= 0) && StreamsInUse >= StreamCapacity && u.OnScene)
                        return OrderResult.Fail($"Water supply supports {StreamCapacity} streams and all are in use.");
                    return OrderResult.Success;
                }
                case OrderType.Stage:
                {
                    var u = UnitById(o.Unit);
                    if (u == null) return OrderResult.Fail($"Unknown unit '{o.Unit}'.");
                    if (Array.IndexOf(Site.StagingIds, o.Staging ?? "north") < 0) return OrderResult.Fail($"Unknown staging point '{o.Staging}'.");
                    return OrderResult.Success;
                }
                case OrderType.Shelter:
                case OrderType.Evacuate:
                {
                    var rc = ReceptorById(o.Receptor);
                    if (rc == null) return OrderResult.Fail($"Unknown location '{o.Receptor}'.");
                    if (o.Type == OrderType.Evacuate && (rc.Action == Protection.Evacuating || rc.Action == Protection.Evacuated))
                        return OrderResult.Fail($"{rc.Name} is already evacuating.");
                    return OrderResult.Success;
                }
            }
            return OrderResult.Fail("Unknown order type.");
        }

        /// <summary>Crews choose their own hose position: upwind of the smoke, clear of venting enclosures.</summary>
        public V2 CoolingPosition(int ci, Unit u)
        {
            var c = Containers[ci];
            var dw = Wind.Downwind;
            V2[] offsets =
            {
                new V2(0, Site.Standoff), new V2(0, -Site.Standoff),
                new V2(Site.Standoff + 3, 0), new V2(-Site.Standoff - 3, 0),
                new V2(10, Site.Standoff), new V2(-10, Site.Standoff), new V2(10, -Site.Standoff), new V2(-10, -Site.Standoff),
            };
            V2 best = c.Pos + offsets[0]; double bestScore = double.MaxValue;
            foreach (var off in offsets)
            {
                var p = c.Pos + off;
                // inside the other row's footprint is not a valid hose position
                if (Math.Abs(p.Z) < 8.5 && Math.Abs(p.X) < 23) continue;
                double score = 0;
                foreach (var o in Containers)
                {
                    if (o.State == CellState.Normal || o.State == CellState.BurntOut) continue;
                    var strength = o.State == CellState.Fire ? Math.Max(0.3, o.Intensity) : 0.5;
                    score += Plume.LocalSmoke(o.Pos, strength, p, dw) * 10;
                    var d = V2.Distance(o.Pos, p);
                    if (o.State == CellState.Venting && d < Phys.DeflagrationRadius + 2) score += 60;
                }
                score += V2.Distance(p, u.Pos) * 0.02;
                // prefer being upwind of the target itself
                score += Math.Max(0, V2.Dot((p - c.Pos).Normalized, dw)) * 6;
                if (score < bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        // ------------------------------------------------------------------ stepping

        public void Step(double dt = Phys.Dt)
        {
            if (Finished) return;
            Time += dt;
            StepWind(dt);
            StepUnits(dt);
            StepContainers(dt);
            StepPlume(dt);
            CheckContainment();
            if (!Lightweight) BuildObservation();
            if (Time >= Duration) Finished = true;
        }

        void StepWind(double dt)
        {
            var cfg = Config;
            var m = Minutes;
            double baseFrom = cfg.WindFrom, baseSpeed = cfg.WindSpeed;
            if (m >= cfg.ShiftStartMin)
            {
                var t = Math.Min(1, (m - cfg.ShiftStartMin) / Math.Max(0.1, cfg.ShiftEndMin - cfg.ShiftStartMin));
                t = t * t * (3 - 2 * t);
                baseFrom = Angle.Wrap(cfg.WindFrom + Angle.Delta(cfg.WindFrom, cfg.ShiftTo) * t);
                baseSpeed = cfg.WindSpeed + (cfg.ShiftSpeed - cfg.WindSpeed) * t;
                if (!_shiftLogged) { _shiftLogged = true; Log("wind", "The wind swung round to blow from the south-west.", null, hidden: true); }
            }
            // Ornstein-Uhlenbeck gusting on direction
            Wind.Gust += (-Wind.Gust / 120.0) * dt + Rng.Gauss() * Math.Sqrt(dt) * 0.55;
            Wind.From = Angle.Wrap(baseFrom + Wind.Gust);
            Wind.Speed = Math.Max(0.8, baseSpeed + Rng.Gauss() * 0.25);

            if (!Wind.StationStuck && m >= cfg.StationStuckMin)
            {
                Wind.StationStuck = true;
                Log("sensor", "The site wind sensor jammed and kept showing the old wind.", null, hidden: true);
            }
            if (!Wind.StationStuck)
            {
                Wind.StationFrom = Angle.Wrap(Wind.From + ObsRng.Gauss() * 3);
                Wind.StationSpeed = Math.Max(0, Wind.Speed + ObsRng.Gauss() * 0.2);
            }
        }
        bool _shiftLogged;

        void StepUnits(double dt)
        {
            var dw = Wind.Downwind;
            foreach (var u in Units)
            {
                if (u.Status == UnitStatus.EnRoute)
                {
                    if (Time >= u.ArriveAt)
                    {
                        u.Pos = Site.StagingPos(u.Staging) + new V2(0, 30);
                        if (u.Target >= 0)
                        {
                            var pos = CoolingPosition(u.Target, u);
                            u.Path = Site.Route(u.Pos, pos);
                            u.Status = UnitStatus.Moving;
                        }
                        else
                        {
                            u.Path = Site.Route(u.Pos, Site.StagingPos(u.Staging));
                            u.Status = UnitStatus.Moving;
                        }
                        Log("arrival", $"{u.Name} on scene.", u.Id);
                    }
                    continue;
                }

                if (u.Status == UnitStatus.Withdrawn)
                {
                    if (!u.Injured && u.RehabUntil > 0 && Time >= u.RehabUntil)
                    {
                        u.Status = UnitStatus.Staged; u.Exposure = 0; u.RehabUntil = -1;
                        Log("arrival", $"{u.Name} crew is back from rest.", u.Id);
                    }
                    else MoveAlong(u, dt);
                    continue;
                }

                if (u.Status == UnitStatus.Moving)
                {
                    if (MoveAlong(u, dt))
                    {
                        if (u.Target >= 0 && u.StreamPower > 0)
                        {
                            u.Status = UnitStatus.SettingUp;
                            u.SetupLeft = u.Kind == UnitKind.Aerial ? 120 : 60;
                        }
                        else u.Status = UnitStatus.Staged;
                    }
                }
                else if (u.Status == UnitStatus.SettingUp)
                {
                    u.SetupLeft -= dt;
                    if (u.SetupLeft <= 0) { u.Status = UnitStatus.Cooling; Log("tactic", $"{u.Name} is spraying water on {Containers[u.Target].Id}.", u.Id); }
                }
                if (u.Status == UnitStatus.Cooling) u.WaterUsed += 1900.0 / 60.0 * dt * u.StreamPower;

                // crew exposure
                double rate = 0;
                foreach (var c in Containers)
                {
                    if (c.State == CellState.Fire) rate += Plume.LocalSmoke(c.Pos, Math.Max(0.25, c.Intensity), u.Pos, dw);
                    else if (c.State == CellState.Venting) rate += Plume.LocalSmoke(c.Pos, 0.45, u.Pos, dw);
                }
                rate += HfAt(u.Pos) / 12.0;
                if (u.Kind == UnitKind.Hazmat) rate *= 0.4; // SCBA and gas-tight suits
                u.Exposure += rate * dt / 60.0;
                if (u.Exposure >= Phys.ExposureLimit)
                {
                    u.Status = UnitStatus.Withdrawn;
                    u.Target = -1;
                    u.RehabUntil = Time + 15 * 60;
                    u.Path = Site.Route(u.Pos, Site.StagingPos(u.Staging));
                    Log("withdrawn", $"{u.Name} crew pulled back after too much smoke.", u.Id);
                }
            }
        }

        bool MoveAlong(Unit u, double dt)
        {
            var step = Phys.Speed * dt;
            while (step > 0 && u.Path.Count > 0)
            {
                var next = u.Path[0];
                var d = V2.Distance(u.Pos, next);
                if (d <= step) { u.Pos = next; step -= d; u.Path.RemoveAt(0); }
                else { u.Pos = u.Pos + (next - u.Pos).Normalized * step; step = 0; }
            }
            return u.Path.Count == 0;
        }

        readonly double[] _qIn = new double[12];

        void StepContainers(double dt)
        {
            var amb = Config.Ambient;
            var dw = Wind.Downwind;
            var spd = Wind.Speed;
            Array.Clear(_qIn, 0, 12);
            for (int i = 0; i < 12; i++)
            {
                var src = Containers[i];
                double I = src.State == CellState.Fire ? src.Intensity : src.State == CellState.Venting ? 0.18 : 0;
                if (I <= 0) continue;
                for (int j = 0; j < 12; j++)
                {
                    if (i == j) continue;
                    var wf = Phys.WindFactor(src.Pos, Containers[j].Pos, dw, spd);
                    _qIn[j] += Phys.Radiant(I, Site.GapCached(i, j), wf);
                }
            }
            for (int j = 0; j < 12; j++)
            {
                var c = Containers[j];
                double cooling = 0;
                foreach (var u in Units) if (u.Status == UnitStatus.Cooling && u.Target == j) cooling += u.StreamPower;
                var block = Math.Min(0.9, Phys.CoolBlock * cooling);

                switch (c.State)
                {
                    case CellState.Normal:
                    {
                        var q = _qIn[j] * (1 - block) + Phys.FaultRate(c.FaultHeat, c.T);
                        q -= Phys.PassiveLoss * (c.T - amb);
                        q -= Phys.CoolLoss * Math.Min(1.6, cooling) * (c.T - amb) * (c.FaultHeat > 0 ? 0.12 : 1.0); // water on the skin barely reaches an internal cell fault
                        c.T += q * dt;
                        if (c.T >= Phys.OnsetTemp)
                        {
                            c.State = CellState.Venting;
                            c.VentStart = Time;
                            LastInvolvementAt = Time;
                            Log("venting", $"{c.Id} is venting flammable gas. Its battery is in thermal runaway.", c.Id);
                        }
                        break;
                    }
                    case CellState.Venting:
                    {
                        c.T += (Phys.VentSelfHeat + _qIn[j] * (1 - block)) * dt;
                        c.Gas = Math.Min(100, c.Gas + Phys.GasRate * dt);
                        c.VentLeft -= dt;
                        if (c.VentLeft <= 0)
                        {
                            c.State = CellState.Fire;
                            c.FireStart = Time;
                            LastInvolvementAt = Time;
                            if (c.Gas > 45 && Rng.Next() < (c.Gas - 45) / 40.0)
                                Deflagrate(c, "Built-up gas ignited inside it");
                            else
                                Log("fire", $"{c.Id} is on fire.", c.Id);
                        }
                        break;
                    }
                    case CellState.Fire:
                    {
                        c.Intensity = Math.Min(1.0, c.Intensity + dt / Phys.FireRamp);
                        if (cooling > 0) c.Intensity = Math.Max(0.55, c.Intensity - 0.0004 * dt * cooling);
                        c.Fuel -= dt / c.BurnSeconds;
                        c.Gas = Math.Max(0, c.Gas - 0.15 * dt);
                        c.T += (Phys.FireTemp - c.T) * Math.Min(1, dt / 200.0);
                        if (c.Fuel <= 0)
                        {
                            c.State = CellState.BurntOut;
                            c.BurntOutAt = Time;
                            Log("burnt_out", $"{c.Id} has burnt out.", c.Id);
                        }
                        break;
                    }
                    case CellState.BurntOut:
                    {
                        c.Intensity *= Math.Exp(-dt / 420.0);
                        c.T += (amb - c.T) * Math.Min(1, dt / 1800.0) - Phys.CoolLoss * cooling * (c.T - amb) * dt * 0.3;
                        break;
                    }
                }
            }
        }

        void Deflagrate(Container c, string why)
        {
            c.Deflagrated = true;
            c.Gas = 0;
            Deflagrations++;
            if (c.State != CellState.Fire) { c.State = CellState.Fire; c.FireStart = Time; }
            c.Intensity = Math.Max(c.Intensity, 0.8);
            Log("deflagration", $"Explosion at {c.Id}. {why}.", c.Id);
            foreach (var o in Containers)
            {
                if (o == c || o.State != CellState.Normal) continue;
                var g = Site.GapCached(c.Index, o.Index);
                if (g < 7) o.T += 55 * (1 - g / 7.0);
            }
            foreach (var u in Units)
            {
                if (!u.OnScene || u.Injured) continue;
                if (V2.Distance(u.Pos, c.Pos) < Phys.DeflagrationRadius)
                {
                    u.Injured = true;
                    u.Status = UnitStatus.Withdrawn;
                    u.Target = -1;
                    Log("injury", $"{u.Name} crew injured in the blast.", u.Id);
                }
            }
        }

        public double HfAt(V2 p)
        {
            var dw = Wind.Downwind;
            double ppm = 0;
            foreach (var c in Containers)
            {
                double q = c.State == CellState.Fire ? Phys.HfPerFire * Math.Max(0.15, c.Intensity)
                         : c.State == CellState.Venting ? Phys.HfVenting
                         : c.State == CellState.BurntOut ? Phys.HfPerFire * c.Intensity * 0.5 : 0;
                if (q > 0) ppm += Plume.GroundPpm(c.Pos, q, p, dw, Wind.Speed);
            }
            return ppm;
        }

        void StepPlume(double dt)
        {
            foreach (var r in Receptors)
            {
                if (r.Action == Protection.Evacuating && Time >= r.EvacDoneAt)
                {
                    r.Action = Protection.Evacuated;
                    Log("protect", $"{r.Name} is fully evacuated.", r.Id);
                }
                r.Ppm = HfAt(r.Pos);
                r.Peak = Math.Max(r.Peak, r.Ppm);
                double factor = r.Action == Protection.Shelter ? Phys.ShelterFactor
                              : r.Action == Protection.Evacuated ? 0
                              : r.Action == Protection.Evacuating ? 0.6 : 1.0;
                r.Dose += r.Ppm * factor * dt / 60.0;
                r.PeakIndoor = Math.Max(r.PeakIndoor, r.Ppm * factor);
            }
        }

        void CheckContainment()
        {
            if (ContainedAt >= 0) return;
            bool anyVenting = Containers.Any(c => c.State == CellState.Venting);
            bool anyHot = Containers.Any(c => c.State == CellState.Normal && c.T > 90);
            bool anyFire = Containers.Any(c => c.State == CellState.Fire);
            if (!anyVenting && !anyHot && Time - LastInvolvementAt > 15 * 60 && (anyFire || Containers.Any(c => c.State == CellState.BurntOut)))
            {
                ContainedAt = Time;
                Log("contained", "The spread has stopped. Nothing new has caught fire for 15 minutes.");
            }
        }

        // ------------------------------------------------------------------ observation

        public void BuildObservation()
        {
            var prev = Obs;
            var obs = new Observation { Time = Time };
            var a1 = UnitById("A1");
            var h1 = UnitById("H1");
            bool thermal = a1 != null && a1.Available;
            bool hazmat = h1 != null && h1.Available;
            var amb = Config.Ambient;

            // sensor faults kick in on schedule
            var stuck = Config.BmsStuckContainer;
            if (stuck >= 0 && stuck < 12 && Minutes >= Config.BmsStuckMin && !Containers[stuck].BmsStuck)
            {
                var c = Containers[stuck];
                c.BmsStuck = true;
                c.BmsStuckValue = Math.Round(c.T + ObsRng.Gauss() * 0.5, 1);
                Log("sensor", $"{c.Id}'s battery sensor froze and kept reading {c.BmsStuckValue:F0}°C.", c.Id, hidden: true);
            }

            obs.Containers = new ContainerObs[12];
            for (int i = 0; i < 12; i++)
            {
                var c = Containers[i];
                var o = new ContainerObs { Id = c.Id, Index = i };
                if (c.State == CellState.Fire || c.State == CellState.BurntOut || c.T > 380) o.Bms = null;
                else if (c.BmsStuck) o.Bms = c.BmsStuckValue + ObsRng.Gauss() * 0.15;
                else o.Bms = c.T + ObsRng.Gauss() * 1.2;
                if (thermal)
                {
                    var skin = amb + 0.6 * (c.T - amb) + ObsRng.Gauss() * 4;
                    o.Thermal = Math.Min(650, Math.Max(amb - 2, skin));
                }
                o.GasRaw = Math.Max(0, c.Gas + ObsRng.Gauss() * 6 + (c.State == CellState.Normal ? Math.Abs(ObsRng.Gauss()) * 3 : 0));
                if (hazmat) o.GasVerified = Math.Max(0, c.Gas + ObsRng.Gauss() * 0.8);
                o.Visible = c.State == CellState.Venting ? "smoke" : c.State == CellState.Fire ? "fire" : c.State == CellState.BurntOut ? "burnt_out" : "normal";
                var cooler = Units.FirstOrDefault(u => u.Status == UnitStatus.Cooling && u.Target == i);
                o.CooledBy = cooler?.Id;

                // best temperature estimate the commander would form
                if (o.Bms.HasValue) { o.Estimate = o.Bms.Value; o.EstimateSource = "bms"; }
                else if (o.Thermal.HasValue) { o.Estimate = amb + (o.Thermal.Value - amb) / 0.6; o.EstimateSource = "thermal"; }
                else if (o.Visible == "fire") { o.Estimate = Phys.FireTemp; o.EstimateSource = "visual"; }
                else if (prev != null) { o.Estimate = prev.Containers[i].Estimate; o.EstimateSource = "last"; }
                else { o.Estimate = amb; o.EstimateSource = "none"; }
                obs.Containers[i] = o;
            }

            obs.StationFrom = Wind.StationFrom;
            obs.StationSpeed = Wind.StationSpeed;
            if (thermal && Containers.Any(c => c.State == CellState.Fire || c.State == CellState.Venting))
                obs.SmokeToward = Angle.Wrap(Wind.From + 180 + ObsRng.Gauss() * 5);

            obs.Receptors = Receptors.Select(r => new ReceptorObs
            {
                Id = r.Id, Name = r.Name, Action = r.Action,
                Measured = hazmat ? Math.Max(0, r.Ppm + ObsRng.Gauss() * (0.08 * r.Ppm + 0.03)) : (double?)null,
            }).ToArray();

            obs.StreamsInUse = StreamsInUse;
            obs.StreamCapacity = StreamCapacity;

            History.Push(obs);
            for (int i = 0; i < 12; i++) obs.Containers[i].RatePerMin = History.Rate(i);
            Obs = obs;
        }

        // ------------------------------------------------------------------ outcome

        public Outcome Outcome()
        {
            var o = new Outcome
            {
                Minutes = Minutes,
                Lost = Containers.Count(c => c.State == CellState.Fire || c.State == CellState.BurntOut),
                Deflagrations = Deflagrations,
                Injuries = Units.Count(u => u.Injured),
                MaxCrewExposure = Units.Max(u => u.Exposure),
                Withdrawals = Events.Count(e => e.Kind == "withdrawn"),
                ContainedMin = ContainedAt >= 0 ? ContainedAt / 60.0 : (double?)null,
                WaterKl = Units.Sum(u => u.WaterUsed) / 1000.0,
            };
            foreach (var r in Receptors)
                o.Receptors.Add(new ReceptorOutcome { Id = r.Id, Name = r.Name, PeakOutdoor = r.Peak, PeakIndoor = r.PeakIndoor, Dose = r.Dose, Action = r.Action.ToString() });
            return o;
        }
    }

    public sealed class Outcome
    {
        public double Minutes;
        public int Lost;
        public int Deflagrations;
        public int Injuries;
        public int Withdrawals;
        public double MaxCrewExposure;
        public double? ContainedMin;
        public double WaterKl;
        public List<ReceptorOutcome> Receptors = new List<ReceptorOutcome>();
        public double WorstIndoor => Receptors.Count == 0 ? 0 : Receptors.Max(r => r.PeakIndoor);
        public double TotalDose => Receptors.Sum(r => r.Dose);
    }

    public sealed class ReceptorOutcome
    {
        public string Id, Name, Action;
        public double PeakOutdoor, PeakIndoor, Dose;
    }
}
