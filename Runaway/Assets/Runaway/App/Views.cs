using Runaway.Sim;
using UnityEngine;

namespace Runaway.App
{
    public static class Palette
    {
        public static readonly Color Ink = Fx.Hex("#E8EAED");
        public static readonly Color Muted = Fx.Hex("#9AA4B1");
        public static readonly Color Advisor = Fx.Hex("#8AB4FF");
        public static readonly Color Good = Fx.Hex("#6CCB8E");
        public static readonly Color Caution = Fx.Hex("#F2B84B");
        public static readonly Color Danger = Fx.Hex("#FF5B4E");
        public static readonly Color Hot = Fx.Hex("#F2914B");

        /// <summary>Temperature colour for labels and the thermal view.</summary>
        public static Color Temp(double t)
        {
            if (t < 45) return Fx.Hex("#C9D1DA");
            if (t < 80) return Fx.Hex("#F2D36B");
            if (t < 120) return Fx.Hex("#F2A04B");
            return Fx.Hex("#FF5B4E");
        }

        public static Color Risk(float p)
        {
            if (p < 0.15f) return Fx.Hex("#F2D36B");
            if (p < 0.5f) return Fx.Hex("#F2914B");
            return Fx.Hex("#FF5B4E");
        }
    }

    static class Particles
    {
        public static ParticleSystem Make(Transform parent, string name, Material mat, int max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.maxParticles = max;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            var em = ps.emission; em.rateOverTime = 0;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.Distance;
            return ps;
        }

        public static Gradient Grad(params (Color c, float a, float t)[] keys)
        {
            var g = new Gradient();
            var ck = new GradientColorKey[keys.Length]; var ak = new GradientAlphaKey[keys.Length];
            for (int i = 0; i < keys.Length; i++) { ck[i] = new GradientColorKey(keys[i].c, keys[i].t); ak[i] = new GradientAlphaKey(keys[i].a, keys[i].t); }
            g.SetKeys(ck, ak);
            return g;
        }
    }

    /// <summary>One battery enclosure: what a person on site would see, plus optional data overlays.</summary>
    public sealed class ContainerView : MonoBehaviour
    {
        public string Id;
        public int Index;
        Material _body, _status;
        ParticleSystem _vent, _smoke, _flame, _embers;
        Light _light;
        Transform _ring, _select;
        Material _ringMat, _selectMat;
        float _seed;
        float _char;
        float _gasPuff;

        public Vector3 Top => transform.position + Vector3.up * 3.3f;

        public void Build(int i)
        {
            Index = i; Id = Runaway.Sim.Site.ContainerId(i);
            transform.position = SiteView.W(Runaway.Sim.Site.ContainerPos(i));
            _seed = i * 13.7f;
            _body = Fx.EmissiveInstance(Fx.Hex("#cfd3d7"));
            var trim = Fx.Lit(Fx.Hex("#9da3a9"), 0.35f, 0.2f);
            var dark = Fx.Lit(Fx.Hex("#2a2e33"), 0.3f);
            var body = Fx.Box(transform, "Body", new Vector3(0, 0.34f + 1.32f, 0), new Vector3((float)Runaway.Sim.Site.Length, 2.64f, (float)Runaway.Sim.Site.Width), _body);
            body.AddComponent<BoxCollider>();
            Fx.Box(transform, "Cap", new Vector3(0, 0.34f + 2.7f, 0), new Vector3(6.25f, 0.12f, 2.55f), trim);
            // door seams on both long faces
            for (int s = -1; s <= 1; s += 2)
                for (int k = 0; k < 4; k++)
                {
                    float x = -2.25f + k * 1.5f;
                    Fx.Box(transform, "Seam", new Vector3(x + 0.74f, 1.62f, s * 1.205f), new Vector3(0.04f, 2.3f, 0.02f), dark, false);
                    Fx.Box(transform, "Handle", new Vector3(x + 0.55f, 1.55f, s * 1.215f), new Vector3(0.05f, 0.35f, 0.03f), trim, false);
                }
            // thermal management louvres on the ends
            for (int s = -1; s <= 1; s += 2)
                for (int k = 0; k < 5; k++)
                    Fx.Box(transform, "Louvre", new Vector3(s * 3.06f, 0.9f + k * 0.36f, 0), new Vector3(0.03f, 0.12f, 1.9f), dark, false);
            // roof vents
            Fx.Box(transform, "Vent", new Vector3(-1.6f, 3.12f, 0), new Vector3(0.9f, 0.18f, 0.9f), dark);
            Fx.Box(transform, "Vent", new Vector3(1.6f, 3.12f, 0), new Vector3(0.9f, 0.18f, 0.9f), dark);
            _status = Fx.EmissiveInstance(Fx.Hex("#222"));
            Fx.Box(transform, "Status", new Vector3(3.08f, 2.6f, 0.8f), new Vector3(0.04f, 0.14f, 0.24f), _status, false);

            var smokeMat = Fx.Particle(Fx.Puff, false, 3f);
            var addMat = Fx.Particle(Fx.Flame, true, 1f);
            var dotMat = Fx.Particle(Fx.Dot, true, 0.5f);

            _vent = Particles.Make(transform, "Off-gas", smokeMat, 260);
            {
                var m = _vent.main; m.startLifetime = new ParticleSystem.MinMaxCurve(4f, 7f); m.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 2.6f);
                m.startSize = new ParticleSystem.MinMaxCurve(1.2f, 2.4f); m.startColor = new Color(0.85f, 0.87f, 0.9f, 0.5f);
                m.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
                var sh = _vent.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(5.5f, 0.1f, 2f); sh.position = new Vector3(0, 3.1f, 0);
                var sz = _vent.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0.6f, 1, 3.2f));
                var col = _vent.colorOverLifetime; col.enabled = true; col.color = Particles.Grad((Color.white, 0f, 0f), (Color.white, 0.55f, 0.12f), (Color.white, 0f, 1f));
            }
            _smoke = Particles.Make(transform, "Smoke", smokeMat, 520);
            {
                var m = _smoke.main; m.startLifetime = new ParticleSystem.MinMaxCurve(11f, 16f); m.startSpeed = new ParticleSystem.MinMaxCurve(3f, 5f);
                m.startSize = new ParticleSystem.MinMaxCurve(3f, 5f); m.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
                m.startColor = new Color(0.16f, 0.15f, 0.15f, 0.75f);
                var sh = _smoke.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(5f, 0.5f, 2f); sh.position = new Vector3(0, 4f, 0);
                var sz = _smoke.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.EaseInOut(0, 0.7f, 1, 4.6f));
                var col = _smoke.colorOverLifetime; col.enabled = true;
                col.color = Particles.Grad((Fx.Hex("#ff9a4a"), 0f, 0f), (Fx.Hex("#3a302a"), 0.8f, 0.08f), (Fx.Hex("#2a2826"), 0.6f, 0.45f), (Fx.Hex("#3b3c40"), 0f, 1f));
                var rot = _smoke.rotationOverLifetime; rot.enabled = true; rot.z = new ParticleSystem.MinMaxCurve(-0.3f, 0.3f);
                var lim = _smoke.limitVelocityOverLifetime; lim.enabled = true; lim.dampen = 0.06f; lim.limit = 6f;
            }
            _flame = Particles.Make(transform, "Flame", addMat, 300);
            {
                var m = _flame.main; m.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.2f); m.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 5f);
                m.startSize = new ParticleSystem.MinMaxCurve(1.6f, 3.4f); m.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
                var sh = _flame.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(5.6f, 0.5f, 2.2f); sh.position = new Vector3(0, 2.6f, 0);
                var sz = _flame.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.EaseInOut(0, 1f, 1, 0.2f));
                var col = _flame.colorOverLifetime; col.enabled = true;
                col.color = Particles.Grad((Fx.Hex("#fff2c4"), 0.9f, 0f), (Fx.Hex("#ffb347"), 0.85f, 0.25f), (Fx.Hex("#ff5a1f"), 0.5f, 0.6f), (Fx.Hex("#7a1f0a"), 0f, 1f));
            }
            _embers = Particles.Make(transform, "Embers", dotMat, 200);
            {
                var m = _embers.main; m.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 3.5f); m.startSpeed = new ParticleSystem.MinMaxCurve(3f, 8f);
                m.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.22f); m.startColor = Fx.Hex("#ffb35c"); m.gravityModifier = -0.15f;
                var sh = _embers.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(5f, 1f, 2f); sh.position = new Vector3(0, 3f, 0);
                var noise = _embers.noise; noise.enabled = true; noise.strength = 1.6f; noise.frequency = 0.6f;
                var col = _embers.colorOverLifetime; col.enabled = true; col.color = Particles.Grad((Fx.Hex("#ffe1a0"), 1f, 0f), (Fx.Hex("#ff6a2a"), 0f, 1f));
            }

            var lg = new GameObject("Fire light"); lg.transform.SetParent(transform, false); lg.transform.localPosition = new Vector3(0, 5f, 0);
            _light = lg.AddComponent<Light>(); _light.type = LightType.Point; _light.color = Fx.Hex("#ff8a3a"); _light.range = 55; _light.intensity = 0; _light.shadows = LightShadows.None;

            _ringMat = Fx.Particle(Fx.Ring, true, 0.3f);
            _ring = Fx.MeshObj(transform, "Risk ring", Fx.Quad, new Vector3(0, 0.36f, 0), new Vector3(9.5f, 1, 9.5f), _ringMat, null, false).transform;
            _selectMat = Fx.Particle(Fx.Ring, true, 0.3f);
            _select = Fx.MeshObj(transform, "Select ring", Fx.Quad, new Vector3(0, 0.37f, 0), new Vector3(11f, 1, 11f), _selectMat, null, false).transform;
        }

        public void ResetVisuals()
        {
            _char = 0;
            foreach (var p in new[] { _vent, _smoke, _flame, _embers }) p.Clear();
        }

        public void Sync(IncidentSim sim, Container c, ContainerObs o, float risk, bool showRisk, bool thermal, bool selected)
        {
            var dw = sim.Wind.Downwind;
            var wind = new Vector3((float)dw.X, 0, (float)dw.Z) * (float)sim.Wind.Speed;
            var t = Time.time;

            // enclosure skin: chars as it burns; thermal view tints by best estimate
            float target = c.State == CellState.Fire ? Mathf.Clamp01((float)(1 - c.Fuel) * 1.6f + 0.25f) : c.State == CellState.BurntOut ? 1f : 0f;
            _char = Mathf.MoveTowards(_char, target, Time.deltaTime * 0.08f);
            var baseCol = Color.Lerp(Fx.Hex("#cfd3d7"), Fx.Hex("#1d1c1b"), _char);
            Color emission = Color.black;
            if (c.State == CellState.Fire) emission = Fx.Hex("#ff5a1f") * (0.6f + 0.4f * Mathf.PerlinNoise(t * 2.1f, _seed)) * (float)c.Intensity * 2.2f;
            else if (c.State == CellState.BurntOut) emission = Fx.Hex("#ff4a12") * (float)c.Intensity * 1.2f * (0.7f + 0.3f * Mathf.PerlinNoise(t * 0.7f, _seed));
            if (thermal && c.State == CellState.Normal)
            {
                var tc = Palette.Temp(o.Estimate);
                baseCol = Color.Lerp(baseCol, tc * 0.4f, 0.7f);
                emission += tc * Mathf.Clamp01((float)(o.Estimate - 30) / 120f) * 1.3f;
            }
            _body.SetColor("_BaseColor", baseCol);
            _body.SetColor("_EmissionColor", emission);

            // the site's own status light, driven by the BMS
            Color status = Fx.Hex("#2fd06d");
            if (o.Bms == null) status = Fx.Hex("#555");
            else if (o.Bms > 100 || o.Visible != "normal") status = Fx.Hex("#ff3b30");
            else if (o.Bms > 55) status = Fx.Hex("#ffb020");
            float blink = status == Fx.Hex("#ff3b30") ? (Mathf.Repeat(t * 2f, 1) < 0.5f ? 1 : 0.15f) : 1;
            _status.SetColor("_EmissionColor", status * 3.5f * blink);

            // particles
            SetRate(_vent, c.State == CellState.Venting ? 46 : 0);
            if (c.State == CellState.Venting)
            {
                var vel = _vent.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
                vel.x = wind.x * 0.5f; vel.z = wind.z * 0.5f; vel.y = 0.6f;
            }
            float I = (float)c.Intensity;
            bool burning = c.State == CellState.Fire;
            SetRate(_flame, burning ? 70 * I + 10 : 0);
            SetRate(_embers, burning ? 22 * I : c.State == CellState.BurntOut ? 3 * I : 0);
            SetRate(_smoke, burning ? 16 * I + 4 : c.State == CellState.BurntOut ? 6 * I : 0);
            if (burning || c.State == CellState.BurntOut)
            {
                var vel = _smoke.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
                vel.x = new ParticleSystem.MinMaxCurve(1, AnimationCurve.EaseInOut(0, 0.1f, 1, 1f), AnimationCurve.EaseInOut(0, 0.1f, 1, 1f));
                vel.x = new ParticleSystem.MinMaxCurve(wind.x * 0.2f, wind.x * 1.05f);
                vel.z = new ParticleSystem.MinMaxCurve(wind.z * 0.2f, wind.z * 1.05f);
                vel.y = new ParticleSystem.MinMaxCurve(1.2f, 2.6f);
                var fv = _flame.velocityOverLifetime; fv.enabled = true; fv.space = ParticleSystemSimulationSpace.World;
                fv.x = wind.x * 0.35f; fv.z = wind.z * 0.35f; fv.y = 0.5f;
                var ev = _embers.velocityOverLifetime; ev.enabled = true; ev.space = ParticleSystemSimulationSpace.World;
                ev.x = wind.x * 0.7f; ev.z = wind.z * 0.7f; ev.y = 0f;
            }
            _light.intensity = burning ? (9 + 6 * Mathf.PerlinNoise(t * 6f, _seed)) * I : c.State == CellState.BurntOut ? 3 * I : 0;

            // risk ring from the forecaster
            bool showRing = showRisk && risk >= 0.05f && c.State == CellState.Normal;
            _ring.gameObject.SetActive(showRing);
            if (showRing)
            {
                var rc = Palette.Risk(risk);
                rc.a = Mathf.Lerp(0.35f, 1f, risk) * (0.75f + 0.25f * Mathf.Sin(t * 3f + _seed));
                _ringMat.SetColor("_Tint", rc);
            }
            _select.gameObject.SetActive(selected);
            if (selected) _selectMat.SetColor("_Tint", new Color(1, 1, 1, 0.55f + 0.2f * Mathf.Sin(t * 4f)));
        }

        static void SetRate(ParticleSystem ps, float rate)
        {
            var em = ps.emission;
            em.rateOverTime = rate;
            if (rate > 0 && !ps.isPlaying) ps.Play();
            else if (rate <= 0 && ps.isPlaying && ps.particleCount == 0) ps.Stop();
        }
    }

    /// <summary>A fire appliance with light bar, aerial ladder and hose stream.</summary>
    public sealed class UnitView : MonoBehaviour
    {
        public string Id;
        Material _barA, _barB;
        Transform _ladder, _nozzle, _body;
        ParticleSystem _stream, _mist;
        Light _beacon;
        Vector3 _vel;
        float _heading;
        SiteView _site;
        bool _visible;

        public Vector3 LabelAnchor => transform.position + Vector3.up * 4.6f;
        public bool Visible => _visible;

        public void Build(Unit u, SiteView site)
        {
            Id = u.Id; _site = site;
            var red = Fx.Lit(Fx.Hex("#a51d18"), 0.55f, 0.15f);
            var white = Fx.Lit(Fx.Hex("#e7e7e2"), 0.5f);
            var yellow = Fx.Lit(Fx.Hex("#e8c22a"), 0.45f);
            var chrome = Fx.Lit(Fx.Hex("#b8bec4"), 0.8f, 0.9f);
            var tyre = Fx.Lit(Fx.Hex("#151515"), 0.2f);
            var glass = Fx.Lit(Fx.Hex("#1e2a33"), 0.9f, 0.3f);
            bool hazmat = u.Kind == UnitKind.Hazmat;
            var paint = hazmat ? white : red;
            _body = new GameObject("Body").transform; _body.SetParent(transform, false);
            float L = u.Kind == UnitKind.Aerial ? 10.5f : 8f;
            var col = Fx.Box(_body, "Chassis", new Vector3(0, 0.75f, 0), new Vector3(2.5f, 0.5f, L), Fx.Lit(Fx.Hex("#202326"), 0.3f));
            Fx.Box(_body, "Cab", new Vector3(0, 1.75f, L * 0.5f - 1.2f), new Vector3(2.5f, 1.9f, 2.4f), paint);
            Fx.Box(_body, "Windscreen", new Vector3(0, 2.15f, L * 0.5f - 0.01f), new Vector3(2.2f, 0.9f, 0.04f), glass, false);
            Fx.Box(_body, "Pack", new Vector3(0, 1.9f, -1.0f), new Vector3(2.5f, 2.2f, L - 2.6f), paint);
            Fx.Box(_body, "Stripe", new Vector3(0, 1.3f, -1.0f + 0f), new Vector3(2.52f, 0.22f, L - 2.6f), hazmat ? yellow : Fx.Lit(Fx.Hex("#efe9d8"), 0.5f), false);
            Fx.Box(_body, "Stripe cab", new Vector3(0, 1.3f, L * 0.5f - 1.2f), new Vector3(2.52f, 0.22f, 2.42f), hazmat ? yellow : Fx.Lit(Fx.Hex("#efe9d8"), 0.5f), false);
            for (int s = -1; s <= 1; s += 2)
                foreach (var z in new[] { L * 0.5f - 1.4f, -L * 0.5f + 1.5f, -L * 0.5f + 2.9f })
                    Fx.Cyl(_body, "Wheel", new Vector3(s * 1.2f, 0.5f, z), new Vector3(0.95f, 0.18f, 0.95f), tyre).transform.localRotation = Quaternion.Euler(0, 0, 90);
            _barA = Fx.EmissiveInstance(Fx.Hex("#300")); _barB = Fx.EmissiveInstance(Fx.Hex("#003"));
            Fx.Box(_body, "Bar L", new Vector3(-0.55f, 2.78f, L * 0.5f - 1.0f), new Vector3(0.9f, 0.16f, 0.32f), _barA, false);
            Fx.Box(_body, "Bar R", new Vector3(0.55f, 2.78f, L * 0.5f - 1.0f), new Vector3(0.9f, 0.16f, 0.32f), _barB, false);
            Fx.Box(_body, "Head L", new Vector3(-0.85f, 1.1f, L * 0.5f + 0.01f), new Vector3(0.35f, 0.2f, 0.04f), Fx.Emissive(Color.white, Fx.Hex("#fff4e0"), 5f), false);
            Fx.Box(_body, "Head R", new Vector3(0.85f, 1.1f, L * 0.5f + 0.01f), new Vector3(0.35f, 0.2f, 0.04f), Fx.Emissive(Color.white, Fx.Hex("#fff4e0"), 5f), false);
            col.AddComponent<BoxCollider>().size = new Vector3(1, 6, 1.1f);

            if (u.Kind == UnitKind.Aerial)
            {
                var pivot = new GameObject("Turntable").transform; pivot.SetParent(_body, false); pivot.localPosition = new Vector3(0, 3.1f, -3.4f);
                _ladder = new GameObject("Ladder").transform; _ladder.SetParent(pivot, false);
                Fx.Box(_ladder, "Rail L", new Vector3(-0.42f, 0, 4.2f), new Vector3(0.12f, 0.22f, 8.6f), chrome);
                Fx.Box(_ladder, "Rail R", new Vector3(0.42f, 0, 4.2f), new Vector3(0.12f, 0.22f, 8.6f), chrome);
                for (int k = 0; k < 12; k++) Fx.Box(_ladder, "Rung", new Vector3(0, 0, 0.3f + k * 0.7f), new Vector3(0.8f, 0.05f, 0.05f), chrome, false);
                Fx.Box(_ladder, "Bucket", new Vector3(0, 0.1f, 8.7f), new Vector3(1.3f, 0.8f, 0.9f), Fx.Lit(Fx.Hex("#d6d3cb"), 0.4f));
                _nozzle = new GameObject("Nozzle").transform; _nozzle.SetParent(_ladder, false); _nozzle.localPosition = new Vector3(0, 0.6f, 9.1f);
            }
            else
            {
                _nozzle = new GameObject("Monitor").transform; _nozzle.SetParent(_body, false); _nozzle.localPosition = new Vector3(0, 3.2f, 0.6f);
                if (!hazmat) Fx.Cyl(_body, "Monitor", new Vector3(0, 3.05f, 0.6f), new Vector3(0.25f, 0.18f, 0.25f), chrome);
            }
            if (hazmat)
            {
                Fx.Box(_body, "Mast", new Vector3(0, 3.6f, -2.5f), new Vector3(0.12f, 1.6f, 0.12f), chrome);
                Fx.Box(_body, "Sensor head", new Vector3(0, 4.45f, -2.5f), new Vector3(0.5f, 0.25f, 0.5f), Fx.Emissive(Fx.Hex("#222"), Fx.Hex("#8ef0ff"), 2f), false);
            }

            var waterMat = Fx.Particle(Fx.Dot, false, 0.8f);
            _stream = Particles.Make(transform, "Stream", waterMat, 900);
            {
                var m = _stream.main; m.startLifetime = 1.8f; m.startSize = new ParticleSystem.MinMaxCurve(0.18f, 0.42f);
                m.startColor = new Color(0.8f, 0.9f, 1f, 0.55f); m.gravityModifier = 1f; m.startSpeed = 0;
                var sh = _stream.shape; sh.enabled = false;
                var col2 = _stream.colorOverLifetime; col2.enabled = true; col2.color = Particles.Grad((Color.white, 0.75f, 0f), (Color.white, 0.5f, 0.7f), (Color.white, 0f, 1f));
                var sz = _stream.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0.6f, 1, 2.2f));
            }
            var mistMat = Fx.Particle(Fx.Puff, false, 2f);
            _mist = Particles.Make(transform, "Mist", mistMat, 200);
            {
                var m = _mist.main; m.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 2.8f); m.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.8f);
                m.startSize = new ParticleSystem.MinMaxCurve(1.5f, 3.2f); m.startColor = new Color(0.85f, 0.9f, 0.95f, 0.28f);
                var sh = _mist.shape; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 1.6f;
                var col3 = _mist.colorOverLifetime; col3.enabled = true; col3.color = Particles.Grad((Color.white, 0f, 0f), (Color.white, 0.4f, 0.2f), (Color.white, 0f, 1f));
            }

            var bg = new GameObject("Beacon"); bg.transform.SetParent(_body, false); bg.transform.localPosition = new Vector3(0, 3.4f, L * 0.5f - 1f);
            _beacon = bg.AddComponent<Light>(); _beacon.type = LightType.Point; _beacon.range = 18; _beacon.intensity = 0; _beacon.shadows = LightShadows.None;
            Snap(u);
        }

        public void Snap(Unit u)
        {
            transform.position = SiteView.W(u.Pos);
            _vel = Vector3.zero;
        }

        public void Sync(IncidentSim sim, Unit u, bool selected)
        {
            _visible = u.OnScene;
            _body.gameObject.SetActive(_visible);
            if (!_visible) { SetRate(_stream, 0); SetRate(_mist, 0); _beacon.intensity = 0; return; }
            var target = SiteView.W(u.Pos);
            if ((target - transform.position).sqrMagnitude > 900) transform.position = target;
            transform.position = Vector3.SmoothDamp(transform.position, target, ref _vel, 0.35f, 30f, Time.deltaTime);

            Vector3 face;
            bool cooling = u.Status == UnitStatus.Cooling && u.Target >= 0;
            Vector3 tgt = Vector3.zero;
            if (u.Target >= 0) tgt = SiteView.W(sim.Containers[u.Target].Pos);
            if (_vel.sqrMagnitude > 0.5f) face = _vel;
            else if ((u.Status == UnitStatus.Cooling || u.Status == UnitStatus.SettingUp) && u.Target >= 0) face = tgt - transform.position;
            else face = transform.forward;
            face.y = 0;
            if (face.sqrMagnitude > 0.01f)
            {
                var want = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
                _heading = Mathf.MoveTowardsAngle(_heading, want, Time.deltaTime * 90f);
                transform.rotation = Quaternion.Euler(0, _heading, 0);
            }

            // light bar
            float t = Time.time * 2.6f + Id.GetHashCode() % 7;
            bool phase = Mathf.Repeat(t, 1) < 0.5f;
            _barA.SetColor("_EmissionColor", (phase ? Fx.Hex("#ff2a2a") * 6f : Color.black));
            _barB.SetColor("_EmissionColor", (!phase ? Fx.Hex("#2a6bff") * 6f : Color.black));
            _beacon.color = phase ? Fx.Hex("#ff3030") : Fx.Hex("#3060ff");
            _beacon.intensity = u.Status == UnitStatus.Withdrawn ? 0.6f : 2.2f;

            if (_ladder != null)
            {
                var want = cooling || u.Status == UnitStatus.SettingUp ? -38f : 0f;
                var cur = _ladder.localEulerAngles.x; if (cur > 180) cur -= 360;
                _ladder.localRotation = Quaternion.Euler(Mathf.MoveTowards(cur, want, Time.deltaTime * 12f), 0, 0);
                var turn = _ladder.parent;
                if (cooling || u.Status == UnitStatus.SettingUp)
                {
                    var local = transform.InverseTransformPoint(tgt); local.y = 0;
                    var yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                    turn.localRotation = Quaternion.RotateTowards(turn.localRotation, Quaternion.Euler(0, yaw, 0), Time.deltaTime * 40f);
                }
            }

            if (cooling)
            {
                var from = _nozzle.position;
                var to = tgt + new Vector3(0, 3.2f, 0) + (from - tgt).normalized * 1.5f;
                float flight = 1.25f;
                var g = Physics.gravity.y;
                var v = (to - from) / flight; v.y = (to.y - from.y) / flight - 0.5f * g * flight;
                var m = _stream.main; m.startSpeed = 0;
                var sh = _stream.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.08f;
                _stream.transform.position = from;
                var vel = _stream.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
                vel.x = new ParticleSystem.MinMaxCurve(v.x * 0.96f, v.x * 1.04f); vel.y = new ParticleSystem.MinMaxCurve(v.y * 0.97f, v.y * 1.03f); vel.z = new ParticleSystem.MinMaxCurve(v.z * 0.96f, v.z * 1.04f);
                m.startLifetime = flight * 1.05f;
                SetRate(_stream, 320);
                _mist.transform.position = to + Vector3.down * 1.2f;
                SetRate(_mist, 18);
            }
            else { SetRate(_stream, 0); SetRate(_mist, 0); }
        }

        static void SetRate(ParticleSystem ps, float rate)
        {
            var em = ps.emission;
            em.rateOverTime = rate;
            if (rate > 0 && !ps.isPlaying) ps.Play();
            else if (rate <= 0 && ps.isPlaying && ps.particleCount == 0) ps.Stop();
        }
    }

    /// <summary>Modelled HF ground concentration from current observations, drawn as a translucent heat layer.</summary>
    public sealed class PlumeOverlay : MonoBehaviour
    {
        public const int N = 128;
        public const float Size = 760f;
        public static readonly Vector3 Center = new Vector3(70, 0, 60);
        Texture2D _tex;
        Material _mat;
        readonly float[] _buf = new float[N * N];
        float _next;
        public bool Visible = true;
        MeshRenderer _r;
        float _fade;

        public void Build()
        {
            _tex = new Texture2D(N, N, TextureFormat.RFloat, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            _mat = Fx.Plume();
            _mat.SetTexture("_Conc", _tex);
            var go = Fx.MeshObj(transform, "Plume quad", Fx.Quad, Center + Vector3.up * 0.25f, new Vector3(Size, 1, Size), _mat, null, false);
            _r = go.GetComponent<MeshRenderer>();
        }

        void Update()
        {
            var gc = GameController.I;
            if (gc?.Sim?.Obs == null) return;
            _fade = Mathf.MoveTowards(_fade, Visible ? 1 : 0, Time.deltaTime * 2.5f);
            _mat.SetFloat("_Opacity", _fade * 0.9f);
            _r.enabled = _fade > 0.01f;
            if (Time.time < _next || !_r.enabled) return;
            _next = Time.time + 0.4f;
            var obs = gc.Sim.Obs;
            var dw = V2.FromBearing(obs.BestWindFrom + 180);
            var spd = obs.StationSpeed;
            var sources = new System.Collections.Generic.List<(V2 p, double q)>();
            foreach (var c in obs.Containers)
            {
                if (c.Visible == "fire") sources.Add((Runaway.Sim.Site.ContainerPos(c.Index), Phys.HfPerFire * 0.85));
                else if (c.Visible == "smoke") sources.Add((Runaway.Sim.Site.ContainerPos(c.Index), Phys.HfVenting));
                else if (c.Visible == "burnt_out") sources.Add((Runaway.Sim.Site.ContainerPos(c.Index), Phys.HfPerFire * 0.1));
            }
            float half = Size * 0.5f, cell = Size / N;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    var p = new V2(Center.x - half + (x + 0.5f) * cell, Center.z - half + (y + 0.5f) * cell);
                    double ppm = 0;
                    foreach (var s in sources) ppm += Runaway.Sim.Plume.GroundPpm(s.p, s.q, p, dw, spd);
                    _buf[y * N + x] = (float)ppm;
                }
            _tex.SetPixelData(_buf, 0);
            _tex.Apply(false);
        }

        /// <summary>Modelled concentration at a world point, for labels.</summary>
        public float Sample(Vector3 world)
        {
            float half = Size * 0.5f, cell = Size / N;
            int x = Mathf.Clamp((int)((world.x - (Center.x - half)) / cell), 0, N - 1);
            int y = Mathf.Clamp((int)((world.z - (Center.z - half)) / cell), 0, N - 1);
            return _buf[y * N + x];
        }
    }
}
