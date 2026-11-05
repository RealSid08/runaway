using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Runaway.Advisor;
using Runaway.Sim;
using UnityEngine;

namespace Runaway.App
{
    /// <summary>Renders stills at chosen incident times for visual review. Usage: -preview out_dir [-cards run.json]</summary>
    public sealed class Preview : MonoBehaviour
    {
        public IEnumerator Run(string dir, string cardsPath, GameController g, Ui ui, SiteView site, CameraRig rig)
        {
            Directory.CreateDirectory(dir);
            rig.InputEnabled = false;
            g.SkipLessons = true;
            JArray decisions = null;
            if (!string.IsNullOrEmpty(cardsPath) && File.Exists(cardsPath)) decisions = (JArray)JObject.Parse(File.ReadAllText(cardsPath))["decisions"];

            rig.Target = new Vector3(40, 0, 60); rig.Yaw = 214; rig.Pitch = 44; rig.Distance = 330; rig.Snap();
            yield return Frames(30);
            yield return Shot(dir, "01-briefing");

            g.StartExercise(AdvisorMode.Off);
            g.Paused = true;
            g.Mode = AdvisorMode.Full;
            rig.Target = new Vector3(2, 0, 0); rig.Yaw = 208; rig.Pitch = 34; rig.Distance = 98; rig.Snap();
            g.FastForward(8);
            yield return Frames(20);
            yield return Shot(dir, "02-early");

            if (decisions != null && decisions.Count > 0)
            {
                Inject(g, decisions[0]);
                yield return Frames(20);
                yield return Shot(dir, "03-card-first");
                g.Accept();
            }

            g.FastForward(14);
            rig.Target = new Vector3(-3, 1, -4); rig.Yaw = 226; rig.Pitch = 22; rig.Distance = 50; rig.Snap();
            yield return Frames(240);
            yield return Shot(dir, "04-fire-close");

            g.FastForward(12);
            rig.Target = new Vector3(85, 0, 95); rig.Yaw = 22; rig.Pitch = 52; rig.Distance = 400; rig.Snap();
            yield return Frames(120);
            yield return Shot(dir, "05-plume");

            rig.Target = new Vector3(2, 0, 0); rig.Yaw = 205; rig.Pitch = 34; rig.Distance = 100; rig.Snap();
            ui.SetThermal(true);
            site.SelectedContainer = "A3";
            yield return Frames(60);
            if (decisions != null)
            {
                var conflict = decisions.FirstOrDefault(d => ((string)d["Trigger"] ?? "").Contains("battery sensor reads")) ?? decisions[Math.Min(5, decisions.Count - 1)];
                Inject(g, conflict);
                yield return Frames(20);
            }
            yield return Shot(dir, "06-thermal-card");
            if (g.Pending != null) { ui.BeginEdit(); yield return Frames(5); ui.ToggleKeep(0); yield return Frames(10); yield return Shot(dir, "07-edit"); ui.ApplyEdit(); }
            ui.SetThermal(false);
            site.SelectedContainer = null;

            g.FastForward(30);
            g.EndExercise();
            yield return Frames(40);
            yield return Shot(dir, "08-review");
            while (g.LessonsPending) yield return null;
            g.PreviewLessons(3);
            yield return Frames(10);
            yield return Shot(dir, "09-review-lessons");
            Application.Quit();
        }

        static void Inject(GameController g, JToken d)
        {
            var card = d["card"]?.ToObject<Recommendation>() ?? d["Card"]?.ToObject<Recommendation>();
            if (card == null) return;
            card.Tidy();
            var rec = new DecisionRecord
            {
                Number = g.Decisions.Count + 1,
                Trigger = (string)d["Trigger"] ?? "",
                Card = card,
                AdvisorSeconds = (double?)d["AdvisorSeconds"] ?? 30,
                ToolCalls = d["ToolCalls"]?.ToObject<System.Collections.Generic.List<string>>() ?? new System.Collections.Generic.List<string>(),
            };
            g.InjectCard(rec);
        }

        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        static IEnumerator Shot(string dir, string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
        }
    }
}
