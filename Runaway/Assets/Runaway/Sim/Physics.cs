using System;

namespace Runaway.Sim
{
    /// <summary>Tunable constants for the incident model. Values are chosen for plausible timing, not engineering design.</summary>
    public static class Phys
    {
        public const double Dt = 10.0;                // seconds per step
        public const double OnsetTemp = 150.0;        // thermal runaway onset, deg C
        public const double VentSelfHeat = 0.32;      // deg C/s while venting
        public const double PassiveLoss = 0.0015;     // 1/s Newtonian loss to ambient
        public const double RadiantK = 0.62;          // neighbour heating coefficient
        public const double RadiantExp = 1.5;         // falloff with edge gap
        public const double WindBoost = 1.5;         // downwind heat transfer boost
        public const double CoolBlock = 0.72;         // fraction of incoming heat a stream absorbs (per unit power)
        public const double CoolLoss = 0.0062;        // 1/s extra loss while cooled
        public const double GasRate = 0.12;           // %LEL per second while venting
        public const double FireTemp = 720.0;
        public const double FireRamp = 300.0;         // seconds to full intensity
        public const double HfPerFire = 36.0;         // g/s at full intensity
        public const double HfVenting = 3.0;          // g/s while venting
        public const double PlumeHeight = 18.0;       // effective release height, m
        public const double ExposureLimit = 40.0;     // crew exposure index that forces withdrawal
        public const double DeflagrationRadius = 17.0;
        public const double Speed = 6.0;              // truck speed on site, m/s
        public const double ShelterFactor = 0.3;
        public const double EvacMinutes = 20.0;
        public const double Aegl1 = 1.0;              // HF ppm: discomfort threshold (AEGL-1, 1 h)
        public const double Aegl2 = 24.0;             // HF ppm: serious effects threshold (AEGL-2, 1 h)

        /// <summary>Internal cell fault heating accelerates with temperature (Arrhenius-like).</summary>
        public static double FaultRate(double fault, double t) => fault <= 0 ? 0 : fault * Math.Exp((t - 60.0) / 30.0);

        /// <summary>Heat transfer multiplier from i to j given the downwind direction and wind speed.</summary>
        public static double WindFactor(V2 from, V2 to, V2 downwind, double speed)
        {
            var dir = (to - from).Normalized;
            var c = V2.Dot(dir, downwind);
            return 1.0 + WindBoost * Math.Max(0, c) * (speed / 4.0);
        }

        public static double Radiant(double intensity, double gap, double windFactor)
            => RadiantK * intensity * windFactor / Math.Pow(gap + 1.0, RadiantExp);
    }

    /// <summary>Ground-level Gaussian plume (Briggs rural, neutral stability) with meander widening.</summary>
    public static class Plume
    {
        public static double GroundPpm(V2 src, double grams, V2 at, V2 downwind, double speed)
        {
            if (grams <= 0) return 0;
            var rel = at - src;
            var x = V2.Dot(rel, downwind);
            if (x < 5) return 0;
            var y = V2.Cross(downwind, rel);
            var u = Math.Max(1.0, speed);
            var sy = 0.08 * x / Math.Sqrt(1 + 0.0001 * x) + 0.12 * x; // + meander
            var sz = 0.06 * x / Math.Sqrt(1 + 0.0015 * x);
            var h = Phys.PlumeHeight;
            var c = grams / (2 * Math.PI * u * sy * sz) * Math.Exp(-y * y / (2 * sy * sy)) * 2 * Math.Exp(-h * h / (2 * sz * sz)); // g/m3
            var mg = c * 1000.0;
            return mg * 24.45 / 20.01; // HF ppm
        }

        /// <summary>Near-field smoke exposure for crews close to involved enclosures, index units per minute.</summary>
        public static double LocalSmoke(V2 src, double strength, V2 at, V2 downwind)
        {
            if (strength <= 0) return 0;
            var rel = at - src;
            var along = V2.Dot(rel, downwind);
            var cross = Math.Abs(V2.Cross(downwind, rel));
            var dist = rel.Length;
            double rate = 0;
            if (along > -2 && along < 45 && cross < along * 0.55 + 5)
                rate += strength * 2.2 * (1 - Math.Max(0, along) / 45.0);
            if (dist < 12) rate += strength * 0.8 * (1 - dist / 12.0); // radiant heat close in
            return rate;
        }
    }
}
