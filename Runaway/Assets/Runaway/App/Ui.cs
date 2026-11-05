using System;
using System.Collections.Generic;
using System.Linq;
using Runaway.Advisor;
using Runaway.Sim;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Angle = Runaway.Sim.Angle;

namespace Runaway.App
{
    /// <summary>Incident console built with UI Toolkit. Reads the controller; sends commands back through it.</summary>
    public sealed class Ui : MonoBehaviour
    {
        GameController G => GameController.I;
        public UIDocument Doc;
        public CameraRig Rig;
        public SiteView Site;
        VisualElement _root, _labels, _app;

        // top bar
        Label _clock, _state, _windMain, _windSub, _involved;
        VisualElement _stateDot, _arrowStation, _arrowSmoke;
        readonly Dictionary<string, (Label ppm, Label act)> _community = new Dictionary<string, (Label, Label)>();
        readonly List<(Label el, float speed)> _speedSegs = new List<(Label, float)>();
        Label _pauseSeg;

        // crews
        readonly Dictionary<string, (VisualElement row, Label status, VisualElement fill)> _unitRows = new Dictionary<string, (VisualElement, Label, VisualElement)>();
        VisualElement _unitActions;

        // advisor
        VisualElement _advisor;
        VisualElement _timerFill;
        readonly List<VisualElement> _actionRows = new List<VisualElement>();
        readonly HashSet<int> _keep = new HashSet<int>();
        bool _editing;
        DecisionRecord _shownCard;
        Phase _shownPhase;
        VisualElement _steps;
        Label _thinkingTime;
        public Label AcceptButton, EditButton, DismissButton, ApplyEditButton;
        public readonly List<VisualElement> ActionChecks = new List<VisualElement>();

        // feed / inspector / overlays
        VisualElement _feed, _inspector, _legend;
        readonly List<(double t, string text, string cls)> _events = new List<(double, string, string)>();
        public Label PlumeToggle, RiskToggle, ThermalToggle;

        // labels
        readonly List<(VisualElement tag, Label id, Label val, Label risk, Label flag)> _ctags = new List<(VisualElement, Label, Label, Label, Label)>();
        readonly Dictionary<string, VisualElement> _utags = new Dictionary<string, VisualElement>();
        readonly Dictionary<string, (VisualElement tag, Label sub)> _rtags = new Dictionary<string, (VisualElement, Label)>();

        // modal
        VisualElement _modal;
        AdvisorMode _chosen = AdvisorMode.Full;
        public Label StartButton, RunAgainButton;
        public readonly Dictionary<AdvisorMode, VisualElement> ModeCards = new Dictionary<AdvisorMode, VisualElement>();

        // toast
        Label _toast;
        float _toastUntil;

        // virtual cursor for recorded demos
        public VisualElement Cursor;

        float _nextSlow;

        public void Build()
        {
            _root = Doc.rootVisualElement;
            _root.Clear();
            _root.styleSheets.Add(Resources.Load<StyleSheet>("UI/Runaway"));
            _app = E("app", _root);
            _app.pickingMode = PickingMode.Ignore;
            _labels = E("labels", _app); _labels.pickingMode = PickingMode.Ignore;
            BuildTopBar();
            BuildCrews();
            _advisor = E("panel advisor", _app);
            BuildFeed();
            BuildInspector();
            BuildOverlays();
            BuildLabels();
            var toastRow = E("toast", _app); toastRow.pickingMode = PickingMode.Ignore;
            _toast = L("", "toast-inner medium", toastRow);
            Cursor = new VisualElement(); Cursor.AddToClassList("cursor"); Cursor.pickingMode = PickingMode.Ignore;
            Cursor.generateVisualContent += DrawCursor;
            Cursor.style.display = DisplayStyle.None;
            _root.Add(Cursor);

            G.PhaseChanged += OnPhase;
            G.DecisionsChanged += () => { _shownCard = null; };
            G.EventRaised += OnEvent;
            Rig.PointerOverUi = PointerOverUi;
            OnPhase();
        }

        // ------------------------------------------------------------------ element helpers

        static VisualElement E(string cls, VisualElement parent = null)
        {
            var e = new VisualElement();
            foreach (var c in cls.Split(' ')) if (c.Length > 0) e.AddToClassList(c);
            parent?.Add(e);
            return e;
        }

        static Label L(string text, string cls, VisualElement parent = null)
        {
            var l = new Label(text);
            foreach (var c in cls.Split(' ')) if (c.Length > 0) l.AddToClassList(c);
            parent?.Add(l);
            return l;
        }

        static Label B(string text, string cls, Action onClick, VisualElement parent = null)
        {
            var b = L(text, "btn " + cls, parent);
            b.AddManipulator(new Clickable(onClick));
            return b;
        }

        static string Clock(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return $"T+{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        }

        void Toast(string text, bool caution = false)
        {
            _toast.text = text;
            _toast.EnableInClassList("caution", caution);
            _toast.AddToClassList("show");
            _toastUntil = Time.unscaledTime + (caution ? 4.5f : 2.6f);
        }

        // ------------------------------------------------------------------ top bar

        void BuildTopBar()
        {
            var bar = E("topbar", _app); bar.pickingMode = PickingMode.Ignore;
            var cb = E("panel clock-block", bar);
            L("Glenvale battery fire", "incident-name medium", cb);
            _clock = L("T+00:00", "clock semibold", cb);
            var st = E("state-chip", cb);
            _stateDot = E("state-dot", st);
            _state = L("", "state-text", st);

            var wind = E("panel wind", bar);
            var compass = E("compass", wind);
            L("N", "compass-n", compass);
            _arrowStation = MakeArrow(compass, false);
            _arrowSmoke = MakeArrow(compass, true);
            var wt = E("", wind);
            _windMain = L("", "wind-main medium", wt);
            _windSub = L("", "wind-sub", wt);

            var com = E("panel communities", bar);
            foreach (var r in G.Sim.Receptors)
            {
                var row = E("community", com);
                L(r.Name, "community-name", row);
                var ppm = L("", "community-ppm medium", row);
                var act = L("", "community-action", row);
                _community[r.Id] = (ppm, act);
            }

            E("spacer", bar).pickingMode = PickingMode.Ignore;
            var inv = E("panel involved", bar);
            _involved = L("0", "involved-num semibold", inv);
            L("of 12 enclosures involved", "involved-label", inv);

            var sp = E("panel speed", bar);
            _pauseSeg = L("Pause", "seg medium", sp);
            _pauseSeg.AddManipulator(new Clickable(() => G.TogglePause()));
            foreach (var s in new[] { 4f, 12f, 24f, 48f })
            {
                var seg = L($"{s:0}×", "seg medium", sp);
                var speed = s;
                seg.AddManipulator(new Clickable(() => { G.Speed = speed; G.Paused = false; }));
                _speedSegs.Add((seg, s));
            }
        }

        static VisualElement MakeArrow(VisualElement parent, bool smoke)
        {
            var a = E("arrow" + (smoke ? " smoke" : ""), parent);
            a.pickingMode = PickingMode.Ignore;
            E("arrow-head", a);
            E("arrow-shaft", a);
            return a;
        }

        // ------------------------------------------------------------------ crews

        void BuildCrews()
        {
            var p = E("panel crews", _app);
            L("Crews", "section-title medium", p);
            foreach (var u in G.Sim.Units)
            {
                var row = E("unit", p);
                var id = u.Id;
                row.AddManipulator(new Clickable(() => SelectUnit(Site.SelectedUnit == id ? null : id)));
                L(u.Id, "badge semibold" + (u.Kind == UnitKind.Hazmat ? " hazmat" : ""), row);
                var txt = E("grow", row);
                L(u.Name, "unit-name medium", txt);
                var status = L("", "unit-status", txt);
                var bar = E("exposure", txt);
                var fill = E("exposure-fill", bar);
                _unitRows[u.Id] = (row, status, fill);
            }
            _unitActions = E("unit-actions", p);
            _unitActions.style.display = DisplayStyle.None;
        }

        public void SelectUnit(string id)
        {
            Site.SelectedUnit = id;
            _unitActions.Clear();
            if (id == null) { _unitActions.style.display = DisplayStyle.None; return; }
            var u = G.Sim.UnitById(id);
            _unitActions.style.display = DisplayStyle.Flex;
            if (u.Kind != UnitKind.Hazmat) L($"Click an enclosure to put {u.Id} on it.", "hint", _unitActions);
            var gates = E("gates", _unitActions);
            foreach (var g in Runaway.Sim.Site.StagingIds)
            {
                var gid = g;
                B(Runaway.Sim.Site.StagingName(g).Replace(" gate", ""), "small", () => Issue(new Order { Type = OrderType.Stage, Unit = id, Staging = gid }), gates);
            }
        }

        void Issue(Order o)
        {
            var cautions = G.Preview(o);
            var r = G.Command(o);
            if (!r.Ok) { Toast(r.Error, true); return; }
            var c = cautions.FirstOrDefault(x => !x.Blocking);
            if (c != null) Toast($"{o}. {c.Message}", true);
            else Toast($"{o}.");
        }

        // ------------------------------------------------------------------ advisor card

        void RenderAdvisor()
        {
            _advisor.Clear();
            _actionRows.Clear(); ActionChecks.Clear();
            AcceptButton = EditButton = DismissButton = ApplyEditButton = null;
            _timerFill = null; _steps = null; _thinkingTime = null;
            var phase = G.Phase;
            var rec = G.Pending;
            _shownPhase = phase; _shownCard = rec;

            var head = E("advisor-head", _advisor);
            L("Advisor", "advisor-title semibold", head);

            if (G.Mode == AdvisorMode.Off)
            {
                L("Off for this exercise", "advisor-meta", head);
                return;
            }

            if (phase == Phase.Advising && rec != null)
            {
                L(Clock(rec.Time), "advisor-meta", head);
                var t = E("thinking", _advisor);
                L(rec.Trigger, "trigger", t);
                _thinkingTime = L("", "muted", t);
                _steps = E("steps", t);
                return;
            }

            if (phase == Phase.Deciding && rec?.Card != null)
            {
                var card = rec.Card;
                L(Clock(rec.Time), "advisor-meta", head);
                E("grow", head);
                if (G.Mode == AdvisorMode.Full) L($"{Cap(card.Confidence)} confidence", "conf medium", head);
                var body = E("advisor-body", _advisor);
                body.EnableInClassList("editing", _editing);
                L(card.Headline, "headline semibold", body);
                if (card.Actions.Count == 0) L("No change to current orders.", "para", body);
                _keep.Clear();
                for (int i = 0; i < card.Actions.Count; i++)
                {
                    var a = card.Actions[i];
                    _keep.Add(i);
                    var row = E("action", body);
                    var chk = E("check on", row);
                    ActionChecks.Add(chk);
                    var idx = i;
                    row.AddManipulator(new Clickable(() => { if (_editing) ToggleKeep(idx); }));
                    var tx = E("grow", row);
                    L(a.Describe(G.Sim), "action-text medium", tx);
                    if (G.Mode == AdvisorMode.Full && !string.IsNullOrEmpty(a.Reason)) L(a.Reason, "action-reason", tx);
                    _actionRows.Add(row);
                }
                if (G.Mode == AdvisorMode.Full)
                {
                    var why = E("block", body);
                    L("Why", "block-title medium", why);
                    L(card.Rationale, "para", why);
                    if (card.ExpectedEffect.Count > 0)
                    {
                        var eff = E("block", body);
                        var hr = E("effect", eff);
                        L("Expected in 20 minutes", "effect-label block-title medium", hr);
                        L("Now", "effect-val faint", hr);
                        L("", "effect-arrow", hr);
                        L("With plan", "effect-val faint", hr);
                        foreach (var m in card.ExpectedEffect)
                        {
                            var r = E("effect", eff);
                            L(m.Label, "effect-label", r);
                            L(Short(m.Without), "effect-val muted", r);
                            L("→", "effect-arrow", r);
                            L(Short(m.With), "effect-val medium advisor-ink", r);
                        }
                    }
                    var concerns = card.SensorConcerns.Concat(rec.Cautions.Select(c => c.Message)).Take(3).ToList();
                    if (concerns.Count > 0)
                    {
                        var sc = E("block", body);
                        L("Not trusted", "block-title medium", sc);
                        foreach (var c in concerns) L(c, "bullet caution", sc);
                    }
                    if (card.Evidence.Count > 0)
                    {
                        var ev = E("block", body);
                        L("Evidence", "block-title medium", ev);
                        foreach (var e in card.Evidence.Take(3)) L(e, "bullet", ev);
                    }
                    if (!string.IsNullOrEmpty(card.WouldChangeMind))
                    {
                        var wc = E("block", body);
                        L("Would change this plan", "block-title medium", wc);
                        L(card.WouldChangeMind, "para", wc);
                    }
                    var trace = Trace(rec);
                    if (card.LessonsCited.Count > 0) trace += $" · applied lesson {string.Join(", ", card.LessonsCited)}";
                    L(trace, "trace", body);
                }
                var foot = E("advisor-foot", _advisor);
                var timer = E("timer", foot);
                _timerFill = E("timer-fill", timer);
                var fb = E("foot-buttons", foot);
                if (_editing)
                {
                    ApplyEditButton = B("Apply selected", "accent medium", ApplyEdit, fb);
                    B("Cancel", "ghost medium", () => { _editing = false; _shownCard = null; }, fb);
                }
                else
                {
                    AcceptButton = B("Accept plan", "accent medium", () => G.Accept(), fb);
                    if (card.Actions.Count > 1) EditButton = B("Edit", "medium", BeginEdit, fb);
                    DismissButton = B("Dismiss", "ghost medium", () => G.Dismiss(), fb);
                }
                return;
            }

            var idle = E("idle", _advisor);
            var last = G.Decisions.LastOrDefault(d => d.Card != null);
            if (phase == Phase.Briefing) L("Ready.", "para muted", idle);
            else if (last == null) L("Watching the incident. Advice arrives when something changes.", "para muted", idle);
            else
            {
                L($"Last advice at {Clock(last.Time)}: {last.Card.Headline}", "para", idle);
                L(last.Response == "accepted" ? "Accepted" : last.Response == "edited" ? "Accepted with changes" : last.Response == "dismissed" ? "Dismissed" : last.Response == "expired" ? "Expired without a decision" : last.Response, "trace", idle);
            }
            var fail = G.Decisions.LastOrDefault();
            if (fail != null && fail.Response == "failed") L("The last advisor request failed: " + fail.Error, "bullet caution", idle);
        }

        public void BeginEdit() { _editing = true; _shownCard = null; }

        public void ToggleKeep(int idx)
        {
            if (_keep.Contains(idx)) _keep.Remove(idx); else _keep.Add(idx);
            if (idx < ActionChecks.Count)
            {
                ActionChecks[idx].EnableInClassList("on", _keep.Contains(idx));
                _actionRows[idx].EnableInClassList("off", !_keep.Contains(idx));
            }
        }

        public void ApplyEdit() { _editing = false; G.ApplyEdited(_keep.ToList()); }

        static string Cap(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("Current orders:", "").Replace("current orders:", "").Replace("With plan:", "").Replace("Plan:", "").Trim();
            return s.Length > 12 ? s.Substring(0, 12) : s;
        }

        static string Trace(DecisionRecord rec)
        {
            var tools = rec.ToolCalls ?? new List<string>();
            int tests = tools.Count(t => t == "test_plan");
            var parts = new List<string>();
            if (tools.Contains("get_sensor_check")) parts.Add("cross-checked sensors");
            if (tools.Contains("get_forecast")) parts.Add("ran the spread forecast");
            if (tests > 0) parts.Add(tests == 1 ? "tested 1 plan" : $"tested {tests} plans");
            var head = parts.Count > 0 ? "Advisor " + string.Join(", ", parts) : "Advisor";
            return $"{head} in {rec.AdvisorSeconds:F0} s";
        }

        static readonly Dictionary<string, string> StepNames = new Dictionary<string, string>
        {
            { "get_situation", "Reading the situation" },
            { "get_sensor_check", "Cross-checking sensors" },
            { "get_forecast", "Running the spread forecast" },
            { "test_plan", "Testing a plan" },
            { "read_lessons", "Reading past lessons" },
        };

        void UpdateThinking()
        {
            if (_steps == null) return;
            var tools = G.LiveTools;
            var rows = new List<string>();
            int tests = 0;
            foreach (var t in tools)
            {
                if (!StepNames.TryGetValue(t, out var name)) continue;
                if (t == "test_plan") { tests++; name = tests == 1 ? "Testing a plan" : $"Testing plan {tests}"; }
                if (!rows.Contains(name)) rows.Add(name);
            }
            if (_steps.childCount != rows.Count + 1)
            {
                _steps.Clear();
                foreach (var r in rows)
                {
                    var s = E("step", _steps); E("step-dot done", s); L(r, "step-text done", s);
                }
                var cur = E("step", _steps); E("step-dot", cur); L(rows.Count == 0 ? "Starting" : "Working", "step-text", cur);
            }
            var el = Time.realtimeSinceStartup - G.AdvisorStarted;
            if (G.CaptureMode) el = 0;
            if (_thinkingTime != null) _thinkingTime.text = G.CaptureMode ? "Simulation is holding while the advisor works." : $"Simulation is holding while the advisor works · {el:F0} s";
        }

        // ------------------------------------------------------------------ feed, inspector, overlays

        void BuildFeed() { _feed = E("panel feed", _app); }

        void OnEvent(SimEvent e)
        {
            if (e.Kind == "order") return;
            var cls = e.Kind == "fire" || e.Kind == "deflagration" || e.Kind == "injury" || e.Kind == "venting" ? "danger" : "";
            _events.Add((e.Time, e.Text, cls));
            RenderFeed();
        }

        void RenderFeed()
        {
            _feed.Clear();
            var items = _events.Skip(Math.Max(0, _events.Count - 6)).ToList();
            _feed.style.display = items.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            foreach (var it in items)
            {
                var r = E("feed-row", _feed);
                L($"+{it.t / 60:0}", "feed-time", r);
                L(it.text, "feed-text " + it.cls, r);
            }
        }

        void BuildInspector()
        {
            _inspector = E("panel inspector", _app);
            _inspector.style.display = DisplayStyle.None;
        }

        void RenderInspector()
        {
            var id = Site.SelectedContainer;
            if (id == null || G.Phase == Phase.Review || G.Phase == Phase.Briefing) { _inspector.style.display = DisplayStyle.None; return; }
            _inspector.style.display = DisplayStyle.Flex;
            _inspector.Clear();
            var i = Runaway.Sim.Site.ContainerIndex(id);
            var o = G.Sim.Obs.Containers[i];
            var head = E("row", _inspector);
            L(id, "inspect-title semibold", head);
            E("grow", head);
            L(o.Visible == "normal" ? (o.CooledBy != null ? $"Cooled by {o.CooledBy}" : "Not cooled") : VisibleText(o.Visible), "muted", head);
            KV("Battery system", o.Bms.HasValue ? $"{o.Bms:F0} °C" + (o.RatePerMin.HasValue && Math.Abs(o.RatePerMin.Value) >= 0.5 ? $"  {(o.RatePerMin > 0 ? "+" : "")}{o.RatePerMin:F1}/min" : "") : "No reading");
            KV("Thermal camera", o.Thermal.HasValue ? $"{o.Thermal:F0} °C skin, about {14 + (o.Thermal - 14) / 0.6:F0} °C inside" : "Needs Aerial 1");
            KV("Gas", o.GasVerified.HasValue ? $"{o.GasVerified:F0}% LEL (HAZMAT)" : $"{o.GasRaw:F0}% LEL (fixed detector)");
            if (G.Risk != null && o.Visible == "normal") KV("Forecast runaway", $"{G.Risk[i * 2] * 100:F0}% in 10 min, {G.Risk[i * 2 + 1] * 100:F0}% in 20");
            if (o.SensorConflict) L("Battery reading and thermal camera disagree.", "bullet caution", _inspector);
        }

        void KV(string k, string v)
        {
            var r = E("kv", _inspector);
            L(k, "k", r);
            L(v, "v medium", r);
        }

        static string VisibleText(string v) => v == "smoke" ? "Venting" : v == "fire" ? "On fire" : v == "burnt_out" ? "Burnt out" : "";

        void BuildOverlays()
        {
            var o = E("panel overlays", _app);
            PlumeToggle = L("HF plume", "seg medium on", o);
            PlumeToggle.AddManipulator(new Clickable(() => SetPlume(!Site.Plume.Visible)));
            RiskToggle = L("Spread risk", "seg medium on", o);
            RiskToggle.AddManipulator(new Clickable(() => SetRisk(!Site.ShowRisk)));
            ThermalToggle = L("Thermal", "seg medium", o);
            ThermalToggle.AddManipulator(new Clickable(() => SetThermal(!Site.ShowThermal)));
            _legend = E("panel legend", _app);
            L("Modelled HF at ground level", "block-title medium", _legend);
            LegendRow(Fx.Hex("#F2C75C"), "Under 1 ppm: smell, mild irritation");
            LegendRow(Fx.Hex("#F98538"), "1 ppm line: discomfort threshold");
            LegendRow(Fx.Hex("#DC389E"), "24 ppm line: serious health effects");
        }

        void LegendRow(Color c, string text)
        {
            var r = E("legend-row", _legend);
            var s = E("swatch", r); s.style.backgroundColor = c;
            L(text, "legend-text", r);
        }

        public void SetPlume(bool on) { Site.Plume.Visible = on; PlumeToggle.EnableInClassList("on", on); _legend.style.display = on ? DisplayStyle.Flex : DisplayStyle.None; }
        public void SetRisk(bool on) { Site.ShowRisk = on; RiskToggle.EnableInClassList("on", on); }
        public void SetThermal(bool on) { Site.ShowThermal = on; ThermalToggle.EnableInClassList("on", on); }

        // ------------------------------------------------------------------ world labels

        void BuildLabels()
        {
            for (int i = 0; i < 12; i++)
            {
                var t = E("tag", _labels); t.pickingMode = PickingMode.Ignore;
                var id = L(Runaway.Sim.Site.ContainerId(i), "tag-id semibold", t);
                var v = L("", "tag-val medium", t);
                var r = L("", "tag-risk medium", t);
                var f = L("!", "tag-flag semibold", t);
                foreach (var x in new VisualElement[] { id, v, r, f }) x.pickingMode = PickingMode.Ignore;
                _ctags.Add((t, id, v, r, f));
            }
            foreach (var u in G.Sim.Units)
            {
                var t = E("tag unit-tag" + (u.Kind == UnitKind.Hazmat ? " hazmat" : ""), _labels); t.pickingMode = PickingMode.Ignore;
                L(u.Id, "tag-id semibold", t).pickingMode = PickingMode.Ignore;
                _utags[u.Id] = t;
            }
            foreach (var r in G.Sim.Receptors)
            {
                var t = E("tag community-tag", _labels); t.pickingMode = PickingMode.Ignore;
                L(r.Name, "tag-id medium", t).pickingMode = PickingMode.Ignore;
                var sub = L("", "tag-sub", t); sub.pickingMode = PickingMode.Ignore;
                _rtags[r.Id] = (t, sub);
            }
        }

        bool Place(VisualElement tag, Vector3 world, float yOffset = 0)
        {
            var cam = Rig.Cam;
            var vp = cam.WorldToViewportPoint(world);
            var w = _root.layout.width; var h = _root.layout.height;
            if (vp.z < 0 || vp.x < -0.05f || vp.x > 1.05f || vp.y < -0.05f || vp.y > 1.05f || float.IsNaN(w)) { tag.style.display = DisplayStyle.None; return false; }
            tag.style.display = DisplayStyle.Flex;
            var tw = tag.layout.width; if (float.IsNaN(tw)) tw = 40;
            tag.style.left = vp.x * w - tw * 0.5f;
            tag.style.top = (1 - vp.y) * h - 26 + yOffset;
            return true;
        }

        void UpdateLabels()
        {
            var sim = G.Sim; var obs = sim.Obs;
            bool hide = G.Phase == Phase.Review || G.Phase == Phase.Briefing;
            for (int i = 0; i < 12; i++)
            {
                var (tag, id, val, risk, flag) = _ctags[i];
                if (hide) { tag.style.display = DisplayStyle.None; continue; }
                var o = obs.Containers[i];
                if (!Place(tag, Site.Containers[i].Top + Vector3.up * 1.2f)) continue;
                if (o.Visible == "normal")
                {
                    val.text = $"{o.Estimate:F0}°";
                    val.style.color = Palette.Temp(o.Estimate);
                }
                else
                {
                    val.text = VisibleText(o.Visible);
                    val.style.color = o.Visible == "burnt_out" ? Palette.Muted : Palette.Danger;
                }
                float p = G.Risk != null && o.Visible == "normal" ? G.Risk[i * 2 + 1] : 0;
                bool showRisk = Site.ShowRisk && p >= 0.1f;
                risk.style.display = showRisk ? DisplayStyle.Flex : DisplayStyle.None;
                if (showRisk) { risk.text = $"{p * 100:F0}%"; risk.style.color = Palette.Risk(p); }
                flag.style.display = o.SensorConflict ? DisplayStyle.Flex : DisplayStyle.None;
                tag.EnableInClassList("cooled", o.CooledBy != null);
                tag.EnableInClassList("selected", Site.SelectedContainer == o.Id);
            }
            foreach (var u in sim.Units)
            {
                var tag = _utags[u.Id];
                if (hide || !Site.Units[u.Id].Visible) { tag.style.display = DisplayStyle.None; continue; }
                Place(tag, Site.Units[u.Id].LabelAnchor);
            }
            foreach (var r in sim.Receptors)
            {
                var (tag, sub) = _rtags[r.Id];
                if (hide) { tag.style.display = DisplayStyle.None; continue; }
                Place(tag, Site.ReceptorAnchors[r.Id]);
                var ro = obs.Receptors.First(x => x.Id == r.Id);
                sub.text = ProtectText(ro.Action);
            }
        }

        static string ProtectText(Protection p) => p == Protection.Shelter ? "Sheltering" : p == Protection.Evacuating ? "Evacuating" : p == Protection.Evacuated ? "Evacuated" : "";

        // ------------------------------------------------------------------ modals

        void OnPhase()
        {
            _shownCard = null;
            if (_modal != null) { _modal.RemoveFromHierarchy(); _modal = null; }
            if (G.Phase == Phase.Briefing) ShowBriefing();
            if (G.Phase == Phase.Review) ShowReview();
            if (G.Phase != Phase.Deciding) _editing = false;
            if (G.Phase == Phase.Briefing) { _events.Clear(); RenderFeed(); }
        }

        void ShowBriefing()
        {
            _modal = E("scrim", _app);
            var p = E("panel brief", _modal);
            L("Glenvale battery fire", "brief-title semibold", p);
            L("Tuesday 22:40 · Glenvale grid battery, 12 enclosures", "brief-time", p);
            L("The battery management system has raised an over-temperature alarm on enclosure B3. You are the Incident Controller. Five appliances are responding and Pumper 1 is seven minutes out. Homes sit 240 m to the east and Glenvale Primary 270 m to the north-east.", "brief-text", p);
            L("Keep the public out of toxic smoke, keep crews safe, and stop the fire spreading.", "brief-goal medium", p);
            var modes = E("modes", p);
            ModeCards.Clear();
            foreach (var (mode, name) in new[] { (AdvisorMode.Full, "Advisor with reasoning"), (AdvisorMode.ActionsOnly, "Advisor, actions only"), (AdvisorMode.Off, "No advisor") })
            {
                var m = E("mode", modes);
                L(name, "mode-name medium", m);
                var mm = mode;
                m.AddManipulator(new Clickable(() => ChooseMode(mm)));
                ModeCards[mode] = m;
            }
            ChooseMode(_chosen);
            var foot = E("brief-foot", p);
            L("Right-drag to orbit, scroll to zoom. Select a crew, then click an enclosure to cool it.", "controls-hint", foot);
            StartButton = B("Start", "primary semibold", () => G.StartExercise(_chosen), foot);
        }

        public void ChooseMode(AdvisorMode m)
        {
            _chosen = m;
            foreach (var kv in ModeCards) kv.Value.EnableInClassList("on", kv.Key == m);
        }

        void ShowReview()
        {
            _modal = E("scrim", _app);
            var p = E("panel review", _modal);
            var sim = G.Sim; var o = sim.Outcome();
            L("After-action review", "review-title semibold", p);
            var modeName = G.Mode == AdvisorMode.Full ? "advisor with reasoning" : G.Mode == AdvisorMode.ActionsOnly ? "advisor, actions only" : "no advisor";
            L($"Exercise ended at {Clock(sim.Time)} · {modeName} · {G.Decisions.Count(d => d.Card != null)} decisions", "review-sub", p);
            var stats = E("stats", p);
            var worst = o.Receptors.OrderByDescending(r => r.PeakIndoor).First();
            Stat(stats, $"{o.Lost}", "of 12 enclosures lost", o.Lost <= 1 ? "good" : o.Lost <= 3 ? "caution" : "danger");
            Stat(stats, $"{worst.PeakIndoor:F1} ppm", $"worst HF indoors, {worst.Name}", worst.PeakIndoor < 1 ? "good" : worst.PeakIndoor < 24 ? "caution" : "danger");
            Stat(stats, $"{o.Injuries}", o.Injuries == 1 ? "crew injured" : "crew injured", o.Injuries == 0 ? "good" : "danger");
            Stat(stats, o.ContainedMin.HasValue ? Clock(o.ContainedMin.Value * 60) : "No", "spread stopped", o.ContainedMin.HasValue ? "good" : "danger");

            var chart = new TimelineChart(sim, G.Decisions);
            chart.AddToClassList("chart");
            p.Add(chart);

            var cols = E("cols", p);
            var left = E("col", cols);
            L("Decisions", "col-title medium", left);
            var ds = new ScrollView(ScrollViewMode.Vertical); ds.style.flexGrow = 1; left.Add(ds);
            foreach (var d in G.Decisions.Where(d => d.Card != null))
            {
                var box = E("decision", ds);
                var h = E("decision-head", box);
                L($"+{d.Time / 60:0}", "decision-time", h);
                L(RespText(d.Response), "response medium " + d.Response, h);
                L(d.Card.Headline, "decision-headline medium", box);
                if (d.Counterfactual != null)
                {
                    var cf = d.Counterfactual;
                    var cw = cf.Receptors.OrderByDescending(r => r.PeakIndoor).First();
                    L($"{d.CounterfactualLabel}: {cf.Lost} lost, worst indoor HF {cw.PeakIndoor:F1} ppm, {cf.Injuries} injured.", "decision-cf", box);
                }
            }
            if (G.Decisions.All(d => d.Card == null)) L("No advisor in this exercise.", "muted", ds);

            var mid = E("col", cols);
            L("What was really happening", "col-title medium", mid);
            foreach (var e in sim.Events.Where(e => e.Hidden)) L($"+{e.Time / 60:0}  {e.Text}", "truth", mid);
            if (!sim.Events.Any(e => e.Hidden)) L("Nothing hidden.", "muted", mid);

            var right = E("col", cols);
            right.style.marginRight = 0;
            L("Lessons for next time", "col-title medium", right);
            _lessonsBox = E("", right);
            RenderLessons();

            var foot = E("review-foot", p);
            RunAgainButton = B("Run again", "primary semibold", () => { G.NewRun(); Site.Rebind(G.Sim); }, foot);
        }

        VisualElement _lessonsBox;
        int _lessonsShown = -1;

        void RenderLessons()
        {
            if (_lessonsBox == null) return;
            _lessonsBox.Clear();
            if (G.Mode == AdvisorMode.Off) { L("Lessons are written by the advisor.", "muted", _lessonsBox); return; }
            if (G.LessonsPending) { L("The advisor is writing lessons from this run.", "muted", _lessonsBox); return; }
            foreach (var l in G.NewLessons)
            {
                var b = E("lesson", _lessonsBox);
                L($"{l.Id}  {l.Title}", "lesson-title medium", b);
                L(l.Text, "lesson-text", b);
            }
            if (G.NewLessons.Count == 0) L("No new lessons.", "muted", _lessonsBox);
            _lessonsShown = G.NewLessons.Count;
        }

        static string RespText(string r) => r == "accepted" ? "Accepted" : r == "edited" ? "Edited" : r == "dismissed" ? "Dismissed" : r == "expired" ? "Expired" : r;

        static void Stat(VisualElement parent, string num, string label, string cls)
        {
            var s = E("stat", parent);
            L(num, "stat-num semibold " + cls, s);
            L(label, "stat-label", s);
        }

        // ------------------------------------------------------------------ frame update

        void Update()
        {
            if (G == null || _root == null) return;
            var sim = G.Sim;
            var obs = sim.Obs;

            if (_shownCard != G.Pending || _shownPhase != G.Phase) RenderAdvisor();
            if (G.Phase == Phase.Advising) UpdateThinking();
            if (_timerFill != null) _timerFill.style.width = Length.Percent(Mathf.Clamp01(G.DecisionLeft / G.DecisionWindow) * 100);
            if (G.Phase == Phase.Review && _lessonsShown != G.NewLessons.Count + (G.LessonsPending ? 1000 : 0)) { RenderLessons(); if (G.LessonsPending) _lessonsShown = G.NewLessons.Count + 1000; }

            if (Time.unscaledTime > _toastUntil) _toast.RemoveFromClassList("show");

            _clock.text = Clock(sim.Time);
            string state; string dot = "";
            switch (G.Phase)
            {
                case Phase.Advising: state = "Holding while the advisor works"; dot = "holding"; break;
                case Phase.Deciding: state = "Holding for your decision"; dot = "holding"; break;
                case Phase.Review: state = "Exercise complete"; dot = "paused"; break;
                case Phase.Briefing: state = "Not started"; dot = "paused"; break;
                default: state = G.Paused ? "Paused" : $"Running at {G.Speed:0}×"; dot = G.Paused ? "paused" : ""; break;
            }
            _state.text = state;
            _stateDot.EnableInClassList("holding", dot == "holding");
            _stateDot.EnableInClassList("paused", dot == "paused");
            _pauseSeg.EnableInClassList("on", G.Paused);
            foreach (var (el, s) in _speedSegs) el.EnableInClassList("on", !G.Paused && Mathf.Approximately(G.Speed, s));

            // wind: station arrow points where the wind blows to
            _arrowStation.style.rotate = new Rotate((float)Angle.Wrap(obs.StationFrom + 180));
            bool smoke = obs.SmokeToward.HasValue;
            _arrowSmoke.style.display = smoke ? DisplayStyle.Flex : DisplayStyle.None;
            if (smoke) _arrowSmoke.style.rotate = new Rotate((float)obs.SmokeToward.Value);
            _windMain.text = $"Wind from {obs.StationFrom:000}° at {obs.StationSpeed:F1} m/s";
            if (smoke)
            {
                var smokeFrom = Angle.Wrap(obs.SmokeToward.Value + 180);
                var diff = Math.Abs(Angle.Delta(obs.StationFrom, smokeFrom));
                _windSub.text = diff > 25 ? $"Smoke drift says from {smokeFrom:000}°. The anemometer disagrees." : $"Smoke drift agrees, from {smokeFrom:000}°";
                _windSub.EnableInClassList("caution", diff > 25);
            }
            else { _windSub.text = "Site anemometer"; _windSub.RemoveFromClassList("caution"); }

            foreach (var r in obs.Receptors)
            {
                var (ppm, act) = _community[r.Id];
                if (r.Measured.HasValue) { ppm.text = $"{r.Measured:F2} ppm"; ppm.style.color = r.Measured > Phys.Aegl1 ? Palette.Danger : r.Measured > 0.3 ? Palette.Caution : Palette.Ink; }
                else { ppm.text = "Not monitored"; ppm.style.color = Palette.Muted; }
                act.text = ProtectText(r.Action);
            }
            _involved.text = $"{sim.Containers.Count(c => c.Involved)}";

            foreach (var u in sim.Units)
            {
                var (row, status, fill) = _unitRows[u.Id];
                status.text = UnitText(sim, u);
                var pct = Mathf.Clamp01((float)(u.Exposure / Phys.ExposureLimit));
                fill.style.width = Length.Percent(pct * 100);
                fill.EnableInClassList("warn", pct > 0.5f);
                fill.EnableInClassList("high", pct > 0.8f);
                row.EnableInClassList("selected", Site.SelectedUnit == u.Id);
                row.EnableInClassList("dim", !u.OnScene || u.Injured);
            }

            if (Time.unscaledTime > _nextSlow) { _nextSlow = Time.unscaledTime + 0.25f; RenderInspector(); }
            UpdateLabels();
            HandleWorldClicks();
            HandleKeys();
        }

        static string UnitText(IncidentSim sim, Unit u)
        {
            if (u.Injured) return "Crew injured, out of service";
            var tgt = u.Target >= 0 ? Runaway.Sim.Site.ContainerId(u.Target) : null;
            switch (u.Status)
            {
                case UnitStatus.EnRoute: return $"Arrives in {Math.Max(0, (u.ArriveAt - sim.Time) / 60):0} min" + (tgt != null ? $", then {tgt}" : "");
                case UnitStatus.Staged: return $"Staged at {Runaway.Sim.Site.StagingName(u.Staging)}";
                case UnitStatus.Moving: return tgt != null ? $"Moving to {tgt}" : $"Moving to {Runaway.Sim.Site.StagingName(u.Staging)}";
                case UnitStatus.SettingUp: return $"Setting up on {tgt}";
                case UnitStatus.Cooling: return u.Kind == UnitKind.Aerial ? $"Cooling {tgt}, camera on site" : $"Cooling {tgt}";
                case UnitStatus.Withdrawn: return $"In rehab until {Clock(u.RehabUntil)}";
            }
            return "";
        }

        // ------------------------------------------------------------------ input

        public bool PointerOverUi()
        {
            var m = Mouse.current;
            if (m == null || _root?.panel == null) return false;
            var sp = m.position.ReadValue();
            var pp = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(sp.x, Screen.height - sp.y));
            var picked = _root.panel.Pick(pp);
            return picked != null;
        }

        void HandleWorldClicks()
        {
            if (G.CaptureMode) return;
            var m = Mouse.current;
            if (m == null || !m.leftButton.wasPressedThisFrame || PointerOverUi()) return;
            if (G.Phase == Phase.Briefing || G.Phase == Phase.Review) return;
            var ray = Rig.Cam.ScreenPointToRay(m.position.ReadValue());
            if (Physics.Raycast(ray, out var hit, 2000f))
            {
                var cv = hit.collider.GetComponentInParent<ContainerView>();
                if (cv != null) { ClickContainer(cv.Id); return; }
                var uv = hit.collider.GetComponentInParent<UnitView>();
                if (uv != null) { SelectUnit(uv.Id); return; }
            }
            Site.SelectedContainer = null;
        }

        public void ClickContainer(string id)
        {
            var su = Site.SelectedUnit;
            if (su != null && G.Sim.UnitById(su).Kind != UnitKind.Hazmat)
            {
                Issue(new Order { Type = OrderType.Cool, Unit = su, Container = id });
                SelectUnit(null);
            }
            Site.SelectedContainer = id;
        }

        void HandleKeys()
        {
            var kb = Keyboard.current;
            if (kb == null || G.CaptureMode) return;
            if (kb.spaceKey.wasPressedThisFrame) G.TogglePause();
            if (kb.escapeKey.wasPressedThisFrame) { SelectUnit(null); Site.SelectedContainer = null; }
            if (kb.enterKey.wasPressedThisFrame && G.Phase == Phase.Deciding && !_editing) G.Accept();
        }

        // ------------------------------------------------------------------ demo cursor

        void DrawCursor(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            p.fillColor = Color.white; p.strokeColor = new Color(0, 0, 0, 0.8f); p.lineWidth = 1.5f;
            p.BeginPath();
            p.MoveTo(new Vector2(0, 0)); p.LineTo(new Vector2(0, 18)); p.LineTo(new Vector2(4.6f, 13.8f)); p.LineTo(new Vector2(8.2f, 21f));
            p.LineTo(new Vector2(11f, 19.6f)); p.LineTo(new Vector2(7.6f, 12.8f)); p.LineTo(new Vector2(13.4f, 12.8f)); p.ClosePath();
            p.Fill(); p.Stroke();
        }

        public Vector2 PanelCenter(VisualElement e)
        {
            var wb = e.worldBound;
            return wb.center;
        }

        public Vector2 PanelSize => new Vector2(_root.layout.width, _root.layout.height);

        public Vector2 WorldToPanel(Vector3 world)
        {
            var vp = Rig.Cam.WorldToViewportPoint(world);
            return new Vector2(vp.x * _root.layout.width, (1 - vp.y) * _root.layout.height);
        }
    }

    /// <summary>Involvement over time with decision markers and the hidden ground-truth events.</summary>
    public sealed class TimelineChart : VisualElement
    {
        readonly IncidentSim _sim;
        readonly List<DecisionRecord> _decisions;

        public TimelineChart(IncidentSim sim, List<DecisionRecord> decisions)
        {
            _sim = sim; _decisions = decisions;
            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(_ => Labels());
        }

        void Labels()
        {
            Clear();
            var w = layout.width; var h = layout.height;
            if (float.IsNaN(w)) return;
            double end = Math.Max(60, _sim.Time);
            for (int m = 0; m <= end / 60; m += 10)
            {
                var l = new Label($"+{m}"); l.AddToClassList("chart-label");
                l.style.left = (float)(16 + (w - 32) * m * 60 / end) - 8; l.style.top = h - 18;
                Add(l);
            }
            foreach (var e in _sim.Events.Where(e => e.Hidden))
            {
                var l = new Label(e.Kind == "wind" ? "Wind shift" : e.Text.Contains("anemometer") ? "Anemometer fails" : e.Subject + " sensor fails");
                l.AddToClassList("chart-label");
                l.style.left = (float)(16 + (w - 32) * e.Time / end) + 4; l.style.top = 6;
                l.style.color = Palette.Caution;
                Add(l);
            }
            var y = new Label("Enclosures involved"); y.AddToClassList("chart-label"); y.style.left = 18; y.style.top = 24; Add(y);
        }

        void Draw(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            var w = contentRect.width; var h = contentRect.height;
            double end = Math.Max(60, _sim.Time);
            float X(double t) => (float)(16 + (w - 32) * t / end);
            float top = 44, bottom = h - 24;
            float Y(int n) => bottom - (bottom - top) * n / 12f;

            p.strokeColor = new Color(1, 1, 1, 0.06f); p.lineWidth = 1;
            for (int n = 0; n <= 12; n += 4) { p.BeginPath(); p.MoveTo(new Vector2(16, Y(n))); p.LineTo(new Vector2(w - 16, Y(n))); p.Stroke(); }

            var times = _sim.Containers.Where(c => c.VentStart >= 0).Select(c => c.VentStart).OrderBy(t => t).ToList();
            p.fillColor = new Color(1f, 0.36f, 0.3f, 0.22f);
            p.strokeColor = new Color(1f, 0.42f, 0.36f, 0.95f); p.lineWidth = 2;
            p.BeginPath();
            p.MoveTo(new Vector2(X(0), Y(0)));
            int count = 0;
            foreach (var t in times) { p.LineTo(new Vector2(X(t), Y(count))); count++; p.LineTo(new Vector2(X(t), Y(count))); }
            p.LineTo(new Vector2(X(end), Y(count)));
            p.Stroke();
            p.BeginPath();
            p.MoveTo(new Vector2(X(0), Y(0)));
            count = 0;
            foreach (var t in times) { p.LineTo(new Vector2(X(t), Y(count))); count++; p.LineTo(new Vector2(X(t), Y(count))); }
            p.LineTo(new Vector2(X(end), Y(count))); p.LineTo(new Vector2(X(end), Y(0))); p.ClosePath();
            p.Fill();

            foreach (var e in _sim.Events.Where(e => e.Hidden))
            {
                p.strokeColor = new Color(0.95f, 0.72f, 0.29f, 0.7f); p.lineWidth = 1;
                for (float yy = top - 20; yy < bottom; yy += 8) { p.BeginPath(); p.MoveTo(new Vector2(X(e.Time), yy)); p.LineTo(new Vector2(X(e.Time), yy + 4)); p.Stroke(); }
            }
            foreach (var d in _decisions.Where(d => d.Card != null))
            {
                var c = d.Response == "accepted" ? Palette.Good : d.Response == "edited" ? Palette.Advisor : Palette.Caution;
                p.fillColor = c;
                p.BeginPath(); p.Arc(new Vector2(X(d.Time), bottom - 2), 5, 0, 360); p.Fill();
            }
        }
    }
}
