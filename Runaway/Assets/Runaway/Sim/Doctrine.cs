using System;
using System.Collections.Generic;
using System.Linq;

namespace Runaway.Sim
{
    public sealed class Caution
    {
        public string Code;
        public string Message;
        public bool Blocking;   // physically impossible, the order cannot run
    }

    /// <summary>
    /// A small set of safety checks shared by the commander UI and the advisor's test tool. Hard
    /// validity comes from the simulation; everything else is a caution the planner must weigh and
    /// explain, the way a safety officer would flag it.
    /// </summary>
    public static class Doctrine
    {
        public static List<Caution> Check(IncidentSim sim, IList<Order> orders)
        {
            var list = new List<Caution>();
            var obs = sim.Obs;
            var probe = sim.Clone();
            probe.Lightweight = true;
            foreach (var o in orders)
            {
                var r = probe.Validate(o);
                if (!r.Ok) { list.Add(new Caution { Code = "invalid", Message = r.Error, Blocking = true }); continue; }
                probe.Apply(o);

                if (o.Type == OrderType.InteriorAttack)
                {
                    var c = obs.Containers[Site.ContainerIndex(o.Container)];
                    if (!c.GasVerified.HasValue)
                        list.Add(new Caution { Code = "gas_unverified", Message = $"No verified gas reading for {c.Id}. Opening doors on unknown gas risks a deflagration." });
                    else if (c.GasVerified.Value >= 10)
                        list.Add(new Caution { Code = "gas_high", Message = $"{c.Id} reads {c.GasVerified.Value:F0}% LEL. Opening the doors could ignite it." });
                }
                if (o.Type == OrderType.Cool)
                {
                    var ci = Site.ContainerIndex(o.Container);
                    var c = obs.Containers[ci];
                    if (c.Visible == "fire")
                        list.Add(new Caution { Code = "cool_burning", Message = $"{c.Id} is already burning. External water will not put out a battery fire; it is usually better spent on exposed neighbours." });
                    if (c.Visible == "burnt_out")
                        list.Add(new Caution { Code = "cool_burnt", Message = $"{c.Id} has burnt out. Cooling it protects nothing." });
                    var u = probe.UnitById(o.Unit);
                    var pos = probe.CoolingPosition(ci, u);
                    foreach (var v in obs.Containers.Where(x => x.Visible == "smoke"))
                    {
                        var d = V2.Distance(pos, Site.ContainerPos(v.Index));
                        if (d < Phys.DeflagrationRadius)
                            list.Add(new Caution { Code = "near_venting", Message = $"{u.Id}'s hose position would be {d:F0} m from venting {v.Id}, inside the blast radius." });
                    }
                }
                if (o.Type == OrderType.Evacuate)
                {
                    var rc = sim.ReceptorById(o.Receptor);
                    list.Add(new Caution { Code = "evac_time", Message = $"Evacuating {rc.Name} takes about {Phys.EvacMinutes:F0} minutes and moves people outdoors through any plume. Shelter-in-place protects faster." });
                }
            }
            return list;
        }
    }
}
