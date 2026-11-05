using System;
using System.Collections;
using System.IO;
using System.Linq;
using Runaway.Sim;
using UnityEngine;
using UnityEngine.UIElements;
using Angle = Runaway.Sim.Angle;

namespace Runaway.App
{
    /// <summary>
    /// Records a demo run: a scripted commander drives the real interface against the live advisor,
    /// while frames are written at a fixed 30 fps. Advisor think time is time-lapsed in the recording.
    /// </summary>
    public sealed class Director : MonoBehaviour
    {
        GameController G; Ui _ui; SiteView _site; CameraRig _rig;
        string _dir, _frames, _stills;
        int _frame, _still;
        float _nextLapse;
        bool _done;
        int _editAt = 3;
        Vector2 _cursor = new Vector2(1400, 700);

        public void Begin(string dir, GameController g, Ui ui, SiteView site, CameraRig rig)
        {
            G = g; _ui = ui; _site = site; _rig = rig;
            _dir = Path.GetFullPath(dir);
            _frames = Path.Combine(_dir, "frames"); _stills = Path.Combine(_dir, "stills");
            Directory.CreateDirectory(_frames); Directory.CreateDirectory(_stills);
            G.CaptureMode = true;
            G.DecisionWindow = 600;
            G.Speed = 24;
            Time.captureFramerate = 30;
            _rig.InputEnabled = false;
            _ui.Cursor.style.display = DisplayStyle.Flex;
            MoveCursorNow(_cursor);
            StartCoroutine(Capture());
            StartCoroutine(Run());
            StartCoroutine(CameraWork());
        }

        // ------------------------------------------------------------------ recording

        bool Recording()
        {
            if (G.Phase == Phase.Advising || (G.Phase == Phase.Review && G.LessonsPending))
            {
                if (Time.realtimeSinceStartup < _nextLapse) return false;
                _nextLapse = Time.realtimeSinceStartup + 0.34f;
                return true;
            }
            return true;
        }

        IEnumerator Capture()
        {
            var wait = new WaitForEndOfFrame();
            while (!_done)
            {
                yield return wait;
                if (!Recording()) continue;
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(_frames, $"{_frame++:D6}.jpg"), tex.EncodeToJPG(92));
                if (_pendingStill != null)
                {
                    File.WriteAllBytes(Path.Combine(_stills, $"{++_still:D2}-{_pendingStill}.png"), tex.EncodeToPNG());
                    _pendingStill = null;
                }
                Destroy(tex);
            }
        }

        string _pendingStill;
        void Still(string name) => _pendingStill = name;

        IEnumerator Wait(float s)
        {
            float t = 0;
            while (t < s) { t += Time.deltaTime; yield return null; }
        }

        // ------------------------------------------------------------------ cursor

        void MoveCursorNow(Vector2 p)
        {
            _cursor = p;
            _ui.Cursor.style.left = p.x; _ui.Cursor.style.top = p.y;
        }

        IEnumerator CursorTo(Vector2 p, float dur = 0.55f)
        {
            var a = _cursor; float t = 0;
            while (t < dur)
            {
                t += Time.deltaTime;
                var k = Mathf.SmoothStep(0, 1, t / dur);
                MoveCursorNow(Vector2.Lerp(a, p, k) + Vector2.up * Mathf.Sin(k * Mathf.PI) * -14);
                yield return null;
            }
            MoveCursorNow(p);
        }

        IEnumerator Press(VisualElement e, Action act, float approach = 0.55f)
        {
            if (e == null) { act(); yield break; }
            var c = _ui.PanelCenter(e);
            yield return CursorTo(c + new Vector2(-4, -2), approach);
            yield return Wait(0.18f);
            e.AddToClassList("pressed");
            yield return Wait(0.12f);
            e.RemoveFromClassList("pressed");
            act();
            yield return Wait(0.1f);
        }

        // ------------------------------------------------------------------ script

        IEnumerator Run()
        {
            yield return Wait(0.6f);
            Still("briefing");
            yield return Wait(1.6f);
            yield return Press(_ui.ModeCards[AdvisorMode.Full], () => _ui.ChooseMode(AdvisorMode.Full));
            yield return Wait(0.5f);
            yield return Press(_ui.StartButton, () => G.StartExercise(AdvisorMode.Full));
            yield return CursorTo(new Vector2(_ui.PanelSize.x * 0.62f, _ui.PanelSize.y * 0.55f), 0.8f);

            int seen = 0;
            bool thermalShown = false, inspectShown = false;
            while (G.Phase != Phase.Review)
            {
                if (G.Phase == Phase.Advising && G.Decisions.Count == 1 && seen == 0 && !_stillAdvising) { _stillAdvising = true; yield return Wait(0.1f); Still("advisor-working"); }
                if (G.Phase == Phase.Deciding && G.Pending != null && G.Pending.Number > seen)
                {
                    var rec = G.Pending;
                    seen = rec.Number;
                    yield return Wait(0.4f);
                    Still($"card-{seen}");
                    bool conflict = rec.Trigger.Contains("thermal camera") || rec.Card.SensorConcerns.Any(s => s.Contains("A3"));
                    if (conflict && !thermalShown)
                    {
                        thermalShown = true;
                        yield return Wait(1.2f);
                        yield return Press(_ui.ThermalToggle, () => _ui.SetThermal(true));
                        var a3 = _site.Containers[Runaway.Sim.Site.ContainerIndex("A3")];
                        yield return CursorTo(_ui.WorldToPanel(a3.Top), 0.7f);
                        _site.SelectedContainer = "A3";
                        yield return Wait(2.6f);
                        Still("sensor-conflict");
                        yield return Wait(1.0f);
                    }
                    yield return Wait(2.4f);
                    var card = rec.Card;
                    if (seen == _editAt && card.Actions.Count >= 2 && _ui.EditButton != null)
                    {
                        yield return Press(_ui.EditButton, _ui.BeginEdit);
                        yield return Wait(0.6f);
                        var last = _ui.ActionChecks.LastOrDefault();
                        if (last != null)
                        {
                            yield return CursorTo(_ui.PanelCenter(last), 0.5f);
                            yield return Wait(0.2f);
                            _ui.ToggleKeep(card.Actions.Count - 1);
                            yield return Wait(0.8f);
                            Still("edit");
                        }
                        yield return Wait(0.6f);
                        yield return Press(_ui.ApplyEditButton, _ui.ApplyEdit);
                    }
                    else
                    {
                        yield return Press(_ui.AcceptButton, () => G.Accept());
                    }
                    if (thermalShown && _site.ShowThermal) { yield return Wait(0.8f); _ui.SetThermal(false); _site.SelectedContainer = null; }
                    yield return CursorTo(new Vector2(_ui.PanelSize.x * 0.58f, _ui.PanelSize.y * 0.6f), 0.9f);
                }
                if (!inspectShown && G.Sim.Containers.Any(c => c.State == CellState.Fire) && G.Phase == Phase.Running && G.Sim.Minutes > 24)
                {
                    inspectShown = true;
                    var hot = G.Sim.Containers.Where(c => c.State == CellState.Normal).OrderByDescending(c => c.T).First();
                    yield return CursorTo(_ui.WorldToPanel(_site.Containers[hot.Index].Top), 0.7f);
                    _site.SelectedContainer = hot.Id;
                    yield return Wait(2.5f);
                    Still("inspector");
                    yield return Wait(1.0f);
                    _site.SelectedContainer = null;
                }
                yield return null;
            }

            yield return Wait(1.0f);
            Still("review");
            while (G.LessonsPending) yield return null;
            yield return Wait(4.0f);
            Still("review-lessons");
            yield return Wait(3.0f);
            _done = true;
            File.WriteAllText(Path.Combine(_dir, "done.txt"), $"frames={_frame}");
            yield return null;
            Application.Quit();
        }

        bool _stillAdvising;

        // ------------------------------------------------------------------ camera

        IEnumerator CameraWork()
        {
            _rig.Target = new Vector3(40, 0, 60); _rig.Yaw = 214; _rig.Pitch = 44; _rig.Distance = 330; _rig.Snap();
            while (G.Phase == Phase.Briefing) { _rig.Yaw += Time.deltaTime * 2f; yield return null; }
            _rig.Target = new Vector3(2, 0, 0); _rig.Distance = 98; _rig.Pitch = 36; _rig.Yaw = 208;
            bool closeFire = false, plumeView = false;
            float orbit = 0;
            while (G.Phase != Phase.Review)
            {
                var sim = G.Sim;
                orbit += Time.deltaTime;
                var firstFire = sim.Containers.FirstOrDefault(c => c.State == CellState.Fire);
                bool windConflict = sim.Obs.SmokeToward.HasValue && Math.Abs(Angle.Delta(sim.Obs.StationFrom, Angle.Wrap(sim.Obs.SmokeToward.Value + 180))) > 25;
                if (firstFire != null && !closeFire)
                {
                    closeFire = true;
                    _rig.Target = SiteView.W(firstFire.Pos) + new Vector3(0, 2, 0); _rig.Distance = 52; _rig.Pitch = 22; _rig.Yaw = 228;
                    yield return Wait(9f);
                    _rig.Target = new Vector3(0, 0, 0); _rig.Distance = 96; _rig.Pitch = 34; _rig.Yaw = 200;
                }
                else if (windConflict && !plumeView && G.Phase == Phase.Running)
                {
                    plumeView = true;
                    _rig.Target = new Vector3(85, 0, 95); _rig.Distance = 400; _rig.Pitch = 52; _rig.Yaw = 22;
                    yield return Wait(6f);
                    Still("plume-school");
                    yield return Wait(4f);
                    _rig.Target = new Vector3(0, 0, 0); _rig.Distance = 100; _rig.Pitch = 34; _rig.Yaw = 205;
                }
                else
                {
                    _rig.Yaw += Time.deltaTime * 1.4f;
                }
                yield return null;
            }
            _rig.Target = new Vector3(0, 0, 0); _rig.Distance = 150; _rig.Pitch = 40;
        }
    }
}
