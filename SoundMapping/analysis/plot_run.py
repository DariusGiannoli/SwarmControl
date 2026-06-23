"""Render the standard set of plots from a SwarmTrajectoryRecorder JSON file.

Usage (from anywhere):
    python SoundMapping/analysis/plot_run.py                              # auto-pick the most recent *_traj.json in Assets/Trajectories
    python SoundMapping/analysis/plot_run.py path/to/run_traj.json        # specific file
    python SoundMapping/analysis/plot_run.py path/to/run.json --stride 20 # decimate the trajectory plot (faster on long runs)
    python SoundMapping/analysis/plot_run.py path/to/run.json --show      # also show interactively
    python SoundMapping/analysis/plot_run.py path/to/run.json -o outdir/  # custom output directory

What gets produced (PNGs in <outdir>):
    <stem>_trajectory.png    - top-down (XZ) drone paths + obstacles + crash markers
    <stem>_trajectory_3d.png - 3D drone paths + obstacles/course lines + crashes
    <stem>_swarm_health.png  - subnetworks/main/disconnected/cumulative crashes over time
    <stem>_crashes.png       - crash timeline (when, which drone, embodied flag)
    <stem>_inputs.png        - fused movement/spread/rotation + per-source rotation breakdown
    <stem>_swarm_center_velocity.png - actual swarm-center speed and XYZ velocity over time
    <stem>_gaps.png          - subnetwork centroids during gaps (color = time, size scales with drones)
    <stem>_collectibles_by_gap.png - collected stars per generated gap
    <stem>_gap_center_deviation.png - swarm-center deviation from each gap center at plane crossing
    <stem>_summary.png       - text card with headline stats

Run with the SwarmControl uv env:
    uv run python SoundMapping/analysis/plot_run.py
"""

from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path
import re

try:
    import tkinter as tk
    from tkinter import filedialog
except Exception:
    tk = None
    filedialog = None

import matplotlib.patches as mpatches
import matplotlib.pyplot as plt
import numpy as np
from matplotlib.widgets import Slider
from mpl_toolkits.mplot3d.art3d import Poly3DCollection  # noqa: F401 -- registers 3d projection

SCRIPT_DIR = Path(__file__).resolve().parent
UI_STATE_CACHE = SCRIPT_DIR / "plot_run_ui_state.json"


# --------------------------------------------------------------------------- #
# Loading
# --------------------------------------------------------------------------- #

def load(path: str | Path) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def _t0(log: dict) -> float:
    """Reference time = first sample we recorded. Used to make every plot's
    time axis run-relative rather than relative to Time.time at scene load."""
    sf = log.get("swarmFrames") or []
    if sf:
        return sf[0]["t"]
    for d in log.get("trajectories", []):
        if d.get("frames"):
            return d["frames"][0]["t"]
    return 0.0


def autodetect_json(start: Path) -> Path | None:
    """Find the most recent *_traj.json under Assets/Trajectories, walking up
    from `start` until we hit a SwarmControl-like root."""
    cur = start.resolve()
    for parent in (cur, *cur.parents):
        candidate_root = parent / "SoundMapping" / "SoundMappingUnity" / "Assets" / "Trajectories"
        if candidate_root.exists():
            files = sorted(candidate_root.rglob("*_traj.json"),
                           key=lambda p: p.stat().st_mtime, reverse=True)
            if files:
                return files[0]
        # Also accept being inside the Trajectories dir directly.
        if parent.name == "Trajectories":
            files = sorted(parent.rglob("*_traj.json"),
                           key=lambda p: p.stat().st_mtime, reverse=True)
            if files:
                return files[0]
    return None


def autodetect_trajectory_dir(start: Path) -> Path | None:
    """Find Assets/Trajectories, walking up from `start`."""
    cur = start.resolve()
    for parent in (cur, *cur.parents):
        candidate_root = parent / "SoundMapping" / "SoundMappingUnity" / "Assets" / "Trajectories"
        if candidate_root.exists():
            return candidate_root
        if parent.name == "Trajectories":
            return parent
    return None


def load_ui_state() -> dict:
    if not UI_STATE_CACHE.exists():
        return {}
    try:
        data = json.loads(UI_STATE_CACHE.read_text(encoding="utf-8"))
    except Exception:
        return {}
    return data if isinstance(data, dict) else {}


def save_ui_state(state: dict) -> None:
    try:
        UI_STATE_CACHE.write_text(json.dumps(state, indent=2), encoding="utf-8")
    except Exception:
        pass


def cached_dir(key: str, fallback: Path | None) -> Path:
    state = load_ui_state()
    value = state.get(key)
    if value:
        path = Path(value).expanduser()
        if path.exists() and path.is_dir():
            return path
    if fallback is not None and fallback.exists():
        return fallback
    return Path.cwd()


def remember_dir(key: str, path: str | Path) -> None:
    p = Path(path).expanduser()
    directory = p if p.is_dir() else p.parent
    if not directory.exists():
        return
    state = load_ui_state()
    state[key] = str(directory.absolute())
    save_ui_state(state)


def ask_for_trajectory_json(initialdir: Path | None) -> Path | None:
    """Open a JSON file picker, falling back to terminal input if Tk is unavailable."""
    start_dir = cached_dir("trajectory_dir", initialdir)
    if tk is not None and filedialog is not None:
        try:
            root = tk.Tk()
            root.withdraw()
            root.update_idletasks()
            root.lift()
            try:
                root.attributes("-topmost", True)
            except Exception:
                pass
            path = filedialog.askopenfilename(
                parent=root,
                title="Select trajectory JSON",
                initialdir=str(start_dir),
                filetypes=[("Trajectory JSON", "*.json"), ("All files", "*.*")],
            )
            try:
                root.attributes("-topmost", False)
            except Exception:
                pass
            root.destroy()
            if path:
                remember_dir("trajectory_dir", path)
            return Path(path) if path else None
        except Exception:
            pass

    resp = input("Enter trajectory JSON path (blank to cancel): ").strip()
    if not resp:
        return None
    path = Path(resp)
    remember_dir("trajectory_dir", path)
    return path


def autodetect_course_json(start: Path) -> Path | None:
    """Find the exported obstacle-course JSON that contains gap centers."""
    cur = start.resolve()
    for parent in (cur, *cur.parents):
        candidates = [
            parent / "Data" / "default" / "ObstacleCourse" / "TestCourse.json",
            parent / "SoundMapping" / "SoundMappingUnity" / "Assets" / "Data" / "default" / "ObstacleCourse" / "TestCourse.json",
        ]
        for candidate in candidates:
            if candidate.exists():
                return candidate
    return None


def load_course_gaps(path: str | Path | None) -> list[dict]:
    if path is None:
        return []
    data = load(path)
    return sorted(data.get("gaps", []), key=lambda g: int(g.get("index", 0)))


def _box_yaw(o: dict) -> float:
    return _yaw_from_quat(o.get("qw", 1.0), o.get("qx", 0.0),
                          o.get("qy", 0.0), o.get("qz", 0.0))


def derive_gaps_from_obstacles(obstacles: list[dict]) -> list[dict]:
    """Reconstruct gap planes/centers from wall boxes saved in the run file."""
    groups: dict[tuple[str, float], list[dict]] = {}
    for o in obstacles or []:
        if o.get("type") != "Box":
            continue
        if float(o.get("sy", 0.0)) < 1.0:
            continue
        yaw = _box_yaw(o)
        if abs(np.cos(yaw)) >= abs(np.sin(yaw)):
            key = ("z", round(float(o["cz"]), 2))
        else:
            key = ("x", round(float(o["cx"]), 2))
        groups.setdefault(key, []).append(o)

    gaps = []
    for (axis, coord), walls in groups.items():
        if len(walls) < 3:
            continue
        cx = float(np.mean([w["cx"] for w in walls]))
        cy = float(np.mean([w["cy"] for w in walls]))
        cz = float(np.mean([w["cz"] for w in walls]))
        if axis == "z":
            normal = [0.0, 0.0, 1.0]
            tangent = [1.0, 0.0, 0.0]
        else:
            normal = [1.0, 0.0, 0.0]
            tangent = [0.0, 0.0, 1.0]
        gaps.append({
            "index": -1,
            "centerX": cx,
            "centerY": cy,
            "centerZ": cz,
            "rotX": 0.0,
            "rotY": 90.0 if axis == "x" else 0.0,
            "rotZ": 0.0,
            "_source": "obstacles",
            "_axis": axis,
            "_normal": normal,
            "_tangent": tangent,
        })

    # Index by the order in which the swarm center reaches each reconstructed
    # plane later in `compute_gap_center_deviations`; this static fallback keeps
    # labels deterministic even before crossing times are known.
    gaps.sort(key=lambda g: (float(g["centerZ"]), float(g["centerX"])))
    for i, gap in enumerate(gaps):
        gap["index"] = i
    return gaps


# --------------------------------------------------------------------------- #
# Obstacle shapes for the top-down (XZ) projection
# --------------------------------------------------------------------------- #

def _yaw_from_quat(qw: float, qx: float, qy: float, qz: float) -> float:
    """Yaw (rotation about Unity's Y axis) from a quaternion. Top-down XZ
    only cares about yaw — pitch/roll get dropped."""
    return float(np.arctan2(2.0 * (qw * qy + qx * qz),
                            1.0 - 2.0 * (qy * qy + qx * qx)))


def _obstacle_patches_xz(obstacles: list, **patch_kwargs) -> list:
    """Build matplotlib patches for a top-down XZ obstacle overlay.
    Spheres + cylinders -> circles; boxes -> rotated rectangles."""
    defaults = dict(facecolor="#888888", alpha=0.35, edgecolor="black", linewidth=0.8)
    defaults.update(patch_kwargs)
    out = []
    for o in obstacles or []:
        t = o.get("type", "")
        cx, cz = float(o["cx"]), float(o["cz"])
        if t in ("Sphere", "Cylinder"):
            r = float(o.get("radius", 0.5))
            out.append(mpatches.Circle((cx, cz), r, **defaults))
        elif t == "Box":
            sx = float(o.get("sx", 1.0))
            sz = float(o.get("sz", 1.0))
            yaw = _yaw_from_quat(o.get("qw", 1.0), o.get("qx", 0.0),
                                 o.get("qy", 0.0), o.get("qz", 0.0))
            half = np.array([[-sx / 2, -sz / 2],
                             [ sx / 2, -sz / 2],
                             [ sx / 2,  sz / 2],
                             [-sx / 2,  sz / 2]])
            c, s = np.cos(yaw), np.sin(yaw)
            R = np.array([[c, -s], [s, c]])
            corners = (R @ half.T).T + np.array([cx, cz])
            out.append(mpatches.Polygon(corners, **defaults))
        else:
            r = float(o.get("radius", 0.5))
            out.append(mpatches.Circle((cx, cz), r, linestyle=":", **defaults))
    return out


def _course_line_patches_xz(course_lines: list) -> list:
    """Build patches for Starting_line / Ending_line overlays in top-down XZ."""
    out = []
    for line in course_lines or []:
        cx, cz = float(line["cx"]), float(line["cz"])
        sx = float(line.get("sx", 1.0))
        sz = float(line.get("sz", 1.0))
        yaw = _yaw_from_quat(line.get("qw", 1.0), line.get("qx", 0.0),
                             line.get("qy", 0.0), line.get("qz", 0.0))
        half = np.array([[-sx / 2, -sz / 2],
                         [ sx / 2, -sz / 2],
                         [ sx / 2,  sz / 2],
                         [-sx / 2,  sz / 2]])
        c, s = np.cos(yaw), np.sin(yaw)
        R = np.array([[c, -s], [s, c]])
        corners = (R @ half.T).T + np.array([cx, cz])
        role = line.get("role", "")
        color = _course_marker_color(role)
        label = _course_marker_label(role)
        out.append(mpatches.Polygon(corners, facecolor=color, alpha=0.18,
                                    edgecolor=color, linewidth=1.8, label=label))
    return out


def _course_marker_color(role: str) -> str:
    if role == "start":
        return "tab:green"
    if role == "end":
        return "tab:red"
    if role == "startSquare":
        return "tab:cyan"
    return "tab:gray"


def _course_marker_label(role: str) -> str:
    if role == "start":
        return "starting line"
    if role == "end":
        return "ending line"
    if role == "startSquare":
        return "starting square"
    return "course marker"


def _match_unity_scene_top_view(ax) -> None:
    """Use the XZ projection seen from Unity +Y looking downward."""
    # Keep +Z upward on the plot. This is the direct XZ projection when viewing
    # the scene from positive Y toward the ground.
    return None


def _unity_to_plot3d(x, y, z):
    """Map Unity world coordinates to matplotlib display coordinates.

    Unity uses Y as the vertical axis; matplotlib's 3D vertical axis is its
    third coordinate. Display as X / Z / Y so the horizontal plane matches the
    2D top-down XZ plot and height is visually vertical.
    """
    return x, z, y


def _embodied_segments(mask: np.ndarray) -> list[tuple[int, int]]:
    """Return [start, end) spans where a per-frame embodied mask is true."""
    if mask.size == 0 or not np.any(mask):
        return []
    padded = np.concatenate(([False], mask.astype(bool), [False]))
    changes = np.flatnonzero(padded[1:] != padded[:-1])
    return [(int(changes[i]), int(changes[i + 1])) for i in range(0, len(changes), 2)]


def _mark_trial_window(log: dict, ax, t0: float) -> None:
    """Overlay Starting_line / Ending_line timing markers on a time-axis plot."""
    for trial in log.get("trials", []):
        ts = trial.get("startGameTime", 0) - t0
        ax.axvline(ts, color="black", linestyle=":", alpha=0.45)
        if trial.get("endGameTime", 0) > 0:
            te = trial["endGameTime"] - t0
            ax.axvline(te, color="black", linestyle=":", alpha=0.45)
            ax.axvspan(ts, te, alpha=0.04, color="black")


# --------------------------------------------------------------------------- #
# Plots
# --------------------------------------------------------------------------- #

def plot_trajectory_2d(log: dict, ax=None, stride: int = 1,
                       gap_deviations: list[dict] | None = None):
    """Top-down (XZ) drone paths + obstacles + crash markers.

    `stride` keeps every Nth frame in the path polylines. For long runs (e.g.
    8000+ frames per drone) stride=10–20 gives a near-identical plot at a
    fraction of the matplotlib draw cost. Stride is *only* applied to path
    rendering — start/end markers always use the true first/last frame so
    they don't shift when stride changes.
    """
    if ax is None:
        _, ax = plt.subplots(figsize=(10, 10))

    stride = max(1, int(stride))

    for p in _obstacle_patches_xz(log.get("obstacles", [])):
        ax.add_patch(p)
    for p in _course_line_patches_xz(log.get("courseLines", [])):
        ax.add_patch(p)

    trajs = log.get("trajectories", [])
    cmap = plt.get_cmap("tab20")
    embodied_label_used = False

    for i, drone in enumerate(trajs):
        frames = drone.get("frames", [])
        if not frames:
            continue
        sampled = frames[::stride]
        # Make sure the last point is included even if stride doesn't hit it,
        # otherwise the visible path ends slightly short on long runs.
        if sampled and sampled[-1] is not frames[-1]:
            sampled = list(sampled) + [frames[-1]]
        xs = np.fromiter((f["x"] for f in sampled), dtype=float, count=len(sampled))
        zs = np.fromiter((f["z"] for f in sampled), dtype=float, count=len(sampled))
        emb = np.fromiter((f.get("e", 0) for f in sampled), dtype=int, count=len(sampled)) > 0

        ax.plot(xs, zs, color=cmap(i % 20), lw=0.7, alpha=0.45)
        for start, end in _embodied_segments(emb):
            if end - start < 2:
                continue
            label = "embodied segment" if not embodied_label_used else None
            ax.plot(xs[start:end], zs[start:end], color="red", lw=2.2,
                    alpha=0.95, zorder=5, label=label)
            embodied_label_used = True

    crashes = log.get("crashes", [])
    if crashes:
        ax.scatter([c["x"] for c in crashes], [c["z"] for c in crashes],
                   marker="x", s=90, color="black", linewidths=2.2, zorder=6,
                   label=f"crashes ({len(crashes)})")

    if gap_deviations:
        ax.scatter([d["x"] for d in gap_deviations], [d["z"] for d in gap_deviations],
                   marker="D", s=58, facecolor="white",
                   edgecolor="tab:orange", linewidths=1.8, zorder=7,
                   label="swarm-center gap crossings")
        for d in gap_deviations:
            ax.annotate(str(d["index"]), (d["x"], d["z"]),
                        xytext=(4, 4), textcoords="offset points",
                        fontsize=8, color="tab:orange", zorder=8)

    ax.set_aspect("equal", adjustable="datalim")
    ax.set_xlabel("X (m)"); ax.set_ylabel("Z (m)")
    _match_unity_scene_top_view(ax)
    title_extra = f"  (stride={stride})" if stride > 1 else ""
    ax.set_title(f"Trajectory + obstacles + crashes — "
                 f"PID {log.get('pid','?')} ({log.get('haptics','?')}/{log.get('order','?')})"
                 f"{title_extra}")
    ax.legend(loc="best", fontsize=8, framealpha=0.85)
    ax.grid(True, alpha=0.2)
    return ax


# --------------------------------------------------------------------------- #
# 3D plot helpers
# --------------------------------------------------------------------------- #

def _quat_to_matrix(qw: float, qx: float, qy: float, qz: float) -> np.ndarray:
    """Quaternion -> 3x3 rotation matrix. Unity uses left-handed coords but
    matplotlib's 3D axes are right-handed; we just plot positions as-is, so
    rotations work the same since the recorded quaternion already encodes
    Unity's world orientation."""
    n = max(1e-12, qw * qw + qx * qx + qy * qy + qz * qz)
    qw, qx, qy, qz = qw / np.sqrt(n), qx / np.sqrt(n), qy / np.sqrt(n), qz / np.sqrt(n)
    return np.array([
        [1 - 2 * (qy * qy + qz * qz),     2 * (qx * qy - qz * qw),     2 * (qx * qz + qy * qw)],
        [    2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz),     2 * (qy * qz - qx * qw)],
        [    2 * (qx * qz - qy * qw),     2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy)],
    ])


def _add_obstacle_3d(ax, o: dict, color: str = "#888888", alpha: float = 0.18) -> None:
    """Render one obstacle in 3D. Spheres + cylinders as wireframes (cheap to
    draw, doesn't occlude trajectories); boxes as semi-transparent faces."""
    t = o.get("type", "")
    cx, cy, cz = float(o["cx"]), float(o["cy"]), float(o["cz"])
    if t == "Sphere":
        r = float(o.get("radius", 0.5))
        u = np.linspace(0, 2 * np.pi, 18)
        v = np.linspace(0, np.pi, 10)
        x = cx + r * np.outer(np.cos(u), np.sin(v))
        z = cz + r * np.outer(np.sin(u), np.sin(v))
        y = cy + r * np.outer(np.ones_like(u), np.cos(v))
        px, py, pz = _unity_to_plot3d(x, y, z)
        ax.plot_wireframe(px, py, pz, color=color, alpha=alpha, linewidth=0.5)
    elif t == "Cylinder":
        r = float(o.get("radius", 0.5))
        h = float(o.get("height", 1.0))
        R = _quat_to_matrix(o.get("qw", 1.0), o.get("qx", 0.0), o.get("qy", 0.0), o.get("qz", 0.0))
        theta = np.linspace(0, 2 * np.pi, 24)
        ys = np.array([-h / 2, h / 2])
        # Local-space ring at each end.
        circle = np.stack([r * np.cos(theta), np.zeros_like(theta), r * np.sin(theta)], axis=1)
        for y_local in ys:
            ring = circle.copy()
            ring[:, 1] = y_local
            world = (R @ ring.T).T + np.array([cx, cy, cz])
            px, py, pz = _unity_to_plot3d(world[:, 0], world[:, 1], world[:, 2])
            ax.plot(px, py, pz,
                    color=color, alpha=alpha, linewidth=0.7)
        # Vertical struts at a few angles so the wireframe reads as a tube.
        for k in range(0, len(theta), 4):
            seg = np.stack([
                [r * np.cos(theta[k]), -h / 2, r * np.sin(theta[k])],
                [r * np.cos(theta[k]),  h / 2, r * np.sin(theta[k])],
            ])
            world = (R @ seg.T).T + np.array([cx, cy, cz])
            px, py, pz = _unity_to_plot3d(world[:, 0], world[:, 1], world[:, 2])
            ax.plot(px, py, pz,
                    color=color, alpha=alpha, linewidth=0.5)
    elif t == "Box":
        sx, sy, sz = float(o.get("sx", 1)), float(o.get("sy", 1)), float(o.get("sz", 1))
        R = _quat_to_matrix(o.get("qw", 1.0), o.get("qx", 0.0), o.get("qy", 0.0), o.get("qz", 0.0))
        h = np.array([sx / 2, sy / 2, sz / 2])
        # 8 corners in local space.
        corners_local = np.array([
            [-h[0], -h[1], -h[2]], [ h[0], -h[1], -h[2]],
            [ h[0],  h[1], -h[2]], [-h[0],  h[1], -h[2]],
            [-h[0], -h[1],  h[2]], [ h[0], -h[1],  h[2]],
            [ h[0],  h[1],  h[2]], [-h[0],  h[1],  h[2]],
        ])
        corners = (R @ corners_local.T).T + np.array([cx, cy, cz])
        px, py, pz = _unity_to_plot3d(corners[:, 0], corners[:, 1], corners[:, 2])
        corners = np.column_stack([px, py, pz])
        faces = [
            [corners[i] for i in (0, 1, 2, 3)],  # bottom
            [corners[i] for i in (4, 5, 6, 7)],  # top
            [corners[i] for i in (0, 1, 5, 4)],
            [corners[i] for i in (1, 2, 6, 5)],
            [corners[i] for i in (2, 3, 7, 6)],
            [corners[i] for i in (3, 0, 4, 7)],
        ]
        coll = Poly3DCollection(faces, facecolor=color, alpha=alpha,
                                edgecolor="black", linewidth=0.4)
        ax.add_collection3d(coll)


def plot_trajectory_3d(log: dict, ax=None, stride: int = 1, interactive: bool = False):
    """3D trajectory plot with obstacles as wireframes (spheres/cylinders) or
    translucent faces (boxes). Stride decimates the per-drone polyline, same
    as the 2D plot."""
    if ax is None:
        fig = plt.figure(figsize=(11, 9))
        ax = fig.add_subplot(111, projection="3d")

    stride = max(1, int(stride))

    for obs in log.get("obstacles", []):
        _add_obstacle_3d(ax, obs)
    for line in log.get("courseLines", []):
        color = _course_marker_color(line.get("role", ""))
        _add_obstacle_3d(ax, {"type": "Box", **line}, color=color, alpha=0.24)

    trajs = log.get("trajectories", [])
    cmap = plt.get_cmap("tab20")

    t0 = _t0(log)
    all_pts = []  # accumulate for axis-limit computation
    animated_trajs = []
    embodied_label_used = False
    for i, drone in enumerate(trajs):
        frames = drone.get("frames", [])
        if not frames:
            continue
        sampled = frames[::stride]
        if sampled and sampled[-1] is not frames[-1]:
            sampled = list(sampled) + [frames[-1]]
        xs = np.fromiter((f["x"] for f in sampled), dtype=float, count=len(sampled))
        ys = np.fromiter((f["y"] for f in sampled), dtype=float, count=len(sampled))
        zs = np.fromiter((f["z"] for f in sampled), dtype=float, count=len(sampled))
        ts = np.fromiter((f["t"] - t0 for f in sampled), dtype=float, count=len(sampled))
        emb = np.fromiter((f.get("e", 0) for f in sampled), dtype=int, count=len(sampled)) > 0
        px, py, pz = _unity_to_plot3d(xs, ys, zs)
        all_pts.append((px, py, pz))

        base_color = cmap(i % 20)
        line, = ax.plot(px, py, pz, color=base_color, lw=0.7, alpha=0.45)
        for start, end in _embodied_segments(emb):
            if end - start < 2:
                continue
            label = "embodied segment" if not embodied_label_used else None
            ax.plot(px[start:end], py[start:end], pz[start:end], color="red",
                    lw=2.2, alpha=0.95, label=label)
            embodied_label_used = True

        head = None
        if interactive:
            head_color = "red" if emb[-1] else base_color
            head_size = 28 if emb[-1] else 14
            head = ax.scatter([px[-1]], [py[-1]], [pz[-1]], s=head_size,
                              color=head_color, alpha=0.95, depthshade=False)
            animated_trajs.append({
                "t": ts,
                "x": px,
                "y": py,
                "z": pz,
                "embodied": emb,
                "base_color": base_color,
                "line": line,
                "head": head,
            })

    crashes = log.get("crashes", [])
    if crashes:
        cx, cy, cz = _unity_to_plot3d([c["x"] for c in crashes],
                                      [c["y"] for c in crashes],
                                      [c["z"] for c in crashes])
        ax.scatter(cx, cy, cz,
                   marker="x", s=70, color="black", linewidths=2.0,
                   label=f"crashes ({len(crashes)})")

    # Equal aspect across X/Y/Z (matplotlib doesn't give true equal automatically
    # for 3D; we pad each axis to the largest range so cubes look like cubes).
    if all_pts:
        xs = np.concatenate([p[0] for p in all_pts])
        ys = np.concatenate([p[1] for p in all_pts])
        zs = np.concatenate([p[2] for p in all_pts])
        # Include obstacle centers in the bounding box so they're never cropped.
        for o in log.get("obstacles", []):
            px, py, pz = _unity_to_plot3d(float(o["cx"]), float(o["cy"]), float(o["cz"]))
            xs = np.append(xs, px)
            ys = np.append(ys, py)
            zs = np.append(zs, pz)
        for line in log.get("courseLines", []):
            px, py, pz = _unity_to_plot3d(float(line["cx"]), float(line["cy"]), float(line["cz"]))
            xs = np.append(xs, px)
            ys = np.append(ys, py)
            zs = np.append(zs, pz)
        mid = np.array([(xs.max() + xs.min()) / 2,
                        (ys.max() + ys.min()) / 2,
                        (zs.max() + zs.min()) / 2])
        half = max(xs.max() - xs.min(),
                   ys.max() - ys.min(),
                   zs.max() - zs.min()) / 2 * 1.05
        half = max(half, 1.0)
        ax.set_xlim(mid[0] - half, mid[0] + half)
        ax.set_ylim(mid[1] - half, mid[1] + half)
        ax.set_zlim(mid[2] - half, mid[2] + half)
        try:
            ax.set_box_aspect([1, 1, 1])
        except AttributeError:
            pass  # older matplotlib

    ax.set_xlabel("X (m)"); ax.set_ylabel("Z (m)"); ax.set_zlabel("Y (m)")
    title_extra = f"  (stride={stride})" if stride > 1 else ""
    ax.set_title(f"3D trajectory + obstacles + crashes — "
                 f"PID {log.get('pid','?')} ({log.get('haptics','?')}/{log.get('order','?')})"
                 f"{title_extra}")
    ax.legend(loc="best", fontsize=8)
    if interactive:
        _attach_trajectory_time_slider(ax, animated_trajs)
    return ax


def _attach_trajectory_time_slider(ax, animated_trajs: list[dict]) -> None:
    """Attach a time slider to a 3D trajectory axis."""
    if not animated_trajs:
        return

    t_min = min(float(tr["t"][0]) for tr in animated_trajs if len(tr["t"]))
    t_max = max(float(tr["t"][-1]) for tr in animated_trajs if len(tr["t"]))
    if t_max <= t_min:
        return

    fig = ax.figure
    fig.subplots_adjust(bottom=0.16)
    slider_ax = fig.add_axes([0.18, 0.045, 0.64, 0.03])
    slider = Slider(slider_ax, "time (s)", t_min, t_max, valinit=t_max, valfmt="%.1f")

    def update(t_now: float) -> None:
        for tr in animated_trajs:
            ts = tr["t"]
            idx = int(np.searchsorted(ts, t_now, side="right"))
            if idx <= 0:
                tr["line"].set_data_3d([], [], [])
                tr["head"]._offsets3d = ([], [], [])
                continue

            tr["line"].set_data_3d(tr["x"][:idx], tr["y"][:idx], tr["z"][:idx])
            tr["head"]._offsets3d = ([tr["x"][idx - 1]], [tr["y"][idx - 1]], [tr["z"][idx - 1]])
            is_embodied = bool(tr["embodied"][idx - 1])
            tr["head"].set_color("red" if is_embodied else tr["base_color"])
            tr["head"].set_sizes([28 if is_embodied else 14])
        fig.canvas.draw_idle()

    slider.on_changed(update)
    fig._trajectory_time_slider = slider  # keep widget alive


def plot_swarm_health(log: dict, ax=None):
    """Counts over time: main group size, disconnected drones, subnetwork count,
    cumulative crashes. Trial windows are shaded."""
    if ax is None:
        _, ax = plt.subplots(figsize=(12, 5))

    sf = log.get("swarmFrames", [])
    if not sf:
        ax.text(0.5, 0.5, "no swarmFrames recorded",
                ha="center", va="center", transform=ax.transAxes)
        return ax

    t0 = _t0(log)
    t = np.array([f["t"] - t0 for f in sf])
    ax.plot(t, [f["nMain"]  for f in sf], label="main group size",            color="tab:green",  lw=1.6)
    ax.plot(t, [f["nDisc"]  for f in sf], label="disconnected drones",        color="tab:orange", lw=1.4)
    ax.plot(t, [f["nSub"]   for f in sf], label="subnetworks (gaps = nSub-1)", color="tab:blue",   lw=1.2)
    ax.plot(t, [f["nCrash"] for f in sf], label="cumulative crashes",          color="tab:red",    lw=1.2, linestyle="--")

    for trial in log.get("trials", []):
        ts = trial["startGameTime"] - t0
        ax.axvline(ts, color="black", linestyle=":", alpha=0.4)
        if trial.get("endGameTime", 0) > 0:
            te = trial["endGameTime"] - t0
            ax.axvline(te, color="black", linestyle=":", alpha=0.4)
            ax.axvspan(ts, te, alpha=0.04, color="black")

    ax.set_xlabel("time since start (s)"); ax.set_ylabel("count")
    ax.set_title("Swarm health over time")
    ax.legend(loc="best", framealpha=0.9); ax.grid(True, alpha=0.3)
    return ax


def plot_crash_timeline(log: dict, ax=None):
    """x = time, y = drone id, red = embodied at crash."""
    if ax is None:
        _, ax = plt.subplots(figsize=(10, 3.5))

    crashes = log.get("crashes", [])
    if not crashes:
        ax.text(0.5, 0.5, "no crashes recorded",
                ha="center", va="center", transform=ax.transAxes)
        return ax

    t0 = _t0(log)
    ts = [c["t"] - t0 for c in crashes]
    ids = [c["droneId"] for c in crashes]
    colors = ["red" if c.get("embodied", 0) else "black" for c in crashes]
    ax.scatter(ts, ids, c=colors, marker="x", s=90, linewidths=2.2)
    ax.set_xlabel("time since start (s)"); ax.set_ylabel("drone id")
    ax.set_title(f"Crash timeline ({len(crashes)} total; red = embodied at crash)")
    ax.grid(True, alpha=0.3)
    return ax


def plot_inputs(log: dict, axes=None):
    """4-panel: fused movement / spread / rotation / per-source rotation breakdown."""
    inputs = log.get("inputs", [])
    if not inputs:
        if axes is None:
            _, axes = plt.subplots(figsize=(12, 3))
        ax = axes if not hasattr(axes, "__len__") else axes[0]
        ax.text(0.5, 0.5, "no inputs recorded (logInputs disabled?)",
                ha="center", va="center", transform=ax.transAxes)
        return axes

    if axes is None:
        _, axes = plt.subplots(4, 1, figsize=(12, 10), sharex=True)

    t0 = _t0(log)
    t = np.array([f["t"] - t0 for f in inputs])

    axes[0].plot(t, [f["fmx"] for f in inputs], label="x",          color="tab:blue")
    axes[0].plot(t, [f["fmy"] for f in inputs], label="y (height)", color="tab:green")
    axes[0].plot(t, [f["fmz"] for f in inputs], label="z",          color="tab:red")
    axes[0].set_ylabel("SwarmMovement"); axes[0].legend(loc="best"); axes[0].grid(True, alpha=0.3)

    axes[1].plot(t, [f["fs"] for f in inputs], color="tab:purple")
    axes[1].set_ylabel("SwarmSpread"); axes[1].grid(True, alpha=0.3)

    axes[2].plot(t, [f["fr"] for f in inputs], color="tab:orange")
    axes[2].set_ylabel("CameraRotation"); axes[2].grid(True, alpha=0.3)

    imu_y  = np.array([f.get("imu_y",  np.nan) for f in inputs], dtype=float)
    trad_r = np.array([f.get("trad_r", np.nan) for f in inputs], dtype=float)
    mq_y   = np.array([f.get("mq_y",   np.nan) for f in inputs], dtype=float)
    pose_y = np.array([f.get("pose_y", np.nan) for f in inputs], dtype=float)
    plotted = False
    if np.any(~np.isnan(imu_y)):  axes[3].plot(t, imu_y,  label="IMU yaw rate",     color="tab:cyan");                 plotted = True
    if np.any(~np.isnan(trad_r)): axes[3].plot(t, trad_r, label="traditional R",    color="tab:gray",   lw=0.8);       plotted = True
    if np.any(~np.isnan(mq_y)):   axes[3].plot(t, mq_y,   label="MQ headset yaw",   color="tab:olive",  lw=0.8);       plotted = True
    if np.any(~np.isnan(pose_y)): axes[3].plot(t, pose_y, label="webcam pose yaw",  color="tab:pink",   lw=0.8);       plotted = True
    if plotted:
        axes[3].legend(loc="best", fontsize=8)
    for ax in axes:
        _mark_trial_window(log, ax, t0)
    axes[3].set_ylabel("rotation sources")
    axes[3].set_xlabel("time since start (s)")
    axes[3].grid(True, alpha=0.3)
    return axes


def plot_gaps(log: dict, ax=None):
    """Subnetwork centroids overlaid on obstacles, only for split frames.
    Marker size scales with drone count, color encodes time."""
    if ax is None:
        _, ax = plt.subplots(figsize=(10, 10))

    for p in _obstacle_patches_xz(log.get("obstacles", []),
                                  facecolor="#888888", alpha=0.12):
        ax.add_patch(p)

    snaps = log.get("subnetworks", [])
    if not snaps:
        ax.text(0.5, 0.5, "no subnetwork data",
                ha="center", va="center", transform=ax.transAxes)
        ax.set_aspect("equal", adjustable="datalim")
        _match_unity_scene_top_view(ax)
        return ax

    by_t: dict[float, list] = {}
    for s in snaps:
        by_t.setdefault(s["t"], []).append(s)
    split_t = sorted(t for t, lst in by_t.items() if len(lst) > 1)
    if not split_t:
        ax.text(0.5, 0.5, "swarm never split during this run",
                ha="center", va="center", transform=ax.transAxes)
        ax.set_aspect("equal", adjustable="datalim")
        _match_unity_scene_top_view(ax)
        return ax

    t0 = _t0(log)
    t_lo = split_t[0] - t0
    t_hi = split_t[-1] - t0
    span = max(1e-6, t_hi - t_lo)
    cmap = plt.get_cmap("plasma")
    for t in split_t:
        col = cmap((t - t0 - t_lo) / span)
        for s in by_t[t]:
            ax.scatter(s["cx"], s["cz"],
                       s=max(15.0, float(s["size"]) * 25.0),
                       facecolor=col, alpha=0.55,
                       edgecolor="black", linewidth=0.4)
    sm = plt.cm.ScalarMappable(cmap=cmap, norm=plt.Normalize(vmin=t_lo, vmax=t_hi))
    sm.set_array([])
    plt.colorbar(sm, ax=ax, label="time since start (s)")
    ax.set_aspect("equal", adjustable="datalim")
    ax.set_xlabel("X (m)"); ax.set_ylabel("Z (m)")
    _match_unity_scene_top_view(ax)
    ax.set_title(f"Subnetwork centroids during gaps "
                 f"(n={len(split_t)} split samples; size scales with drones)")
    return ax


_STAR_GAP_RE = re.compile(r"^Star_(?P<gap>.+)_(?P<row>\d+)_(?P<col>\d+)$")
_GAP_INDEX_RE = re.compile(r"\((?P<idx>\d+)\)")


def _gap_name_from_collectible(name: str) -> str:
    """Extract the gap name from generated star names."""
    m = _STAR_GAP_RE.match(name or "")
    if not m:
        return "unknown"
    return m.group("gap")


def _gap_sort_key(name: str) -> tuple[int, int | str]:
    m = _GAP_INDEX_RE.search(name)
    if m:
        return (0, int(m.group("idx")))
    if name == "unknown":
        return (2, name)
    return (1, name)


def _swarm_center_series(log: dict) -> tuple[np.ndarray, np.ndarray]:
    """Return times and Unity XYZ swarm-center samples.

    Prefer `subnetworks[idx=0]`, which the recorder defines as the largest
    connected component / main group. Older or partial files fall back to the
    centroid across all per-drone samples at each timestamp.
    """
    main_rows = [s for s in log.get("subnetworks", []) if int(s.get("idx", 0)) == 0]
    if main_rows:
        main_rows.sort(key=lambda s: float(s["t"]))
        times = np.array([float(s["t"]) for s in main_rows], dtype=float)
        points = np.array([[float(s["cx"]), float(s["cy"]), float(s["cz"])] for s in main_rows], dtype=float)
        return times, points

    by_t: dict[float, list[tuple[float, float, float]]] = {}
    for drone in log.get("trajectories", []):
        for frame in drone.get("frames", []):
            by_t.setdefault(float(frame["t"]), []).append(
                (float(frame["x"]), float(frame["y"]), float(frame["z"]))
            )
    if not by_t:
        return np.array([], dtype=float), np.empty((0, 3), dtype=float)

    times = np.array(sorted(by_t), dtype=float)
    points = np.array([np.mean(by_t[t], axis=0) for t in times], dtype=float)
    return times, points


def _swarm_center_velocity_series(log: dict) -> tuple[np.ndarray, np.ndarray]:
    """Return relative midpoint times and actual swarm-center velocity samples."""
    times, points = _swarm_center_series(log)
    if len(times) < 2:
        return np.array([], dtype=float), np.empty((0, 3), dtype=float)

    order = np.argsort(times)
    times = times[order]
    points = points[order]

    dt = np.diff(times)
    valid = np.isfinite(dt) & (dt > 1e-9)
    if not np.any(valid):
        return np.array([], dtype=float), np.empty((0, 3), dtype=float)

    velocity = np.diff(points, axis=0)[valid] / dt[valid, None]
    t_mid = 0.5 * (times[:-1][valid] + times[1:][valid]) - float(times[0])
    finite = np.isfinite(t_mid) & np.all(np.isfinite(velocity), axis=1)
    return t_mid[finite], velocity[finite]


def plot_swarm_center_velocity(log: dict, axes=None):
    """Actual swarm-center velocity derived from position samples."""
    if axes is None:
        _, axes = plt.subplots(2, 1, figsize=(12, 7), sharex=True)

    t, velocity = _swarm_center_velocity_series(log)
    if len(t) == 0:
        ax = axes[0] if hasattr(axes, "__len__") else axes
        ax.text(0.5, 0.5, "not enough swarm-center samples for velocity",
                ha="center", va="center", transform=ax.transAxes)
        return axes

    speed_3d = np.linalg.norm(velocity, axis=1)
    speed_xz = np.linalg.norm(velocity[:, [0, 2]], axis=1)
    mean_speed = float(np.mean(speed_3d)) if len(speed_3d) else float("nan")

    axes[0].plot(t, speed_3d, color="black", linewidth=1.3, label="3D speed")
    axes[0].plot(t, speed_xz, color="tab:purple", linewidth=1.0, alpha=0.85, label="XZ speed")
    if np.isfinite(mean_speed):
        axes[0].axhline(mean_speed, color="tab:red", linestyle="--", linewidth=1.0,
                        label=f"mean 3D = {mean_speed:.2f} m/s")
    axes[0].set_ylabel("speed (m/s)")
    axes[0].set_title("Actual swarm-center velocity")
    axes[0].grid(True, alpha=0.3)
    axes[0].legend(loc="best")

    axes[1].plot(t, velocity[:, 0], label="vx", color="tab:blue", linewidth=1.0)
    axes[1].plot(t, velocity[:, 1], label="vy (height)", color="tab:green", linewidth=1.0)
    axes[1].plot(t, velocity[:, 2], label="vz", color="tab:red", linewidth=1.0)
    axes[1].axhline(0.0, color="black", linewidth=0.7, alpha=0.45)
    axes[1].set_xlabel("time since first swarm-center sample (s)")
    axes[1].set_ylabel("velocity (m/s)")
    axes[1].grid(True, alpha=0.3)
    axes[1].legend(loc="best")
    return axes


def _gap_rotation_matrix(gap: dict) -> np.ndarray:
    """Approximate gap local-to-world rotation matrix from exported Euler angles."""
    rx = np.deg2rad(float(gap.get("rotX", 0.0)))
    ry = np.deg2rad(float(gap.get("rotY", 0.0)))
    rz = np.deg2rad(float(gap.get("rotZ", 0.0)))
    cx, sx = np.cos(rx), np.sin(rx)
    cy, sy = np.cos(ry), np.sin(ry)
    cz, sz = np.cos(rz), np.sin(rz)
    rx_mat = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]], dtype=float)
    ry_mat = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]], dtype=float)
    rz_mat = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]], dtype=float)
    return rz_mat @ ry_mat @ rx_mat


def _gap_plane_normal(gap: dict) -> np.ndarray:
    """Gap plane normal in Unity XYZ."""
    normal = _gap_rotation_matrix(gap) @ np.array([0.0, 0.0, 1.0])
    norm = float(np.linalg.norm(normal))
    return normal / norm if norm > 1e-12 else np.array([0.0, 0.0, 1.0])


def _gap_plane_axes(gap: dict) -> tuple[np.ndarray, np.ndarray]:
    """Return gap-plane normal and in-plane horizontal axis in Unity XYZ.

    Prefer wall-center geometry over the gap transform rotation so the analysis
    plane matches the wall line shown in the 2D trajectory plot.
    """
    if "_normal" in gap and "_tangent" in gap:
        normal = np.array(gap["_normal"], dtype=float)
        tangent = np.array(gap["_tangent"], dtype=float)
        normal = normal / max(1e-12, float(np.linalg.norm(normal)))
        tangent = tangent / max(1e-12, float(np.linalg.norm(tangent)))
        return normal, tangent

    wall_points = []
    for wall_name in ("left", "right", "top", "bottom"):
        wall = gap.get(wall_name)
        if wall:
            wall_points.append([float(wall["x"]), float(wall["z"])])

    if len(wall_points) >= 2:
        xz = np.array(wall_points, dtype=float)
        centered = xz - np.mean(xz, axis=0)
        _, _, vh = np.linalg.svd(centered, full_matrices=False)
        line_xz = vh[0]
        line_norm = float(np.linalg.norm(line_xz))
        if line_norm > 1e-12:
            line_xz = line_xz / line_norm
            tangent = np.array([line_xz[0], 0.0, line_xz[1]], dtype=float)
            normal = np.array([-line_xz[1], 0.0, line_xz[0]], dtype=float)
            return normal, tangent

    rotation = _gap_rotation_matrix(gap)
    normal = rotation @ np.array([0.0, 0.0, 1.0])
    normal = normal / max(1e-12, float(np.linalg.norm(normal)))
    tangent = rotation @ np.array([1.0, 0.0, 0.0])
    tangent = tangent / max(1e-12, float(np.linalg.norm(tangent)))
    return normal, tangent


def compute_gap_center_deviations(log: dict, gaps: list[dict]) -> list[dict]:
    """Deviation from gap center at each ordered swarm-center plane crossing."""
    times, points = _swarm_center_series(log)
    if len(times) < 2 or not gaps:
        return []

    results = []
    after_t = -np.inf
    for gap in gaps:
        center = np.array([
            float(gap["centerX"]),
            float(gap["centerY"]),
            float(gap["centerZ"]),
        ], dtype=float)
        normal, tangent = _gap_plane_axes(gap)
        signed = (points - center) @ normal

        hits = []
        exact = np.flatnonzero(np.isclose(signed, 0.0, atol=1e-6))
        for i in exact:
            hits.append((float(times[int(i)]), points[int(i)]))

        crossings = np.flatnonzero(signed[:-1] * signed[1:] < 0.0)
        for i_raw in crossings:
            i = int(i_raw)
            alpha = signed[i] / (signed[i] - signed[i + 1])
            point = points[i] + alpha * (points[i + 1] - points[i])
            t = times[i] + alpha * (times[i + 1] - times[i])
            hits.append((float(t), point))

        if not hits:
            continue

        hits.sort(key=lambda h: h[0])
        ordered_hits = [h for h in hits if h[0] > after_t]
        if not ordered_hits:
            continue

        t_cross, point_cross = ordered_hits[0]
        after_t = t_cross
        delta = point_cross - center
        in_plane_horizontal = float(np.dot(delta, tangent))
        results.append({
            "index": int(gap.get("index", len(results))),
            "t": float(t_cross),
            "x": float(point_cross[0]),
            "y": float(point_cross[1]),
            "z": float(point_cross[2]),
            "deviation": float(np.hypot(in_plane_horizontal, delta[1])),
            "dx": float(delta[0]),
            "dy": float(delta[1]),
            "dz": float(delta[2]),
        })
    return results


def plot_gap_center_deviation(log: dict, gaps: list[dict], ax=None):
    """Bar chart: swarm-center distance from each gap center at plane crossing."""
    if ax is None:
        _, ax = plt.subplots(figsize=(11, 4.5))

    if not gaps:
        ax.text(0.5, 0.5, "no course gap centers found\n(expected Assets/Data/default/ObstacleCourse/TestCourse.json)",
                ha="center", va="center", transform=ax.transAxes)
        ax.axis("off")
        return ax

    deviations = compute_gap_center_deviations(log, gaps)
    if not deviations:
        ax.text(0.5, 0.5, "no swarm-center crossings found",
                ha="center", va="center", transform=ax.transAxes)
        ax.axis("off")
        return ax

    labels = [f"gap {d['index']}" for d in deviations]
    values = [d["deviation"] for d in deviations]
    xs = np.arange(len(deviations))
    mean_dev = float(np.mean(values))

    ax.bar(xs, values, color="tab:orange", alpha=0.82)
    ax.axhline(mean_dev, color="black", linestyle="--", linewidth=1.2,
               label=f"mean = {mean_dev:.2f} m")
    ax.set_xticks(xs)
    ax.set_xticklabels(labels, rotation=45, ha="right")
    ax.set_ylabel("deviation at gap plane (m)")
    ax.set_xlabel("gap")
    ax.set_title("Swarm-center deviation from gap center at plane crossing")
    ax.grid(True, axis="y", alpha=0.25)
    ax.legend(loc="best")

    for x, value in zip(xs, values):
        ax.text(x, value, f"{value:.2f}", ha="center", va="bottom", fontsize=8)

    return ax


def plot_collectibles_by_gap(log: dict, ax=None):
    """Bar chart: number of collected star events grouped by generated gap."""
    if ax is None:
        _, ax = plt.subplots(figsize=(11, 4.5))

    events = log.get("collectibles", [])
    if not events:
        ax.text(0.5, 0.5, "no collectible events recorded\n(run with updated SwarmTrajectoryRecorder)",
                ha="center", va="center", transform=ax.transAxes)
        ax.axis("off")
        return ax

    counts = Counter(_gap_name_from_collectible(e.get("name", "")) for e in events)
    gaps = sorted(counts, key=_gap_sort_key)
    values = [counts[g] for g in gaps]
    xs = np.arange(len(gaps))

    ax.bar(xs, values, color="tab:blue", alpha=0.82)
    ax.set_xticks(xs)
    ax.set_xticklabels(gaps, rotation=45, ha="right")
    ax.set_ylabel("collected stars")
    ax.set_xlabel("gap")
    ax.set_title(f"Collected stars per gap (n={len(events)} events)")
    ax.grid(True, axis="y", alpha=0.25)

    for x, value in zip(xs, values):
        ax.text(x, value, str(value), ha="center", va="bottom", fontsize=8)

    return ax


def plot_summary(log: dict, ax=None, gap_deviations: list[dict] | None = None):
    """Text card with the headline run metrics."""
    if ax is None:
        _, ax = plt.subplots(figsize=(7, 5))

    elapsed   = log.get("elapsedTime", 0.0)
    picked    = log.get("collectiblesPickedUp", 0)
    total     = log.get("totalCollectibles", 0)
    n_crashes = log.get("crashCount", 0)
    sf        = log.get("swarmFrames", [])
    mean_disc = float(np.mean([f["nDisc"] for f in sf])) if sf else 0.0
    max_sub   = int(np.max([f["nSub"] for f in sf])) if sf else 0
    mean_gap_dev = None
    if gap_deviations:
        mean_gap_dev = float(np.mean([d["deviation"] for d in gap_deviations]))

    ratio = f"  ({100 * picked / total:.0f}%)" if total > 0 else ""
    lines = [
        f"PID:        {log.get('pid','?')}    Haptics: {log.get('haptics','?')}    Order: {log.get('order','?')}",
        f"Scene:      {log.get('scene','?')}",
        f"Embodied:   {log.get('embodiedName','?')} (id={log.get('embodiedId','?')})",
        "",
        f"Elapsed time:        {elapsed:.2f} s",
        f"Collectibles:        {picked} / {total}{ratio}",
        f"Crashes:             {n_crashes}",
        f"Mean disconnected:   {mean_disc:.2f} drones/frame",
        f"Max subnetworks:     {max_sub}  ({max(0, max_sub - 1)} gaps at peak)",
    ]
    if mean_gap_dev is not None:
        lines.extend([
            f"Mean gap deviation: {mean_gap_dev:.2f} m  ({len(gap_deviations)} crossings)",
        ])
    ax.axis("off")
    ax.text(0.02, 0.98, "\n".join(lines), family="monospace", fontsize=11,
            va="top", ha="left", transform=ax.transAxes)
    ax.set_title("Run summary")
    return ax


# --------------------------------------------------------------------------- #
# Driver
# --------------------------------------------------------------------------- #


# --- Multi-DOF control coordination (simplest version) -----------------------
DOF_KEYS = ["fmx", "fmy", "fmz", "fs", "fr"]
DOF_LABELS = ["x (fmx)", "height (fmy)", "z (fmz)", "spread (fs)", "view (fr)"]


def _run_window_inputs(log: dict) -> list:
    """Inputs restricted to the 'run' trial window when available."""
    ins = log.get("inputs", []) or []
    trials = [t for t in (log.get("trials") or [])
              if str(t.get("label", "")).strip().lower() == "run"
              and isinstance(t.get("startRealtime"), (int, float))
              and isinstance(t.get("endRealtime"), (int, float))]
    if trials:
        s = float(trials[0]["startRealtime"]); e = float(trials[0]["endRealtime"])
        ins = [f for f in ins if isinstance(f.get("t"), (int, float)) and s <= float(f["t"]) <= e]
    return ins


def coordination_metrics(log: dict, deadzone_frac: float = 0.15) -> dict | None:
    """Simplest multi-DOF coordination metric: at each timestep count how many of
    the five command DOF are actively driven (deviating from their neutral value
    by more than a per-channel deadzone). Returns the per-timestep count plus
    summary statistics (mean number of DOF active at once, fraction of time with
    >= 2 DOF active)."""
    ins = _run_window_inputs(log)
    if len(ins) < 2:
        return None
    t0 = _t0(log)
    t = np.array([float(f["t"]) - t0 for f in ins])
    cmd = np.array([[float(f.get(k, np.nan)) for k in DOF_KEYS] for f in ins])  # (N, 5)
    neutral = np.nanmedian(cmd, axis=0)                      # resting value per DOF
    lo = np.nanpercentile(cmd, 5, axis=0); hi = np.nanpercentile(cmd, 95, axis=0)
    thr = np.maximum(deadzone_frac * (hi - lo), 1e-6)        # per-DOF deadzone
    active = np.abs(cmd - neutral) > thr                     # (N, 5) bool
    n_active = active.sum(axis=1)
    return {
        "t": t, "n_active": n_active, "active": active,
        "neutral": neutral, "thr": thr,
        "mean_active": float(np.mean(n_active)),
        "frac_multi": float(np.mean(n_active >= 2)),
        "per_dof_active_frac": active.mean(axis=0),
    }



def zhai_coordination(log: dict) -> dict | None:
    """Zhai-style coordination: efficiency of the command trajectory in the
    5-DOF control space. Each DOF is normalised by its robust range so all
    contribute comparably, then we compare the city-block (sequential, one axis
    at a time) path length against the actual Euclidean path length.

    R = L1 / L2  (>= 1):  1 = perfectly sequential (one DOF at a time),
                          up to sqrt(n_dof) when all DOF move together.
    coord_norm = (R - 1) / (sqrt(n_dof) - 1)  in [0, 1].
    """
    ins = _run_window_inputs(log)
    if len(ins) < 3:
        return None
    cmd = np.array([[float(f.get(k, np.nan)) for k in DOF_KEYS] for f in ins])
    lo = np.nanpercentile(cmd, 5, axis=0); hi = np.nanpercentile(cmd, 95, axis=0)
    scale = np.maximum(hi - lo, 1e-6)
    q = cmd / scale
    dq = np.diff(q, axis=0)
    l1 = np.sum(np.abs(dq), axis=1)            # sequential (city-block) step length
    l2 = np.sqrt(np.sum(dq ** 2, axis=1))      # actual (Euclidean) step length
    L1 = float(l1.sum()); L2 = float(l2.sum())
    if L2 <= 0:
        return None
    n = cmd.shape[1]
    R = L1 / L2
    return {"R": R, "coord_norm": (R - 1.0) / (np.sqrt(n) - 1.0), "L1": L1, "L2": L2}


def plot_coordination(log: dict, axes=None, deadzone_frac: float = 0.15):
    """Plot (and print) the multi-DOF coordination metric for one run."""
    res = coordination_metrics(log, deadzone_frac)
    if axes is None:
        _, axes = plt.subplots(2, 1, figsize=(12, 6), gridspec_kw={"height_ratios": [2, 1]})
    if res is None:
        axes[0].text(0.5, 0.5, "no inputs in run window", ha="center", va="center",
                     transform=axes[0].transAxes)
        return axes
    t, n = res["t"], res["n_active"]
    axes[0].step(t, n, where="post", color="tab:blue", lw=1.0)
    axes[0].fill_between(t, n, step="post", alpha=0.18, color="tab:blue")
    axes[0].axhline(res["mean_active"], color="tab:red", ls="--", lw=1.2,
                    label=f"mean = {res['mean_active']:.2f} DOF")
    axes[0].set_ylabel("DOF active at once")
    axes[0].set_ylim(0, len(DOF_KEYS))
    zc = zhai_coordination(log)
    ztxt = ("  |  Zhai coord = %.2f" % zc["coord_norm"]) if zc else ""
    axes[0].set_title("Control coordination  |  mean %.2f DOF active,  %.0f%% of time >= 2 DOF%s"
                      % (res["mean_active"], 100 * res["frac_multi"], ztxt))
    axes[0].legend(loc="upper right"); axes[0].grid(True, alpha=0.3)
    _mark_trial_window(log, axes[0], _t0(log))
    counts = np.bincount(n, minlength=len(DOF_KEYS) + 1)[:len(DOF_KEYS) + 1]
    frac = counts / counts.sum() if counts.sum() else counts
    axes[1].bar(np.arange(len(frac)), frac, color="tab:blue", width=0.6)
    axes[1].set_xlabel("number of DOF active simultaneously")
    axes[1].set_ylabel("fraction of time")
    axes[1].set_xticks(np.arange(len(frac)))
    axes[1].grid(True, axis="y", alpha=0.3)
    print("Coordination (run window):")
    print("  mean DOF active simultaneously = %.2f" % res["mean_active"])
    print("  fraction of time with >= 2 DOF active = %.2f" % res["frac_multi"])
    print("  per-DOF active fraction: " +
          ", ".join("%s=%.2f" % (l, f) for l, f in zip(DOF_LABELS, res["per_dof_active_frac"])))
    if zc:
        print("  Zhai-style coordination: R(L1/L2) = %.3f,  coord_norm = %.3f" % (zc["R"], zc["coord_norm"]))
    return axes


def make_all(json_path: Path, outdir: Path, stride: int = 1, show: bool = False) -> None:
    log = load(json_path)
    course_path = autodetect_course_json(json_path)
    gaps = derive_gaps_from_obstacles(log.get("obstacles", []))
    if not gaps:
        gaps = load_course_gaps(course_path)
    gap_deviations = compute_gap_center_deviations(log, gaps)
    outdir.mkdir(parents=True, exist_ok=True)
    stem = json_path.stem

    figs: list = []

    fig, ax = plt.subplots(figsize=(10, 10))
    plot_trajectory_2d(log, ax=ax, stride=stride, gap_deviations=gap_deviations)
    fig.tight_layout(); fig.savefig(outdir / f"{stem}_trajectory.png", dpi=130)
    figs.append(fig)

    fig = plt.figure(figsize=(11, 9))
    ax = fig.add_subplot(111, projection="3d")
    plot_trajectory_3d(log, ax=ax, stride=stride, interactive=show)
    if show:
        fig.savefig(outdir / f"{stem}_trajectory_3d.png", dpi=130)
    else:
        fig.tight_layout(); fig.savefig(outdir / f"{stem}_trajectory_3d.png", dpi=130)
    figs.append(fig)

    fig, ax = plt.subplots(figsize=(12, 5))
    plot_swarm_health(log, ax=ax)
    fig.tight_layout(); fig.savefig(outdir / f"{stem}_swarm_health.png", dpi=130)
    figs.append(fig)

    fig, ax = plt.subplots(figsize=(10, 3.5))
    plot_crash_timeline(log, ax=ax)
    fig.tight_layout(); fig.savefig(outdir / f"{stem}_crashes.png", dpi=130)
    figs.append(fig)

    fig, axes = plt.subplots(4, 1, figsize=(12, 10), sharex=True)
    plot_inputs(log, axes=axes)
    fig.tight_layout(); fig.savefig(outdir / f"{stem}_inputs.png", dpi=130)
    figs.append(fig)

    fig, axes = plt.subplots(2, 1, figsize=(12, 6), gridspec_kw={"height_ratios": [2, 1]})
    plot_coordination(log, axes=axes)
    fig.tight_layout(); fig.savefig(outdir / f"{stem}_coordination.png", dpi=130)
    figs.append(fig)

    fig, axes = plt.subplots(2, 1, figsize=(12, 7), sharex=True)
    plot_swarm_center_velocity(log, axes=axes)
    fig.tight_layout(); fig.savefig(outdir / f"{stem}_swarm_center_velocity.png", dpi=130)
    figs.append(fig)

    fig, ax = plt.subplots(figsize=(10, 10))
    plot_gaps(log, ax=ax)
    fig.tight_layout(); fig.savefig(outdir / f"{stem}_gaps.png", dpi=130)
    figs.append(fig)

    fig, ax = plt.subplots(figsize=(11, 4.5))
    plot_collectibles_by_gap(log, ax=ax)
    fig.tight_layout(); fig.savefig(outdir / f"{stem}_collectibles_by_gap.png", dpi=130)
    figs.append(fig)

    fig, ax = plt.subplots(figsize=(11, 4.5))
    plot_gap_center_deviation(log, gaps, ax=ax)
    fig.tight_layout(); fig.savefig(outdir / f"{stem}_gap_center_deviation.png", dpi=130)
    figs.append(fig)

    fig, ax = plt.subplots(figsize=(7, 5))
    plot_summary(log, ax=ax, gap_deviations=gap_deviations)
    fig.tight_layout(); fig.savefig(outdir / f"{stem}_summary.png", dpi=130)
    figs.append(fig)

    print(f"Wrote 11 plots to {outdir}/")
    if gap_deviations:
        mean_dev = float(np.mean([d["deviation"] for d in gap_deviations]))
        print(f"Mean gap-center deviation: {mean_dev:.3f} m ({len(gap_deviations)} crossings)")
    elif course_path is None:
        print("No course gap-center file found; skipped gap-center deviation statistics.")
    if show:
        plt.show()
    else:
        for f in figs:
            plt.close(f)


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("json", type=Path, nargs="?", default=None,
                   help="Path to *_traj.json. If omitted, asks whether to use the most recent "
                        "file under <repo>/SoundMapping/SoundMappingUnity/Assets/Trajectories/.")
    p.add_argument("-o", "--outdir", type=Path, default=None,
                   help="Output directory (default: <json-parent>/<json-stem>_plots/).")
    p.add_argument("--stride", type=int, default=10,
                   help="Keep every Nth trajectory frame in plot 1 (default: 10). "
                        "Use 1 for full detail, higher values for faster rendering on long runs.")
    p.add_argument("--show", action="store_true",
                   help="Open the plots in an interactive window after saving.")
    args = p.parse_args()

    json_path = args.json
    if json_path is None:
        json_path = autodetect_json(Path.cwd())
        if json_path is None:
            raise SystemExit(
                "No *_traj.json found. Pass a path explicitly, or make sure you've "
                "run a session in Unity that saved a trajectory under Assets/Trajectories/."
            )
        print(f"Latest trajectory JSON found:\n  {json_path}")
        resp = input("Press Enter to use this latest file, or type 'b' to browse for another file: ").strip().lower()
        if resp == "b":
            selected = ask_for_trajectory_json(autodetect_trajectory_dir(Path.cwd()) or json_path.parent)
            if selected is None:
                raise SystemExit("Canceled. No JSON file selected.")
            json_path = selected
            if not json_path.exists():
                raise SystemExit(f"File not found: {json_path}")
        print(f"Selected: {json_path}")
    elif not json_path.exists():
        raise SystemExit(f"File not found: {json_path}")

    outdir = args.outdir or (json_path.parent / f"{json_path.stem}_plots")
    make_all(json_path, outdir, stride=args.stride, show=args.show)


if __name__ == "__main__":
    main()
