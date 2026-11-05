using System;
using System.Collections.Generic;

namespace Runaway.Sim
{
    /// <summary>2D ground-plane vector. X is east, Z is north, metres.</summary>
    public struct V2
    {
        public double X, Z;
        public V2(double x, double z) { X = x; Z = z; }
        public static V2 operator +(V2 a, V2 b) => new V2(a.X + b.X, a.Z + b.Z);
        public static V2 operator -(V2 a, V2 b) => new V2(a.X - b.X, a.Z - b.Z);
        public static V2 operator *(V2 a, double s) => new V2(a.X * s, a.Z * s);
        public double Length => Math.Sqrt(X * X + Z * Z);
        public V2 Normalized { get { var l = Length; return l < 1e-9 ? new V2(0, 0) : new V2(X / l, Z / l); } }
        public static double Dot(V2 a, V2 b) => a.X * b.X + a.Z * b.Z;
        public static double Cross(V2 a, V2 b) => a.X * b.Z - a.Z * b.X;
        public static double Distance(V2 a, V2 b) => (a - b).Length;
        public static V2 Lerp(V2 a, V2 b, double t) => a + (b - a) * t;

        /// <summary>Unit vector pointing toward a compass bearing (degrees clockwise from north).</summary>
        public static V2 FromBearing(double deg)
        {
            var r = deg * Math.PI / 180.0;
            return new V2(Math.Sin(r), Math.Cos(r));
        }

        public static double Bearing(V2 v)
        {
            var d = Math.Atan2(v.X, v.Z) * 180.0 / Math.PI;
            return (d + 360.0) % 360.0;
        }

        public override string ToString() => $"({X:F1}, {Z:F1})";
    }

    /// <summary>Deterministic, cloneable xorshift64* generator so runs and counterfactual replays are reproducible.</summary>
    public sealed class Rng
    {
        ulong _s;
        public Rng(ulong seed) { _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed; for (int i = 0; i < 4; i++) NextU(); }
        Rng(Rng other) { _s = other._s; }
        public Rng Clone() => new Rng(this);

        ulong NextU()
        {
            _s ^= _s >> 12; _s ^= _s << 25; _s ^= _s >> 27;
            return _s * 2685821657736338717UL;
        }

        public double Next() => (NextU() >> 11) * (1.0 / 9007199254740992.0);
        public double Range(double a, double b) => a + (b - a) * Next();
        public int Int(int n) => (int)(Next() * n) % Math.Max(1, n);

        public double Gauss()
        {
            var u1 = Math.Max(1e-12, Next());
            var u2 = Next();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }

    public static class Angle
    {
        /// <summary>Signed smallest difference b - a in degrees, in (-180, 180].</summary>
        public static double Delta(double a, double b)
        {
            var d = (b - a) % 360.0;
            if (d > 180) d -= 360;
            if (d <= -180) d += 360;
            return d;
        }

        public static double Wrap(double d) => ((d % 360.0) + 360.0) % 360.0;
    }

    public enum CellState { Normal, Venting, Fire, BurntOut }
    public enum UnitKind { Pumper, Aerial, Hazmat }
    public enum UnitStatus { EnRoute, Staged, Moving, SettingUp, Cooling, Withdrawn }
    public enum Protection { None, Shelter, Evacuating, Evacuated }
    public enum OrderType { Cool, Stage, InteriorAttack, Shelter, Evacuate }

    public sealed class Container
    {
        public int Index;
        public string Id;
        public V2 Pos;
        public double T;            // internal module temperature, deg C
        public double Gas;          // flammable off-gas inside the enclosure, % of LEL
        public double Intensity;    // fire intensity 0..1
        public double Fuel = 1;     // remaining combustible fraction
        public double FaultHeat;    // internal fault heating, deg C per second
        public double VentLeft;     // seconds of venting before ignition
        public double BurnSeconds;  // full-burn duration for this unit
        public CellState State;
        public bool InteriorAttacked;
        public bool Deflagrated;
        public double VentStart = -1, FireStart = -1, BurntOutAt = -1;

        // sensor model state
        public bool BmsStuck;
        public double BmsStuckValue;

        public Container Clone() => (Container)MemberwiseClone();
        public bool Involved => State != CellState.Normal;
    }

    public sealed class Unit
    {
        public string Id;
        public string Name;
        public UnitKind Kind;
        public UnitStatus Status = UnitStatus.EnRoute;
        public V2 Pos;
        public List<V2> Path = new List<V2>();
        public double ArriveAt;      // dispatch ETA, seconds
        public int Target = -1;      // container being cooled
        public string Staging = "north";
        public double SetupLeft;
        public double Exposure;      // crew exposure index; 40 = withdraw
        public double RehabUntil = -1;
        public bool Injured;
        public double StreamPower;   // 0 for HAZMAT
        public double WaterUsed;     // litres

        public bool OnScene => Status != UnitStatus.EnRoute;
        public bool Available => OnScene && Status != UnitStatus.Withdrawn && !Injured;
        public bool UsesStream => Status == UnitStatus.Cooling || Status == UnitStatus.SettingUp || (Status == UnitStatus.Moving && Target >= 0);

        public Unit Clone()
        {
            var u = (Unit)MemberwiseClone();
            u.Path = new List<V2>(Path);
            return u;
        }
    }

    public sealed class Receptor
    {
        public string Id;
        public string Name;
        public V2 Pos;
        public int People;
        public Protection Action = Protection.None;
        public double EvacDoneAt = -1;
        public double Ppm;      // current outdoor HF concentration
        public double Peak;     // peak outdoor concentration
        public double Dose;     // ppm-minutes actually received by occupants
        public double PeakIndoor;
        public Receptor Clone() => (Receptor)MemberwiseClone();
    }

    public sealed class WindState
    {
        public double From;        // true bearing wind blows from
        public double Speed;       // m/s
        public double Gust;        // OU noise on direction
        public double StationFrom; // what the site anemometer reports
        public double StationSpeed;
        public bool StationStuck;
        public V2 Downwind => V2.FromBearing(From + 180.0);
        public WindState Clone() => (WindState)MemberwiseClone();
    }

    public sealed class Order
    {
        public OrderType Type;
        public string Unit;
        public string Container;
        public string Staging;
        public string Receptor;
        public string Source = "commander";
        public double Time;

        public Order Clone() => (Order)MemberwiseClone();

        public override string ToString()
        {
            switch (Type)
            {
                case OrderType.Cool: return $"{Unit} cool {Container}";
                case OrderType.Stage: return $"{Unit} to {Site.StagingName(Staging)}";
                case OrderType.InteriorAttack: return $"{Unit} interior attack on {Container}";
                case OrderType.Shelter: return $"Shelter in place: {Receptor}";
                case OrderType.Evacuate: return $"Evacuate: {Receptor}";
            }
            return Type.ToString();
        }
    }

    public sealed class SimEvent
    {
        public double Time;
        public string Kind;     // arrival, venting, fire, burnt_out, deflagration, withdrawn, injury, wind, sensor, order, protect, contained
        public string Text;
        public string Subject;  // container, unit or receptor id
        public bool Hidden;     // ground truth the commander cannot see (sensor faults, true wind shift)
        public SimEvent Clone() => (SimEvent)MemberwiseClone();
    }

    public sealed class ScenarioConfig
    {
        public int Seed = 7;
        public int InitialContainer = 8;     // B3
        public double InitialTemp = 58;
        public double FaultHeat = 0.09;
        public double WindFrom = 268;
        public double WindSpeed = 4.2;
        public double ShiftStartMin = 27;
        public double ShiftEndMin = 34;
        public double ShiftTo = 222;
        public double ShiftSpeed = 6.4;
        public double StationStuckMin = 25;
        public int BmsStuckContainer = 2;    // A3
        public double BmsStuckMin = 19;
        public double[] ArrivalMin = { 7, 11, 15, 20, 29 }; // P1, P2, A1, H1, P3
        public double DurationMin = 90;
        public double Ambient = 14;

        public ScenarioConfig Clone()
        {
            var c = (ScenarioConfig)MemberwiseClone();
            c.ArrivalMin = (double[])ArrivalMin.Clone();
            return c;
        }

        public static ScenarioConfig Default() => new ScenarioConfig();

        /// <summary>Randomised variant used for training data and evaluation sweeps.</summary>
        public static ScenarioConfig Randomized(int seed)
        {
            var r = new Rng((ulong)seed * 7919UL + 17UL);
            var c = new ScenarioConfig { Seed = seed };
            c.InitialContainer = r.Int(12);
            c.InitialTemp = r.Range(55, 85);
            c.FaultHeat = r.Range(0.08, 0.13);
            c.WindFrom = Angle.Wrap(r.Range(0, 360));
            c.WindSpeed = r.Range(2.0, 8.0);
            c.ShiftStartMin = r.Range(12, 60);
            c.ShiftEndMin = c.ShiftStartMin + r.Range(3, 10);
            c.ShiftTo = Angle.Wrap(c.WindFrom + r.Range(-80, 80));
            c.ShiftSpeed = r.Range(2.0, 9.0);
            c.StationStuckMin = r.Next() < 0.5 ? r.Range(10, 50) : 1e9;
            c.BmsStuckContainer = r.Next() < 0.6 ? r.Int(12) : -1;
            c.BmsStuckMin = r.Range(8, 40);
            var a = new double[5];
            a[0] = r.Range(5, 10); a[1] = a[0] + r.Range(2, 6); a[2] = a[1] + r.Range(2, 8);
            a[3] = a[2] + r.Range(2, 8); a[4] = a[3] + r.Range(4, 12);
            c.ArrivalMin = a;
            return c;
        }
    }

    /// <summary>Fixed site geometry: a 12-enclosure grid battery with gates and three nearby communities.</summary>
    public static class Site
    {
        public const double Length = 6.1, Width = 2.4;
        public const double RowZ = 4.0, Pitch = 7.0;
        public const double Standoff = 15.0;
        public static readonly string[] StagingIds = { "north", "west", "south", "east" };

        public static V2 ContainerPos(int i)
        {
            int col = i % 6;
            bool rowA = i < 6;
            return new V2(-17.5 + col * Pitch, rowA ? RowZ : -RowZ);
        }

        public static string ContainerId(int i) => (i < 6 ? "A" : "B") + (i % 6 + 1);

        public static int ContainerIndex(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 2) return -1;
            var row = char.ToUpperInvariant(id[0]);
            if (row != 'A' && row != 'B') return -1;
            if (!int.TryParse(id.Substring(1), out var n) || n < 1 || n > 6) return -1;
            return (row == 'A' ? 0 : 6) + n - 1;
        }

        public static V2 StagingPos(string id)
        {
            switch (id)
            {
                case "west": return new V2(-62, 0);
                case "south": return new V2(0, -42);
                case "east": return new V2(62, 0);
                default: return new V2(0, 42);
            }
        }

        public static string StagingName(string id)
        {
            switch (id)
            {
                case "west": return "West gate";
                case "south": return "South gate";
                case "east": return "East gate";
                default: return "North gate";
            }
        }

        /// <summary>Edge-to-edge distance between two enclosures (axis-aligned rectangles).</summary>
        public static double Gap(int i, int j)
        {
            var a = ContainerPos(i); var b = ContainerPos(j);
            var dx = Math.Max(0, Math.Abs(a.X - b.X) - Length);
            var dz = Math.Max(0, Math.Abs(a.Z - b.Z) - Width);
            return Math.Sqrt(dx * dx + dz * dz);
        }

        static double[,] _gap;
        public static double GapCached(int i, int j)
        {
            if (_gap == null)
            {
                var g = new double[12, 12];
                for (int a = 0; a < 12; a++) for (int b = 0; b < 12; b++) g[a, b] = Gap(a, b);
                _gap = g;
            }
            return _gap[i, j];
        }

        public static List<Receptor> DefaultReceptors() => new List<Receptor>
        {
            new Receptor { Id = "homes", Name = "Glenvale homes", Pos = new V2(238, 26), People = 140 },
            new Receptor { Id = "school", Name = "Glenvale Primary", Pos = new V2(172, 206), People = 310 },
            new Receptor { Id = "farm", Name = "Hollis farm", Pos = new V2(-176, -148), People = 6 },
        };

        public static List<Unit> DefaultUnits(ScenarioConfig c) => new List<Unit>
        {
            new Unit { Id = "P1", Name = "Pumper 1", Kind = UnitKind.Pumper, StreamPower = 1.0, ArriveAt = c.ArrivalMin[0] * 60 },
            new Unit { Id = "P2", Name = "Pumper 2", Kind = UnitKind.Pumper, StreamPower = 1.0, ArriveAt = c.ArrivalMin[1] * 60 },
            new Unit { Id = "A1", Name = "Aerial 1", Kind = UnitKind.Aerial, StreamPower = 1.3, ArriveAt = c.ArrivalMin[2] * 60 },
            new Unit { Id = "H1", Name = "HAZMAT 1", Kind = UnitKind.Hazmat, StreamPower = 0, ArriveAt = c.ArrivalMin[3] * 60 },
            new Unit { Id = "P3", Name = "Pumper 3", Kind = UnitKind.Pumper, StreamPower = 1.0, ArriveAt = c.ArrivalMin[4] * 60 },
        };

        /// <summary>Route around the battery block so trucks never drive through enclosures.</summary>
        public static List<V2> Route(V2 from, V2 to)
        {
            var path = new List<V2>();
            const double bx = 23, bz = 8.5, ring = 30;
            bool Crosses(V2 a, V2 b)
            {
                for (int s = 1; s < 20; s++)
                {
                    var p = V2.Lerp(a, b, s / 20.0);
                    if (Math.Abs(p.X) < bx && Math.Abs(p.Z) < bz) return true;
                }
                return false;
            }
            if (!Crosses(from, to)) { path.Add(to); return path; }
            // pick the shorter way around via ring corners
            V2[] corners = { new V2(ring, ring), new V2(-ring, ring), new V2(-ring, -ring), new V2(ring, -ring) };
            double best = double.MaxValue; List<V2> bestPath = null;
            foreach (var c1 in corners)
            {
                if (!Crosses(from, c1) && !Crosses(c1, to))
                {
                    var d = V2.Distance(from, c1) + V2.Distance(c1, to);
                    if (d < best) { best = d; bestPath = new List<V2> { c1, to }; }
                }
                foreach (var c2 in corners)
                {
                    if (c1.X == c2.X && c1.Z == c2.Z) continue;
                    if (!Crosses(from, c1) && !Crosses(c1, c2) && !Crosses(c2, to))
                    {
                        var d = V2.Distance(from, c1) + V2.Distance(c1, c2) + V2.Distance(c2, to);
                        if (d < best) { best = d; bestPath = new List<V2> { c1, c2, to }; }
                    }
                }
            }
            return bestPath ?? new List<V2> { to };
        }
    }
}
