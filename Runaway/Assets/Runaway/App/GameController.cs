using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Runaway.Advisor;
using Runaway.Sim;
using Unity.InferenceEngine;
using UnityEngine;

namespace Runaway.App
{
    public enum Phase { Briefing, Running, Advising, Deciding, Review }
    public enum AdvisorMode { Full, ActionsOnly, Off }

    /// <summary>Owns the simulation, the advisor round trips and the decision record. Views only read it.</summary>
    public sealed class GameController : MonoBehaviour
    {
        public static GameController I { get; private set; }

        public IncidentSim Sim { get; private set; }
        public Phase Phase { get; private set; } = Phase.Briefing;
        public AdvisorMode Mode = AdvisorMode.Full;
        public float Speed = 24f;               // simulated seconds per real second
        public bool Paused;
        public float DecisionWindow = 45f;      // real seconds to accept, edit or dismiss
        public float DecisionLeft { get; private set; }
        public DecisionRecord Pending { get; private set; }
        public readonly List<DecisionRecord> Decisions = new List<DecisionRecord>();
        public float[] Risk { get; private set; }   // [12*2] forecaster output
        public string AdvisorStatus { get; private set; }
        public float AdvisorStarted { get; private set; }
        public List<Lesson> NewLessons { get; private set; } = new List<Lesson>();
        public bool LessonsPending { get; private set; }
        public string RunId { get; private set; }
        public LessonStore Lessons { get; private set; }
        public bool ReviewReady { get; private set; }

        public event Action<SimEvent> EventRaised;
        public event Action PhaseChanged;
        public event Action DecisionsChanged;

        readonly ConcurrentQueue<Action> _main = new ConcurrentQueue<Action>();
        AdvisorTools _tools;
        CodexAdvisor _advisor;
        ApiServer _api;
        DecisionTriggers _triggers;
        InferenceForecaster _forecaster;
        double _acc;
        int _lastEvent;
        double _lastRisk = -1;
        string _runDir;
        public string RepoRoot { get; private set; }
        public bool CaptureMode;
        public bool SkipLessons;
        public List<string> LiveTools { get { var l = _advisor?.Live; if (l == null) return new List<string>(); lock (l) return new List<string>(l); } }
        public float FrameDelta => Time.deltaTime;

        void Awake()
        {
            I = this;
            RepoRoot = FindRepo();
            _runDir = Path.Combine(Application.persistentDataPath, "runs");
            Directory.CreateDirectory(_runDir);
            Lessons = new LessonStore(Path.Combine(Application.persistentDataPath, "lessons.json"));
            var model = Resources.Load<ModelAsset>("Models/escalation");
            if (model != null) _forecaster = new InferenceForecaster(model);
            int port = 7377;
            try { _api = new ApiServer(port, Handle); }
            catch (Exception e) { Debug.LogError("Advisor API failed to start: " + e.Message); }
            _advisor = new CodexAdvisor(new AdvisorSettings
            {
                McpDir = Path.Combine(RepoRoot, "advisor"),
                WorkDir = Path.Combine(Application.temporaryCachePath, "advisor"),
                Port = port,
            });
            NewRun();
        }

        static string FindRepo()
        {
            var env = Environment.GetEnvironmentVariable("RUNAWAY_REPO");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            var d = new DirectoryInfo(Application.dataPath);
            for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "advisor")) && Directory.Exists(Path.Combine(d.FullName, "ml"))) return d.FullName;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "workspace/runaway");
        }

        public void NewRun()
        {
            Sim = IncidentSim.Create(ScenarioConfig.Default());
            _triggers = new DecisionTriggers();
            _tools = new AdvisorTools { Sim = Sim, Forecaster = _forecaster, Lessons = Lessons };
            Decisions.Clear();
            Pending = null;
            NewLessons = new List<Lesson>();
            ReviewReady = false;
            _lastEvent = 0;
            _acc = 0;
            _lastRisk = -1;
            RunId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            UpdateRisk();
            SetPhase(Phase.Briefing);
        }

        public void StartExercise(AdvisorMode mode)
        {
            Mode = mode;
            SetPhase(Phase.Running);
        }

        void SetPhase(Phase p)
        {
            Phase = p;
            PhaseChanged?.Invoke();
        }

        Task<string> Handle(string path, string body)
        {
            var tcs = new TaskCompletionSource<string>();
            _main.Enqueue(() =>
            {
                try { tcs.SetResult(_tools.Route(path, body)); }
                catch (Exception e) { tcs.SetResult(JsonConvert.SerializeObject(new { error = e.Message })); }
            });
            return tcs.Task;
        }

        void Update()
        {
            while (_main.TryDequeue(out var a)) a();

            switch (Phase)
            {
                case Phase.Running:
                    if (!Paused) Advance(FrameDelta * Speed);
                    break;
                case Phase.Deciding:
                    DecisionLeft -= FrameDelta;
                    if (DecisionLeft <= 0) Respond("expired", new List<Order>());
                    break;
            }
        }

        void Advance(double simSeconds)
        {
            _acc += simSeconds;
            while (_acc >= Phys.Dt && Phase == Phase.Running)
            {
                _acc -= Phys.Dt;
                Sim.Step();
                FlushEvents();
                if (Sim.Time - _lastRisk >= 30) UpdateRisk();
                if (Sim.Finished || (Sim.ContainedAt > 0 && Sim.Time > Sim.ContainedAt + 8 * 60))
                {
                    EndExercise();
                    return;
                }
                if (Mode != AdvisorMode.Off)
                {
                    var reason = _triggers.Evaluate(Sim);
                    if (reason != null) { RequestAdvice(reason); return; }
                }
            }
        }

        void FlushEvents()
        {
            for (; _lastEvent < Sim.Events.Count; _lastEvent++)
            {
                var e = Sim.Events[_lastEvent];
                if (!e.Hidden) EventRaised?.Invoke(e);
            }
        }

        void UpdateRisk()
        {
            if (_forecaster == null || Sim.Obs == null) return;
            Risk = _forecaster.Predict(Sim.Obs);
            _lastRisk = Sim.Time;
        }

        // ---------------------------------------------------------------- advisor

        public void RequestAdvice(string reason)
        {
            if (Phase == Phase.Advising || Phase == Phase.Deciding) return;
            var rec = new DecisionRecord { Number = Decisions.Count + 1, Time = Sim.Time, Trigger = reason, Snapshot = Sim.Clone() };
            Decisions.Add(rec);
            Pending = rec;
            _tools.Trigger = reason;
            _tools.TestLog.Clear();
            AdvisorStatus = "Reviewing the situation";
            AdvisorStarted = Time.realtimeSinceStartup;
            SetPhase(Phase.Advising);
            DecisionsChanged?.Invoke();
            var recent = string.Join("\n", Decisions.Where(d => d != rec && d.Card != null).Reverse().Take(3).Reverse()
                .Select(d => $"T+{d.Time / 60:F0}: {d.Card.Headline} [commander {d.Response}]"));
            if (string.IsNullOrEmpty(recent)) recent = "(none)";
            var task = _advisor.Plan(reason, recent);
            task.ContinueWith(t => _main.Enqueue(() => OnAdvice(rec, t.Result)));
        }

        void OnAdvice(DecisionRecord rec, AdvisorResult res)
        {
            if (Pending != rec) return;
            rec.AdvisorSeconds = res.Seconds;
            rec.ToolCalls = res.ToolCalls;
            if (!res.Ok || res.Card == null)
            {
                rec.Response = "failed";
                rec.Error = res.Error;
                Pending = null;
                Debug.LogWarning("Advisor failed: " + res.Error);
                SetPhase(Phase.Running);
                DecisionsChanged?.Invoke();
                return;
            }
            rec.Card = res.Card;
            rec.Cautions = Doctrine.Check(Sim, res.Card.ToOrders());
            DecisionLeft = DecisionWindow;
            SetPhase(Phase.Deciding);
            DecisionsChanged?.Invoke();
            AppendLog(rec, "advice");
        }

        /// <summary>The commander's answer to the pending card. Orders are the ones actually issued.</summary>
        public void Respond(string response, List<Order> orders)
        {
            var rec = Pending;
            if (rec == null) return;
            rec.Response = response;
            foreach (var o in orders)
            {
                var r = Sim.Apply(o);
                if (r.Ok) rec.Applied.Add(o);
                else Debug.Log($"Order rejected: {o} ({r.Error})");
            }
            if (rec.Card != null && rec.Card.LessonsCited.Count > 0 && response != "dismissed") Lessons.MarkCited(rec.Card.LessonsCited);
            Pending = null;
            FlushEvents();
            UpdateRisk();
            AppendLog(rec, "response");
            SetPhase(Phase.Running);
            DecisionsChanged?.Invoke();
        }

        /// <summary>Preview tooling: show a recorded card without calling the advisor.</summary>
        public void InjectCard(DecisionRecord rec)
        {
            rec.Snapshot = Sim.Clone();
            rec.Time = Sim.Time;
            rec.Cautions = Doctrine.Check(Sim, rec.Card.ToOrders());
            Decisions.Add(rec);
            Pending = rec;
            DecisionLeft = DecisionWindow * 0.62f;
            SetPhase(Phase.Deciding);
            DecisionsChanged?.Invoke();
        }

        /// <summary>Preview tooling: advance the simulation instantly.</summary>
        public void FastForward(double minutes)
        {
            var target = Sim.Time + minutes * 60;
            while (Sim.Time < target && !Sim.Finished) { Sim.Step(); FlushEvents(); }
            UpdateRisk();
        }

        public void ForcePhase(Phase p) => SetPhase(p);

        /// <summary>Preview tooling: show stored lessons on the review screen.</summary>
        public void PreviewLessons(int n) { NewLessons = Lessons.All.Skip(Math.Max(0, Lessons.All.Count - n)).ToList(); DecisionsChanged?.Invoke(); }

        public void Accept() { if (Pending?.Card != null) Respond("accepted", Pending.Card.ToOrders()); }
        public void Dismiss() { Respond("dismissed", new List<Order>()); }
        public void ApplyEdited(IEnumerable<int> keep)
        {
            if (Pending?.Card == null) return;
            var all = Pending.Card.Actions;
            var set = new HashSet<int>(keep);
            var orders = all.Select((a, i) => (a, i)).Where(x => set.Contains(x.i)).Select(x => x.a.ToOrder()).Where(o => o != null).ToList();
            Respond(set.Count == all.Count ? "accepted" : "edited", orders);
        }

        // ---------------------------------------------------------------- commander

        public OrderResult Command(Order o)
        {
            o.Source = "commander";
            var r = Sim.Apply(o);
            FlushEvents();
            if (r.Ok) UpdateRisk();
            return r;
        }

        public List<Caution> Preview(Order o) => Doctrine.Check(Sim, new List<Order> { o });

        public void TogglePause() => Paused = !Paused;

        // ---------------------------------------------------------------- end of run

        public void EndExercise()
        {
            if (Phase == Phase.Review) return;
            Pending = null;
            foreach (var d in Decisions.Where(d => d.Card != null && d.Snapshot != null))
            {
                var later = Sim.OrderLog;
                if (d.Response == "accepted")
                {
                    d.Counterfactual = Counterfactual.Replay(d.Snapshot, new List<Order>(), later, Sim.Time);
                    d.CounterfactualLabel = "Without this advice";
                }
                else
                {
                    d.Counterfactual = Counterfactual.Replay(d.Snapshot, d.Card.ToOrders(), later, Sim.Time);
                    d.CounterfactualLabel = "If you had followed it in full";
                }
            }
            ReviewReady = true;
            if (Mode != AdvisorMode.Off && !SkipLessons) LessonsPending = true;
            SetPhase(Phase.Review);
            SaveRun();
            DecisionsChanged?.Invoke();
            if (Mode != AdvisorMode.Off && !SkipLessons)
            {
                LessonsPending = true;
                var json = DebriefBuilder.Build(Sim, Decisions, Mode.ToString());
                _advisor.Debrief(json).ContinueWith(t => _main.Enqueue(() =>
                {
                    LessonsPending = false;
                    if (t.Result.Ok && t.Result.Lessons != null) NewLessons = Lessons.Add(t.Result.Lessons.Lessons, RunId);
                    DecisionsChanged?.Invoke();
                }));
            }
        }

        void AppendLog(DecisionRecord rec, string kind)
        {
            try
            {
                var line = JsonConvert.SerializeObject(new { kind, run = RunId, rec.Number, minute = rec.Time / 60, rec.Trigger, rec.Response, rec.AdvisorSeconds, rec.ToolCalls, card = rec.Card, applied = rec.Applied.Select(o => o.ToString()) });
                File.AppendAllText(Path.Combine(_runDir, RunId + ".jsonl"), line + "\n");
            }
            catch (Exception e) { Debug.LogWarning(e.Message); }
        }

        void SaveRun()
        {
            try
            {
                var o = Sim.Outcome();
                var json = JsonConvert.SerializeObject(new
                {
                    run = RunId, mode = Mode.ToString(), outcome = o,
                    decisions = Decisions.Select(d => new { d.Number, minute = d.Time / 60, d.Trigger, d.Response, d.AdvisorSeconds, d.ToolCalls, card = d.Card, applied = d.Applied.Select(x => x.ToString()), counterfactual = d.Counterfactual, d.CounterfactualLabel }),
                    events = Sim.Events.Where(e => e.Kind != "order"),
                    orders = Sim.OrderLog.Select(x => new { minute = x.Time / 60, order = x.ToString(), x.Source }),
                }, Formatting.Indented);
                File.WriteAllText(Path.Combine(_runDir, RunId + ".json"), json);
            }
            catch (Exception e) { Debug.LogWarning(e.Message); }
        }

        void OnDestroy()
        {
            _api?.Dispose();
            _forecaster?.Dispose();
        }
    }

    /// <summary>Escalation forecaster running in-engine with Unity's Inference Engine.</summary>
    public sealed class InferenceForecaster : IForecaster, IDisposable
    {
        readonly Worker _worker;
        readonly float[] _x = new float[Features.Nodes * Features.F];
        readonly float[] _a = new float[Features.Nodes * Features.Nodes];

        public InferenceForecaster(ModelAsset asset)
        {
            _worker = new Worker(ModelLoader.Load(asset), BackendType.CPU);
        }

        public float[] Predict(Observation obs)
        {
            Features.Build(obs, _x, _a);
            using (var tx = new Tensor<float>(new TensorShape(1, Features.Nodes, Features.F), _x))
            using (var ta = new Tensor<float>(new TensorShape(1, Features.Nodes, Features.Nodes), _a))
            {
                _worker.Schedule(tx, ta);
                var o = _worker.PeekOutput() as Tensor<float>;
                return o.DownloadToArray();
            }
        }

        public void Dispose() => _worker?.Dispose();
    }
}
