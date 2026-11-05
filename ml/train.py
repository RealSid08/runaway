"""Train the escalation forecaster: a two-layer graph network over the 12 battery enclosures.

Data comes from the headless simulator (tools/simrunner, `datagen`), which writes observation
features, a wind-weighted adjacency matrix and future-involvement labels. Nothing here sees
ground truth at inference time: features are built from what sensors report.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
import onnxruntime as ort
import torch
from torch import nn

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent


class EscalationGNN(nn.Module):
    def __init__(self, f: int, hidden: int = 48):
        super().__init__()
        self.self1 = nn.Linear(f, hidden)
        self.nb1 = nn.Linear(f, hidden)
        self.self2 = nn.Linear(hidden, hidden)
        self.nb2 = nn.Linear(hidden, hidden)
        self.head = nn.Linear(hidden, 2)

    def forward(self, x: torch.Tensor, adj: torch.Tensor) -> torch.Tensor:
        h = torch.relu(self.self1(x) + self.nb1(torch.matmul(adj, x)))
        h = torch.relu(self.self2(h) + self.nb2(torch.matmul(adj, h)))
        return self.head(h)  # logits, [B, 12, 2]


class Exported(nn.Module):
    """Inference graph: probabilities, with temperature scaling folded in."""

    def __init__(self, model: EscalationGNN, temperature: torch.Tensor):
        super().__init__()
        self.model = model
        self.register_buffer("t", temperature)

    def forward(self, x: torch.Tensor, adj: torch.Tensor) -> torch.Tensor:
        return torch.sigmoid(self.model(x, adj) / self.t)


def load(data_dir: Path):
    meta = json.loads((data_dir / "meta.json").read_text())
    n, nodes, f = meta["n"], meta["nodes"], meta["f"]
    x = np.fromfile(data_dir / "x.bin", dtype=np.float32).reshape(n, nodes, f)
    a = np.fromfile(data_dir / "a.bin", dtype=np.float32).reshape(n, nodes, nodes)
    y = np.fromfile(data_dir / "y.bin", dtype=np.float32).reshape(n, nodes, 2)
    m = np.fromfile(data_dir / "m.bin", dtype=np.float32).reshape(n, nodes)
    run = np.fromfile(data_dir / "run.bin", dtype=np.int32)
    return meta, x, a, y, m, run


def auc(scores: np.ndarray, labels: np.ndarray) -> float:
    order = np.argsort(scores)
    ranks = np.empty(len(scores))
    ranks[order] = np.arange(1, len(scores) + 1)
    pos = labels > 0.5
    npos, nneg = pos.sum(), (~pos).sum()
    if npos == 0 or nneg == 0:
        return float("nan")
    return float((ranks[pos].sum() - npos * (npos + 1) / 2) / (npos * nneg))


def calibration(p: np.ndarray, y: np.ndarray, bins: int = 10):
    edges = np.linspace(0, 1, bins + 1)
    rows, ece = [], 0.0
    for lo, hi in zip(edges[:-1], edges[1:]):
        sel = (p >= lo) & (p < hi if hi < 1 else p <= hi)
        if sel.sum() == 0:
            continue
        conf, acc = p[sel].mean(), y[sel].mean()
        ece += sel.mean() * abs(conf - acc)
        rows.append((float(conf), float(acc), int(sel.sum())))
    return rows, float(ece)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", type=Path, default=ROOT / "data")
    ap.add_argument("--epochs", type=int, default=8)
    ap.add_argument("--out", type=Path, default=REPO / "Runaway/Assets/Runaway/Resources/Models/escalation.onnx")
    ap.add_argument("--report", type=Path, default=REPO / "docs/forecaster")
    args = ap.parse_args()

    torch.manual_seed(7)
    np.random.seed(7)
    meta, x, a, y, m, run = load(args.data)
    runs = meta["runs"]
    perm = np.random.permutation(runs)
    test_runs = set(perm[: runs // 10].tolist())
    val_runs = set(perm[runs // 10 : runs // 5].tolist())
    is_test = np.array([r in test_runs for r in run])
    is_val = np.array([r in val_runs for r in run])
    is_train = ~(is_test | is_val)
    print(f"samples train={is_train.sum()} val={is_val.sum()} test={is_test.sum()}")

    tx, ta, ty, tm = (torch.from_numpy(v) for v in (x, a, y, m))
    model = EscalationGNN(meta["f"])
    opt = torch.optim.AdamW(model.parameters(), lr=3e-3, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)
    bce = nn.BCEWithLogitsLoss(reduction="none")
    train_idx = np.where(is_train)[0]

    def masked_loss(idx):
        logits = model(tx[idx], ta[idx])
        loss = bce(logits, ty[idx]) * tm[idx].unsqueeze(-1)
        return loss.sum() / (tm[idx].sum() * 2 + 1e-6)

    for epoch in range(args.epochs):
        model.train()
        np.random.shuffle(train_idx)
        total = 0.0
        for s in range(0, len(train_idx), 1024):
            idx = torch.from_numpy(train_idx[s : s + 1024])
            loss = masked_loss(idx)
            opt.zero_grad()
            loss.backward()
            opt.step()
            total += loss.item() * len(idx)
        sched.step()
        model.eval()
        with torch.no_grad():
            vidx = torch.from_numpy(np.where(is_val)[0])
            vl = masked_loss(vidx).item()
        print(f"epoch {epoch + 1}: train {total / len(train_idx):.4f}  val {vl:.4f}")

    # temperature scaling on validation logits
    model.eval()
    with torch.no_grad():
        vidx = torch.from_numpy(np.where(is_val)[0])
        vlog = model(tx[vidx], ta[vidx])
        vmask = tm[vidx].unsqueeze(-1).expand_as(vlog) > 0
        vlog, vy = vlog[vmask], ty[vidx][vmask]
    temp = torch.ones(1, requires_grad=True)
    topt = torch.optim.LBFGS([temp], lr=0.1, max_iter=60)

    def closure():
        topt.zero_grad()
        l = nn.functional.binary_cross_entropy_with_logits(vlog / temp, vy)
        l.backward()
        return l

    topt.step(closure)
    temperature = temp.detach().clamp(0.5, 3.0)
    print(f"temperature {temperature.item():.3f}")

    # test metrics
    with torch.no_grad():
        idx = torch.from_numpy(np.where(is_test)[0])
        p = torch.sigmoid(model(tx[idx], ta[idx]) / temperature).numpy()
    ym, mm = y[is_test], m[is_test] > 0
    report: dict = {"samples": {"train": int(is_train.sum()), "val": int(is_val.sum()), "test": int(is_test.sum())}, "temperature": float(temperature)}
    args.report.mkdir(parents=True, exist_ok=True)
    fig, axes = plt.subplots(1, 2, figsize=(9, 4.2), dpi=150)
    for k, name in enumerate(["10 min", "20 min"]):
        pk, yk = p[..., k][mm], ym[..., k][mm]
        # simple heuristic to beat: radiant exposure feature plus temperature
        hk = (x[is_test][..., 9] * 0.6 + x[is_test][..., 0] * 2.5)[mm]
        rows, ece = calibration(pk, yk)
        report[name] = {
            "auc": auc(pk, yk),
            "heuristic_auc": auc(hk, yk),
            "brier": float(np.mean((pk - yk) ** 2)),
            "base_rate_brier": float(np.mean((yk.mean() - yk) ** 2)),
            "ece": ece,
            "positive_rate": float(yk.mean()),
            "reliability": rows,
        }
        ax = axes[k]
        ax.plot([0, 1], [0, 1], color="#9aa3ae", lw=1, ls="--")
        ax.plot([r[0] for r in rows], [r[1] for r in rows], marker="o", color="#3d7be0", lw=1.8)
        ax.set_title(f"Runaway within {name}  (AUC {report[name]['auc']:.3f}, ECE {ece:.3f})", fontsize=9)
        ax.set_xlabel("Predicted probability", fontsize=8)
        ax.set_ylabel("Observed frequency", fontsize=8)
        ax.tick_params(labelsize=7)
        ax.set_xlim(0, 1)
        ax.set_ylim(0, 1)
        ax.grid(alpha=0.25)
    fig.tight_layout()
    fig.savefig(args.report / "reliability.png")
    (args.report / "metrics.json").write_text(json.dumps(report, indent=2))
    for name in ("10 min", "20 min"):
        r = report[name]
        print(f"{name}: AUC {r['auc']:.3f} (heuristic {r['heuristic_auc']:.3f})  Brier {r['brier']:.4f} (base {r['base_rate_brier']:.4f})  ECE {r['ece']:.4f}")

    # export
    args.out.parent.mkdir(parents=True, exist_ok=True)
    exp = Exported(model, temperature).eval()
    dx = torch.zeros(1, meta["nodes"], meta["f"])
    da = torch.zeros(1, meta["nodes"], meta["nodes"])
    torch.onnx.export(exp, (dx, da), str(args.out), input_names=["x", "adj"], output_names=["p"], opset_version=15, dynamo=False)
    sess = ort.InferenceSession(str(args.out))
    i0 = np.where(is_test)[0][:64]
    for i in i0:
        ref = exp(tx[i : i + 1], ta[i : i + 1]).detach().numpy()
        got = sess.run(None, {"x": x[i : i + 1], "adj": a[i : i + 1]})[0]
        assert np.allclose(ref, got, atol=1e-5), "onnx parity failed"
    print(f"exported {args.out} (parity ok on 64 test graphs)")


if __name__ == "__main__":
    main()
