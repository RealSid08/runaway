using System.Collections.Generic;
using Runaway.Sim;
using UnityEngine;

namespace Runaway.App
{
    /// <summary>Builds the static world and keeps the live views (enclosures, trucks, plume) in step with the simulation.</summary>
    public sealed class SiteView : MonoBehaviour
    {
        public readonly List<ContainerView> Containers = new List<ContainerView>();
        public readonly Dictionary<string, UnitView> Units = new Dictionary<string, UnitView>();
        public readonly Dictionary<string, Vector3> ReceptorAnchors = new Dictionary<string, Vector3>();
        public PlumeOverlay Plume;
        public bool ShowRisk = true;
        public bool ShowThermal;
        public string SelectedContainer;
        public string SelectedUnit;

        Transform _static;
        readonly System.Random _r = new System.Random(11);
        float R(float a, float b) => a + (float)_r.NextDouble() * (b - a);

        public static Vector3 W(V2 p, float y = 0) => new Vector3((float)p.X, y, (float)p.Z);

        public void Build(GameController gc)
        {
            _static = new GameObject("Static").transform;
            _static.SetParent(transform, false);
            BuildGround();
            BuildSite();
            BuildRoads();
            BuildCommunities(gc.Sim);
            BuildTrees();
            BuildPowerLine();

            for (int i = 0; i < 12; i++)
            {
                var go = new GameObject("Enclosure " + Runaway.Sim.Site.ContainerId(i));
                go.transform.SetParent(transform, false);
                var v = go.AddComponent<ContainerView>();
                v.Build(i);
                Containers.Add(v);
            }
            foreach (var u in gc.Sim.Units)
            {
                var go = new GameObject("Unit " + u.Id);
                go.transform.SetParent(transform, false);
                var v = go.AddComponent<UnitView>();
                v.Build(u, this);
                Units[u.Id] = v;
            }
            var pg = new GameObject("Plume overlay");
            pg.transform.SetParent(transform, false);
            Plume = pg.AddComponent<PlumeOverlay>();
            Plume.Build();
        }

        public void Rebind(IncidentSim sim)
        {
            foreach (var u in sim.Units) if (Units.TryGetValue(u.Id, out var v)) v.Snap(u);
            foreach (var c in Containers) c.ResetVisuals();
        }

        void LateUpdate()
        {
            var gc = GameController.I;
            if (gc == null || gc.Sim == null) return;
            var sim = gc.Sim;
            for (int i = 0; i < 12; i++)
            {
                float risk = gc.Risk != null && sim.Obs.Containers[i].Visible == "normal" ? gc.Risk[i * 2 + 1] : 0f;
                Containers[i].Sync(sim, sim.Containers[i], sim.Obs.Containers[i], risk, ShowRisk, ShowThermal, SelectedContainer == Containers[i].Id);
            }
            foreach (var u in sim.Units)
                if (Units.TryGetValue(u.Id, out var v)) v.Sync(sim, u, SelectedUnit == u.Id);
        }

        // ------------------------------------------------------------------ static world

        void BuildGround()
        {
            var grassTex = Fx.GroundTex(Fx.Hex("#2a2b1f"), Fx.Hex("#3b3826"), Fx.Hex("#4a4433"), 5f);
            var grass = Fx.Lit(Color.white, 0.08f);
            grass = new Material(grass) { mainTexture = grassTex };
            grass.SetTexture("_BaseMap", grassTex);
            grass.SetTextureScale("_BaseMap", new Vector2(70, 70));
            var g = GameObject.CreatePrimitive(PrimitiveType.Plane);
            g.name = "Ground";
            g.transform.SetParent(_static, false);
            g.transform.localScale = new Vector3(300, 1, 300);
            g.GetComponent<MeshRenderer>().sharedMaterial = grass;
            g.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void BuildSite()
        {
            var gravelTex = Fx.GroundTex(Fx.Hex("#55575a"), Fx.Hex("#6b6c6c"), Fx.Hex("#7d7c78"), 14f);
            var gravel = new Material(Fx.Lit(Color.white, 0.12f));
            gravel.SetTexture("_BaseMap", gravelTex);
            gravel.SetTextureScale("_BaseMap", new Vector2(12, 8));
            Fx.Box(_static, "Gravel pad", new Vector3(0, 0.04f, 0), new Vector3(98, 0.08f, 66), gravel, false);

            var concrete = Fx.Lit(Fx.Hex("#8d8f90"), 0.15f);
            var steel = Fx.Lit(Fx.Hex("#5f666d"), 0.45f, 0.6f);
            var dark = Fx.Lit(Fx.Hex("#24282d"), 0.3f);
            var asphalt = Fx.Lit(Fx.Hex("#1c1e21"), 0.35f);

            // internal ring road
            Fx.Box(_static, "Ring N", new Vector3(0, 0.09f, 24), new Vector3(70, 0.05f, 6), asphalt, false);
            Fx.Box(_static, "Ring S", new Vector3(0, 0.09f, -24), new Vector3(70, 0.05f, 6), asphalt, false);
            Fx.Box(_static, "Ring W", new Vector3(-32, 0.09f, 0), new Vector3(6, 0.05f, 54), asphalt, false);
            Fx.Box(_static, "Ring E", new Vector3(32, 0.09f, 0), new Vector3(6, 0.05f, 54), asphalt, false);

            // cable trench between rows
            Fx.Box(_static, "Cable trench", new Vector3(0, 0.12f, 0), new Vector3(44, 0.12f, 0.8f), concrete, false);

            // transformer yard (east)
            var tx = new GameObject("Transformer").transform; tx.SetParent(_static, false); tx.localPosition = new Vector3(40, 0, 0);
            Fx.Box(tx, "Plinth", new Vector3(0, 0.2f, 0), new Vector3(8, 0.4f, 7), concrete);
            Fx.Box(tx, "Tank", new Vector3(0, 2.0f, 0), new Vector3(4.2f, 3.2f, 3.2f), Fx.Lit(Fx.Hex("#6f7a72"), 0.35f, 0.2f));
            for (int i = 0; i < 6; i++) Fx.Box(tx, "Fin", new Vector3(-1.6f + i * 0.64f, 1.9f, 2.0f), new Vector3(0.12f, 2.6f, 0.8f), steel);
            for (int i = 0; i < 6; i++) Fx.Box(tx, "Fin", new Vector3(-1.6f + i * 0.64f, 1.9f, -2.0f), new Vector3(0.12f, 2.6f, 0.8f), steel);
            for (int i = 0; i < 3; i++) Fx.Cyl(tx, "Bushing", new Vector3(-1.2f + i * 1.2f, 4.2f, 0), new Vector3(0.28f, 0.6f, 0.28f), Fx.Lit(Fx.Hex("#a14b2e"), 0.5f));

            // control room (west)
            var cr = new GameObject("Control room").transform; cr.SetParent(_static, false); cr.localPosition = new Vector3(-38, 0, -16);
            Fx.Box(cr, "Building", new Vector3(0, 1.8f, 0), new Vector3(10, 3.6f, 6), Fx.Lit(Fx.Hex("#b8b4aa"), 0.2f));
            Fx.Box(cr, "Roof", new Vector3(0, 3.7f, 0), new Vector3(10.4f, 0.2f, 6.4f), dark);
            Fx.Box(cr, "Door", new Vector3(3.2f, 1.1f, 3.01f), new Vector3(1.1f, 2.2f, 0.04f), dark);
            Fx.Box(cr, "Lamp", new Vector3(3.2f, 2.6f, 3.05f), new Vector3(0.5f, 0.12f, 0.12f), Fx.Emissive(Color.white, Fx.Hex("#ffd39a"), 4f), false);
            Fx.Box(cr, "Window", new Vector3(-1.5f, 1.9f, 3.01f), new Vector3(3.5f, 0.9f, 0.04f), Fx.Emissive(Fx.Hex("#222"), Fx.Hex("#9ec3e0"), 1.2f), false);

            // container plinths
            for (int i = 0; i < 12; i++)
            {
                var p = W(Runaway.Sim.Site.ContainerPos(i));
                Fx.Box(_static, "Plinth", p + new Vector3(0, 0.17f, 0), new Vector3(6.7f, 0.34f, 3.0f), concrete);
            }

            // fence with gates
            BuildFence(-48, 48, -32, 32);

            // floodlights
            var poles = new[] { new Vector3(-46, 0, 30), new Vector3(46, 0, 30), new Vector3(-46, 0, -30), new Vector3(46, 0, -30), new Vector3(0, 0, 30.5f), new Vector3(0, 0, -30.5f) };
            for (int i = 0; i < poles.Length; i++)
            {
                var pole = new GameObject("Floodlight").transform; pole.SetParent(_static, false); pole.localPosition = poles[i];
                Fx.Cyl(pole, "Pole", new Vector3(0, 5f, 0), new Vector3(0.22f, 5f, 0.22f), steel);
                var head = Fx.Box(pole, "Head", new Vector3(0, 10.1f, 0), new Vector3(0.9f, 0.25f, 0.5f), Fx.Emissive(Fx.Hex("#333"), Fx.Hex("#ffe2b0"), 6f), false);
                var lgo = new GameObject("Light"); lgo.transform.SetParent(pole, false);
                lgo.transform.localPosition = new Vector3(0, 10, 0);
                lgo.transform.LookAt(new Vector3(0, 0, 0) - poles[i] * 0.2f + pole.position * 0.0f);
                var l = lgo.AddComponent<Light>();
                l.type = LightType.Spot; l.color = Fx.Hex("#ffd7a8"); l.intensity = 38f; l.range = 75f; l.spotAngle = 78f; l.innerSpotAngle = 40f;
                l.shadows = i < 2 ? LightShadows.Soft : LightShadows.None;
                l.shadowStrength = 0.8f;
                lgo.transform.rotation = Quaternion.LookRotation((new Vector3(0, 0, 0) - (poles[i] + Vector3.up * 10)).normalized + Vector3.down * 0.2f);
            }
        }

        void BuildFence(float x0, float x1, float z0, float z1)
        {
            var post = Fx.Lit(Fx.Hex("#7c838a"), 0.4f, 0.5f);
            var wire = Fx.Lit(Fx.Hex("#8a9198"), 0.5f, 0.6f);
            void Run(Vector3 a, Vector3 b, float gapAt, bool alongX)
            {
                var len = (b - a).magnitude;
                var dir = (b - a).normalized;
                for (float s = 0; s <= len; s += 3.2f)
                {
                    var p = a + dir * s;
                    var gc = alongX ? p.x : p.z;
                    if (Mathf.Abs(gc - gapAt) < 4.5f) continue;
                    Fx.Cyl(_static, "Post", p + Vector3.up * 1.1f, new Vector3(0.08f, 1.1f, 0.08f), post);
                }
                // rails either side of the gate
                foreach (var seg in new[] { (a, a + dir * (Mathf.Abs((alongX ? a.x : a.z) - gapAt) - 4.5f)), (a + dir * (Mathf.Abs((alongX ? a.x : a.z) - gapAt) + 4.5f), b) })
                {
                    var mid = (seg.Item1 + seg.Item2) * 0.5f; var l = (seg.Item2 - seg.Item1).magnitude;
                    for (int k = 0; k < 3; k++)
                    {
                        var size = alongX ? new Vector3(l, 0.035f, 0.035f) : new Vector3(0.035f, 0.035f, l);
                        Fx.Box(_static, "Wire", mid + Vector3.up * (0.5f + k * 0.75f), size, wire, false);
                    }
                }
            }
            Run(new Vector3(x0, 0, z1), new Vector3(x1, 0, z1), 0, true);
            Run(new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), 0, true);
            Run(new Vector3(x0, 0, z0), new Vector3(x0, 0, z1), 0, false);
            Run(new Vector3(x1, 0, z0), new Vector3(x1, 0, z1), 0, false);
        }

        void BuildRoads()
        {
            var asphalt = Fx.Lit(Fx.Hex("#17191c"), 0.45f);
            var line = Fx.Emissive(Fx.Hex("#bdb7a4"), Fx.Hex("#bdb7a4"), 0.15f);
            Fx.Box(_static, "Access road", new Vector3(0, 0.07f, 92), new Vector3(7, 0.06f, 120), asphalt, false);
            Fx.Box(_static, "Glenvale Rd", new Vector3(80, 0.07f, 152), new Vector3(1100, 0.06f, 8), asphalt, false);
            for (float x = -460; x < 620; x += 9) Fx.Box(_static, "Line", new Vector3(x, 0.105f, 152), new Vector3(4, 0.01f, 0.18f), line, false);
            Fx.Box(_static, "Homes st", new Vector3(238, 0.07f, 86), new Vector3(7, 0.06f, 130), asphalt, false);
            Fx.Box(_static, "School drive", new Vector3(172, 0.07f, 176), new Vector3(7, 0.06f, 44), asphalt, false);
            Fx.Box(_static, "Farm track", new Vector3(-176, 0.06f, -40), new Vector3(4, 0.04f, 220), Fx.Lit(Fx.Hex("#3a3326"), 0.1f), false);
            Fx.Box(_static, "Farm link", new Vector3(-88, 0.06f, 70), new Vector3(180, 0.04f, 4), Fx.Lit(Fx.Hex("#3a3326"), 0.1f), false);
        }

        void BuildCommunities(IncidentSim sim)
        {
            var wall = new[] { Fx.Lit(Fx.Hex("#b9ab96"), 0.2f), Fx.Lit(Fx.Hex("#a7a39c"), 0.2f), Fx.Lit(Fx.Hex("#c7bfae"), 0.2f), Fx.Lit(Fx.Hex("#8f8678"), 0.2f) };
            var roof = new[] { Fx.Lit(Fx.Hex("#3d3f44"), 0.35f, 0.3f), Fx.Lit(Fx.Hex("#5a3a30"), 0.25f), Fx.Lit(Fx.Hex("#2f3a3f"), 0.35f, 0.3f) };
            var warm = Fx.Emissive(Fx.Hex("#1a1610"), Fx.Hex("#ffc779"), 2.2f);
            var cool = Fx.Emissive(Fx.Hex("#14181c"), Fx.Hex("#b9d4ff"), 1.4f);

            GameObject House(Vector3 p, float yaw, float w, float l)
            {
                var h = new GameObject("House").transform; h.SetParent(_static, false); h.localPosition = p; h.localRotation = Quaternion.Euler(0, yaw, 0);
                var m = wall[_r.Next(wall.Length)];
                Fx.Box(h, "Walls", new Vector3(0, 1.6f, 0), new Vector3(w, 3.2f, l), m);
                Fx.MeshObj(h, "Roof", Fx.Gable, new Vector3(0, 3.2f, 0), new Vector3(w + 0.8f, 2.2f, l + 0.8f), roof[_r.Next(roof.Length)]);
                int lit = _r.Next(1, 4);
                for (int k = 0; k < lit; k++)
                {
                    float x = R(-w * 0.3f, w * 0.3f);
                    Fx.Box(h, "Window", new Vector3(x, 1.7f, l * 0.5f + 0.02f), new Vector3(1.2f, 1.0f, 0.04f), _r.NextDouble() < 0.75 ? warm : cool, false);
                }
                Fx.Box(h, "Window", new Vector3(w * 0.5f + 0.02f, 1.7f, R(-l * 0.25f, l * 0.25f)), new Vector3(0.04f, 1.0f, 1.2f), warm, false);
                return h.gameObject;
            }

            var homes = sim.ReceptorById("homes");
            var hp = W(homes.Pos);
            ReceptorAnchors["homes"] = hp + Vector3.up * 6;
            for (int i = 0; i < 14; i++)
            {
                int col = i % 2, row = i / 2;
                var p = hp + new Vector3(col == 0 ? -14 : 14, 0, -60 + row * 18 + R(-2, 2));
                House(p, col == 0 ? 90 : -90, R(9, 12), R(11, 14));
            }

            var school = sim.ReceptorById("school");
            var sp = W(school.Pos);
            ReceptorAnchors["school"] = sp + Vector3.up * 9;
            var s = new GameObject("Glenvale Primary").transform; s.SetParent(_static, false); s.localPosition = sp;
            var brick = Fx.Lit(Fx.Hex("#8c5a46"), 0.15f);
            Fx.Box(s, "Main", new Vector3(0, 2.6f, 0), new Vector3(46, 5.2f, 14), brick);
            Fx.Box(s, "Roof", new Vector3(0, 5.35f, 0), new Vector3(47, 0.3f, 15), roof[0]);
            Fx.Box(s, "Wing", new Vector3(-17, 2.2f, 17), new Vector3(12, 4.4f, 22), brick);
            Fx.Box(s, "WingRoof", new Vector3(-17, 4.55f, 17), new Vector3(12.6f, 0.3f, 22.6f), roof[0]);
            for (int k = 0; k < 9; k++) Fx.Box(s, "Window", new Vector3(-18 + k * 4.5f, 2.8f, -7.02f), new Vector3(2.6f, 1.4f, 0.04f), k % 3 == 0 ? cool : Fx.Lit(Fx.Hex("#1b2026"), 0.8f), false);
            Fx.Box(s, "Oval", new Vector3(16, 0.06f, 34), new Vector3(46, 0.04f, 34), Fx.Lit(Fx.Hex("#2f3b25"), 0.05f), false);
            Fx.Cyl(s, "Flagpole", new Vector3(10, 4, -11), new Vector3(0.12f, 4, 0.12f), Fx.Lit(Fx.Hex("#9aa0a6"), 0.6f, 0.8f));

            var farm = sim.ReceptorById("farm");
            var fp = W(farm.Pos);
            ReceptorAnchors["farm"] = fp + Vector3.up * 6;
            House(fp, 20, 11, 13);
            var shed = Fx.Lit(Fx.Hex("#7d8186"), 0.4f, 0.5f);
            var f = new GameObject("Sheds").transform; f.SetParent(_static, false); f.localPosition = fp;
            Fx.Box(f, "Shed", new Vector3(26, 3, 10), new Vector3(18, 6, 12), shed);
            Fx.MeshObj(f, "ShedRoof", Fx.Gable, new Vector3(26, 6, 10), new Vector3(19, 2.2f, 13), shed);
            Fx.Box(f, "Shed2", new Vector3(30, 2.5f, -14), new Vector3(10, 5, 9), shed);
        }

        void BuildTrees()
        {
            var trunk = Fx.Lit(Fx.Hex("#3a2f26"), 0.1f);
            var leaves = new[] { Fx.Lit(Fx.Hex("#24301f"), 0.05f), Fx.Lit(Fx.Hex("#2b3523"), 0.05f), Fx.Lit(Fx.Hex("#1f2a22"), 0.05f) };
            void Tree(Vector3 p, float h, bool cypress)
            {
                var t = new GameObject("Tree").transform; t.SetParent(_static, false); t.localPosition = p;
                Fx.Cyl(t, "Trunk", new Vector3(0, h * 0.18f, 0), new Vector3(0.35f, h * 0.18f, 0.35f), trunk);
                var lm = leaves[_r.Next(leaves.Length)];
                if (cypress)
                {
                    Fx.MeshObj(t, "Crown", Fx.Cone, new Vector3(0, h * 0.2f, 0), new Vector3(h * 0.34f, h * 0.9f, h * 0.34f), lm);
                }
                else
                {
                    for (int k = 0; k < 3; k++)
                    {
                        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        Object.Destroy(go.GetComponent<Collider>());
                        go.transform.SetParent(t, false);
                        go.transform.localPosition = new Vector3(R(-1.5f, 1.5f), h * R(0.55f, 0.85f), R(-1.5f, 1.5f));
                        go.transform.localScale = Vector3.one * h * R(0.32f, 0.45f);
                        go.GetComponent<MeshRenderer>().sharedMaterial = lm;
                    }
                }
            }
            // cypress windbreaks outside the fence
            for (float x = -60; x <= 60; x += 4.5f) { Tree(new Vector3(x + R(-0.8f, 0.8f), 0, 44 + R(-1, 1)), R(10, 14), true); }
            for (float z = -40; z <= 40; z += 4.5f) { Tree(new Vector3(-64 + R(-1, 1), 0, z), R(10, 14), true); }
            for (float x = -60; x <= 60; x += 5f) { if (Mathf.Abs(x) < 6) continue; Tree(new Vector3(x, 0, -46 + R(-1, 1)), R(9, 13), true); }
            // scattered gums around the district
            int placed = 0;
            while (placed < 170)
            {
                var p = new Vector3(R(-420, 520), 0, R(-380, 460));
                if (Mathf.Abs(p.x) < 80 && Mathf.Abs(p.z) < 60) continue;
                if (Mathf.Abs(p.z - 152) < 10) continue;
                if (Mathf.Abs(p.x) < 8 && p.z > 30 && p.z < 160) continue;
                if (Vector3.Distance(p, new Vector3(238, 0, 26)) < 70) continue;
                if (Vector3.Distance(p, new Vector3(172, 0, 206)) < 60) continue;
                Tree(p, R(9, 18), false);
                placed++;
            }
            // street trees in Glenvale
            for (int i = 0; i < 10; i++) Tree(new Vector3(238 + (i % 2 == 0 ? -4.5f : 4.5f), 0, -40 + i * 14), R(7, 10), false);
        }

        void BuildPowerLine()
        {
            var wood = Fx.Lit(Fx.Hex("#4a3c30"), 0.1f);
            var cable = Fx.Lit(Fx.Hex("#1a1a1a"), 0.3f);
            Vector3 prev = Vector3.zero; bool has = false;
            for (float x = 52; x <= 640; x += 58)
            {
                var p = new Vector3(x, 0, 4 + (x > 300 ? (x - 300) * 0.15f : 0));
                Fx.Cyl(_static, "Pole", p + Vector3.up * 6, new Vector3(0.35f, 6, 0.35f), wood);
                Fx.Box(_static, "Crossarm", p + Vector3.up * 11.4f, new Vector3(0.25f, 0.25f, 3.2f), wood);
                if (has)
                {
                    for (int k = -1; k <= 1; k++)
                    {
                        var a = prev + new Vector3(0, 11.5f, k * 1.4f); var b = p + new Vector3(0, 11.5f, k * 1.4f);
                        var mid = (a + b) * 0.5f + Vector3.down * 0.9f;
                        var go = Fx.Box(_static, "Cable", mid, new Vector3(0.04f, 0.04f, (b - a).magnitude), cable, false);
                        go.transform.rotation = Quaternion.LookRotation(b - a);
                    }
                }
                prev = p; has = true;
            }
        }
    }
}
