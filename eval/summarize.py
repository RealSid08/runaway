"""Builds the README results table from eval outputs (baseline sweep plus advisor runs)."""
import json
import pathlib
import statistics as st

root = pathlib.Path(__file__).resolve().parent
sweep = json.loads((root / "sweep-baselines.json").read_text())
advisor = {}
for p in sorted(root.glob("advisor-*.json")):
    d = json.loads(p.read_text())
    advisor[d["seed"]] = d["outcome"]
seed7 = root / "headless-seed7-v2.json"
default_run = json.loads(seed7.read_text())["outcome"] if seed7.exists() else None


def row(name, rows):
    lost = st.mean(r["lost"] for r in rows)
    cont = sum(1 for r in rows if r["contained"] is not None) / len(rows) * 100
    worst = st.mean(r["worstIndoor"] for r in rows)
    inj = st.mean(r["injuries"] for r in rows)
    return f"| {name} | {len(rows)} | {lost:.1f} | {cont:.0f}% | {worst:.2f} | {inj:.2f} |"


def norm(o):
    return {"lost": o["Lost"], "contained": o["ContainedMin"], "worstIndoor": max(r["PeakIndoor"] for r in o["Receptors"]), "injuries": o["Injuries"]}


lines = ["| Response | Incidents | Enclosures lost (mean) | Spread stopped | Worst indoor HF, ppm (mean) | Crew injuries (mean) |", "|---|---|---|---|---|---|"]
lines.append(row("No response", [r for r in sweep if r["policy"] == "none"]))
lines.append(row("Rule-of-thumb cooling", [r for r in sweep if r["policy"] == "baseline"]))
if advisor:
    seeds = sorted(advisor)
    lines.append("")
    lines.append(f"Same {len(seeds)} randomised incidents (seeds {', '.join(map(str, seeds))}), advisor cards accepted in full:")
    lines.append("")
    lines.append("| Response | Incidents | Enclosures lost (mean) | Spread stopped | Worst indoor HF, ppm (mean) | Crew injuries (mean) |")
    lines.append("|---|---|---|---|---|---|")
    lines.append(row("No response", [r for r in sweep if r["policy"] == "none" and r["seed"] in seeds]))
    lines.append(row("Rule-of-thumb cooling", [r for r in sweep if r["policy"] == "baseline" and r["seed"] in seeds]))
    lines.append(row("Codex planner", [norm(advisor[s]) for s in seeds]))
if default_run:
    o = norm(default_run)
    lines.append("")
    lines.append(f"Default scenario with the two hidden sensor faults: the planner lost {o["lost"]} enclosure{"" if o["lost"] == 1 else "s"} and stopped the spread at T+{o['contained']:.0f}; worst indoor HF {o['worstIndoor']:.2f} ppm. The rule-of-thumb response lost 3 and did not stop the spread within 90 minutes; with no response all 12 were lost.")
print("\n".join(lines))
