using System;
using System.Collections.Generic;

namespace Runaway.Sim
{
    /// <summary>Everything the incident commander (and the advisor) can actually know. Never ground truth.</summary>
    public sealed class Observation
    {
        public double Time;
        public ContainerObs[] Containers;
        public double StationFrom;
        public double StationSpeed;
        public double? SmokeToward;   // bearing the smoke column drifts toward, seen from Aerial 1
        public ReceptorObs[] Receptors;
        public int StreamsInUse;
        public int StreamCapacity;

        /// <summary>Wind the commander would act on: smoke drift when visible, else the station.</summary>
        public double BestWindFrom => SmokeToward.HasValue ? Angle.Wrap(SmokeToward.Value + 180) : StationFrom;
    }

    public sealed class ContainerObs
    {
        public string Id;
        public int Index;
        public double? Bms;           // battery management system module temperature
        public double? Thermal;       // enclosure skin temperature from Aerial 1's thermal camera
        public double GasRaw;         // fixed gas detector, %LEL, noisy
        public double? GasVerified;   // HAZMAT 1 meter, %LEL
        public string Visible;        // normal | smoke | fire | burnt_out
        public string CooledBy;
        public double Estimate;       // best module temperature estimate
        public string EstimateSource; // bms | thermal | visual | last | none
        public double? RatePerMin;    // BMS trend over the last two minutes

        public double Gas => GasVerified ?? GasRaw;

        /// <summary>True when the BMS reading and the thermal camera disagree by more than measurement noise allows.</summary>
        public bool SensorConflict
        {
            get
            {
                if (!Bms.HasValue || !Thermal.HasValue) return false;
                var implied = 14 + (Thermal.Value - 14) / 0.6;
                return Math.Abs(implied - Bms.Value) > 35;
            }
        }
    }

    public sealed class ReceptorObs
    {
        public string Id;
        public string Name;
        public Protection Action;
        public double? Measured;      // HAZMAT 1 air monitoring, ppm HF
    }

    /// <summary>Rolling BMS history used for trends. Two minutes at 10 s steps.</summary>
    public sealed class ObservationHistory
    {
        const int N = 13;
        readonly Queue<double?[]> _bms = new Queue<double?[]>();
        readonly Queue<double> _t = new Queue<double>();

        public void Push(Observation o)
        {
            var v = new double?[12];
            for (int i = 0; i < 12; i++) v[i] = o.Containers[i].Bms;
            _bms.Enqueue(v); _t.Enqueue(o.Time);
            while (_bms.Count > N) { _bms.Dequeue(); _t.Dequeue(); }
        }

        public double? Rate(int i)
        {
            if (_bms.Count < 3) return null;
            var arr = _bms.ToArray(); var ts = _t.ToArray();
            var first = arr[0][i]; var last = arr[arr.Length - 1][i];
            if (!first.HasValue || !last.HasValue) return null;
            var dtMin = (ts[ts.Length - 1] - ts[0]) / 60.0;
            if (dtMin <= 0) return null;
            return (last.Value - first.Value) / dtMin;
        }

        public ObservationHistory Clone()
        {
            var h = new ObservationHistory();
            foreach (var v in _bms) h._bms.Enqueue((double?[])v.Clone());
            foreach (var t in _t) h._t.Enqueue(t);
            return h;
        }
    }
}
