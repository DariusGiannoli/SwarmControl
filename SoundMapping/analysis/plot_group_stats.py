"""Compare multiple SwarmTrajectoryRecorder runs across control groups.

Usage:
    python SoundMapping/analysis/plot_group_stats.py
    python SoundMapping/analysis/plot_group_stats.py --no-gui \
        --joystick run1_traj.json run2_traj.json \
        --body-control run3_traj.json run4_traj.json

The GUI starts from the last saved selection, shows the current files, and lets
you add joystick/body-control runs, remove entries, or continue without adding.
It then computes run-level statistics and writes plots/tables under
SoundMapping/analysis/outputs/group_stats/.
"""

from __future__ import annotations

import argparse
import csv
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import tempfile
from typing import Iterable

_cache_root = Path(tempfile.gettempdir()) / "swarmcontrol_plot_group_stats"
_cache_root.mkdir(parents=True, exist_ok=True)
os.environ.setdefault("MPLCONFIGDIR", str(_cache_root / "matplotlib"))
os.environ.setdefault("XDG_CACHE_HOME", str(_cache_root / "xdg"))

import matplotlib.pyplot as plt
from matplotlib.figure import Figure
import numpy as np
plt.rcParams.update({
    "pdf.fonttype": 42,
    "ps.fonttype": 42,
    "svg.fonttype": "none",
    "font.size": 15,
})
try:
    from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg, NavigationToolbar2Tk
except Exception:
    FigureCanvasTkAgg = None
    NavigationToolbar2Tk = None

try:
    import tkinter as tk
    from tkinter import filedialog, messagebox, ttk
except Exception:
    tk = None
    filedialog = None
    messagebox = None
    ttk = None

try:
    from scipy.stats import wilcoxon
except Exception:
    wilcoxon = None

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parents[1]
DEFAULT_TRAJ_DIR = REPO_ROOT / "SoundMapping" / "SoundMappingUnity" / "Assets" / "Trajectories"
DATA_DIR = REPO_ROOT / "data"
OUT_DIR = SCRIPT_DIR / "outputs" / "group_stats"
SELECTION_CACHE = SCRIPT_DIR / "plot_group_stats_selection.json"
UI_STATE_CACHE = SCRIPT_DIR / "plot_group_stats_ui_state.json"

GROUPS = ["joystick", "body-control"]
GROUP_LABELS = {
    "joystick": "Joystick",
    "body-control": "Body-control",
}
GROUP_COLORS = {
    "joystick": "tab:blue",
    "body-control": "tab:orange",
}
NORMALIZATION_SCALES = {
    "x": 55.0,
    "y": 21.0,
    "z": 200.0,
    "spread": 4.0,
    "view": float(np.pi),
}
COMMAND_NORMALIZATION_SCALES = {
    "x": 2.0,
    "y": 2.0,
    "z": 2.0,
    "spread": 4.0,
    "view": 2.0,
}
CAMERA_ROTATION_SPEED_RAD_S = float(np.deg2rad(80.0))
ACCELERATION_COMPONENT_KEYS = [
    "normalized_acceleration_x",
    "normalized_acceleration_y",
    "normalized_acceleration_z",
    "normalized_acceleration_spread",
    "normalized_acceleration_view",
]
ACCELERATION_METRIC_KEYS = [
    *ACCELERATION_COMPONENT_KEYS,
    "normalized_acceleration_5dof",
]
COMMAND_RATE_COMPONENT_KEYS = [
    "command_rate_x",
    "command_rate_y",
    "command_rate_z",
    "command_rate_spread",
    "command_rate_view",
]
COMMAND_RATE_METRIC_KEYS = [
    *COMMAND_RATE_COMPONENT_KEYS,
    "command_rate_5dof",
]
METRIC_KEYS = [
    "duration_s",
    "path_length_m",
    "mean_speed_mps",
    "collection_pct",
    "crash_count",
    "mean_disconnected",
    "command_rate_5dof",
    "mean_gap_deviation_m",
    "coordination",
]
METRIC_LABELS = {
    "duration_s": "Duration (s)",
    "path_length_m": "Swarm-center path length (m)",
    "mean_speed_mps": "Mean swarm-center speed (m/s)",
    "collectibles": "Collectibles picked up",
    "collection_pct": "Collectibles picked up (%)",
    "crash_count": "Crash count",
    "mean_disconnected": "Mean disconnected drones",
    "normalized_acceleration_x": "X normalized acceleration",
    "normalized_acceleration_y": "Y normalized acceleration",
    "normalized_acceleration_z": "Z normalized acceleration",
    "normalized_acceleration_spread": "Spread normalized acceleration",
    "normalized_acceleration_view": "View normalized acceleration",
    "normalized_acceleration_5dof": "5-DOF normalized acceleration (lower = smoother)",
    "command_rate_x": "X command-rate energy",
    "command_rate_y": "Y command-rate energy",
    "command_rate_z": "Z command-rate energy",
    "command_rate_spread": "Spread command-rate energy",
    "command_rate_view": "View command-rate energy",
    "command_rate_5dof": "5-DOF control command smoothness",
    "mean_gap_deviation_m": "Mean gap-center deviation (m)",
    "coordination": "Multi-DOF coordination",
}

try:
    import plot_run
except Exception as exc:
    raise SystemExit(f"Could not import sibling plot_run.py: {exc}") from exc


@dataclass(frozen=True)
class Selection:
    group: str
    path: Path
    participant: str = ""
    included: bool = True


def keep_window_on_top(win, parent=None, modal: bool = False) -> None:
    """Raise a Tk window and keep it visible above the main app windows."""
    try:
        if parent is not None:
            win.transient(parent)
            parent.attributes("-topmost", False)

            def restore_parent_topmost(event) -> None:
                if event.widget is win:
                    try:
                        parent.attributes("-topmost", True)
                    except Exception:
                        pass

            win.bind("<Destroy>", restore_parent_topmost, add="+")
    except Exception:
        pass
    try:
        win.update_idletasks()
        win.lift()
        win.attributes("-topmost", True)
        win.focus_force()
        win.after(100, win.lift)
    except Exception:
        pass
    if modal:
        try:
            win.grab_set()
        except Exception:
            pass


def screen_size_px(root=None) -> tuple[int, int]:
    if root is not None:
        try:
            return int(root.winfo_screenwidth()), int(root.winfo_screenheight())
        except Exception:
            pass
    return 1440, 900


def fit_toplevel_to_screen(win, min_width: int, min_height: int, width_frac: float = 0.96, height_frac: float = 0.92) -> None:
    screen_w, screen_h = screen_size_px(win)
    width = max(min_width, int(screen_w * width_frac))
    height = max(min_height, int(screen_h * height_frac))
    width = min(width, max(screen_w - 80, min_width))
    height = min(height, max(screen_h - 90, min_height))
    x = max(20, int((screen_w - width) / 2))
    y = max(20, int((screen_h - height) / 2))
    try:
        win.geometry(f"{width}x{height}+{x}+{y}")
    except Exception:
        pass


def metric_grid_shape(metric_count: int) -> tuple[int, int]:
    if metric_count <= 4:
        return 1, max(1, metric_count)
    if metric_count <= 8:
        return 2, 4
    ncols = 3
    return int(np.ceil(metric_count / ncols)), ncols


def figure_size_for_screen(ncols: int, nrows: int, root=None, screen_size: tuple[int, int] | None = None) -> tuple[float, float]:
    screen_w, screen_h = screen_size if screen_size is not None else screen_size_px(root)
    dpi = float(plt.rcParams.get("figure.dpi", 100) or 100)
    width = max(3.2 * ncols, screen_w * 0.94 / dpi)
    height = max(3.0 * nrows, screen_h * 0.82 / dpi)
    return width, height


def fit_matplotlib_window_to_screen(fig, screen_size: tuple[int, int] | None = None) -> None:
    screen_w, screen_h = screen_size if screen_size is not None else screen_size_px()
    width = int(screen_w * 0.92)
    height = int(screen_h * 0.86)
    try:
        manager = fig.canvas.manager
    except Exception:
        manager = None
    if manager is None:
        return
    try:
        manager.resize(width, height)
    except Exception:
        pass
    window = getattr(manager, "window", None)
    if window is not None:
        try:
            x = max(20, int((screen_w - width) / 2))
            y = max(20, int((screen_h - height) / 2))
            window.geometry(f"{width}x{height}+{x}+{y}")
        except Exception:
            pass


def show_figure_window(fig, title: str, screen_size: tuple[int, int] | None = None) -> bool:
    if tk is None or FigureCanvasTkAgg is None:
        return False
    screen_w, screen_h = screen_size if screen_size is not None else screen_size_px()
    width = max(900, int(screen_w * 0.96))
    height = max(620, int(screen_h * 0.92))
    x = max(0, int((screen_w - width) / 2))
    y = max(0, int((screen_h - height) / 2))
    try:
        win = tk.Tk()
    except Exception:
        return False
    win.title(title)
    win.geometry(f"{width}x{height}+{x}+{y}")
    win.minsize(900, 620)
    frame = tk.Frame(win)
    canvas = FigureCanvasTkAgg(fig, master=frame)
    canvas.draw()
    if NavigationToolbar2Tk is not None:
        toolbar = NavigationToolbar2Tk(canvas, win, pack_toolbar=False)
        toolbar.update()
        toolbar.pack(side=tk.TOP, fill=tk.X)
    frame.pack(fill="both", expand=True)
    canvas.get_tk_widget().pack(fill="both", expand=True)

    def close() -> None:
        try:
            win.attributes("-topmost", False)
        except Exception:
            pass
        win.destroy()

    tk.Button(win, text="Close", command=close).pack(anchor="e", padx=10, pady=(0, 10))
    win.protocol("WM_DELETE_WINDOW", close)
    try:
        win.update_idletasks()
        win.lift()
        win.attributes("-topmost", True)
        win.after(600, lambda: win.attributes("-topmost", False))
    except Exception:
        pass
    win.mainloop()
    return True


class AnalysisProgress:
    def __init__(self, total: int, screen_size: tuple[int, int] | None = None) -> None:
        self.win = None
        self.label_var = None
        self.progress_var = None
        if tk is None or ttk is None:
            return
        screen_w, screen_h = screen_size if screen_size is not None else screen_size_px()
        width, height = 620, 150
        x = max(20, int((screen_w - width) / 2))
        y = max(20, int((screen_h - height) / 2))
        try:
            self.win = tk.Tk()
            self.win.title("Calculating trajectory statistics")
            self.win.geometry(f"{width}x{height}+{x}+{y}")
            self.win.resizable(False, False)
            self.label_var = tk.StringVar(value="Preparing analysis...")
            self.progress_var = tk.DoubleVar(value=0.0)
            tk.Label(self.win, textvariable=self.label_var, anchor="w", justify="left").pack(
                fill="x", padx=16, pady=(18, 10)
            )
            ttk.Progressbar(
                self.win,
                variable=self.progress_var,
                maximum=max(total, 1),
                mode="determinate",
            ).pack(fill="x", padx=16, pady=(0, 16))
            self.win.protocol("WM_DELETE_WINDOW", lambda: None)
            self.win.update_idletasks()
            self.win.lift()
            self.win.attributes("-topmost", True)
            self.win.after(500, lambda: self._set_topmost(False))
            self.win.update()
        except Exception:
            self.close()

    def _set_topmost(self, value: bool) -> None:
        if self.win is None:
            return
        try:
            self.win.attributes("-topmost", value)
        except Exception:
            pass

    def update(self, current: int, total: int, message: str) -> None:
        if self.win is None:
            return
        try:
            self.label_var.set(message)
            self.progress_var.set(current)
            self.win.title(f"Calculating trajectory statistics ({current}/{total})")
            self.win.update_idletasks()
            self.win.update()
        except Exception:
            self.win = None

    def close(self) -> None:
        if self.win is None:
            return
        try:
            self.win.attributes("-topmost", False)
        except Exception:
            pass
        try:
            self.win.destroy()
        except Exception:
            pass
        self.win = None


def normalize_path(path: str | Path) -> Path:
    p = Path(path).expanduser()
    return p.absolute()


def relocate_missing_data_path(path: str | Path) -> Path:
    original = normalize_path(path)
    if original.exists():
        return original
    for directory in (DATA_DIR, DEFAULT_TRAJ_DIR):
        candidate = directory / original.name
        if candidate.exists():
            return normalize_path(candidate)
    return original


def load_json(path: Path) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


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


def cached_dir(key: str, fallback: Path) -> Path:
    state = load_ui_state()
    value = state.get(key)
    if value:
        path = Path(value).expanduser()
        if path.exists() and path.is_dir():
            return path
    return fallback if fallback.exists() else Path.cwd()


def remember_dir(key: str, path: str | Path) -> None:
    p = Path(path).expanduser()
    directory = p if p.is_dir() else p.parent
    if not directory.exists():
        return
    state = load_ui_state()
    state[key] = str(normalize_path(directory))
    save_ui_state(state)


def dedupe_selections(selections: Iterable[Selection]) -> list[Selection]:
    out: list[Selection] = []
    seen: set[str] = set()
    for sel in selections:
        group = normalize_group(sel.group)
        if group not in GROUPS:
            continue
        relocated_path = relocate_missing_data_path(sel.path)
        key = str(relocated_path)
        if key in seen:
            continue
        seen.add(key)
        participant = (sel.participant or "").strip()
        out.append(Selection(group=group, path=relocated_path, participant=participant, included=bool(sel.included)))
    return out


def selection_key(sel: Selection) -> str:
    return str(Path(sel.path).expanduser().absolute())


def normalize_group(text: str) -> str:
    t = (text or "").strip().lower().replace("_", "-").replace(" ", "-")
    if t in {"joystick", "joy", "traditional", "controller"}:
        return "joystick"
    if t in {"body", "body-control", "bodycontrol", "pose", "imu"}:
        return "body-control"
    return t


def is_formal_trial_file(path: str | Path) -> bool:
    stem = Path(path).stem.lower()
    return re.search(r"(^|_)t[12]($|_)", stem) is not None


def trial_label_from_path(path: str | Path) -> str:
    stem = Path(path).stem.lower()
    match = re.search(r"(^|_)t([12])($|_)", stem)
    return f"t{match.group(2)}" if match else ""


def selection_timestamp(path: str | Path) -> tuple[float, str]:
    try:
        stat = Path(path).stat()
    except OSError:
        return float("nan"), ""
    created = getattr(stat, "st_birthtime", float("nan"))
    if np.isfinite(float(created)):
        return float(created), "created"
    return float(stat.st_mtime), "modified"


def print_participants_by_start_interface(selections: Iterable[Selection]) -> None:
    by_participant: dict[str, dict[str, tuple[float, str]]] = {}
    timestamp_source = ""
    for sel in selections:
        participant = (sel.participant or "").strip()
        if not participant or sel.group not in GROUPS or not is_formal_trial_file(sel.path):
            continue
        ts, source = selection_timestamp(sel.path)
        if not np.isfinite(ts):
            continue
        timestamp_source = timestamp_source or source
        group_times = by_participant.setdefault(participant, {})
        if sel.group not in group_times or ts < group_times[sel.group][0]:
            group_times[sel.group] = (ts, sel.path.name)

    joystick_first = []
    body_first = []
    unknown = []
    for participant, group_times in sorted(by_participant.items()):
        if "joystick" not in group_times or "body-control" not in group_times:
            unknown.append(participant)
        elif group_times["joystick"][0] < group_times["body-control"][0]:
            joystick_first.append(participant)
        else:
            body_first.append(participant)

    print(f"\nParticipants grouped by first interface ({timestamp_source or 'file'} time):")
    print(f"  Joystick first (n={len(joystick_first)}): {', '.join(joystick_first) if joystick_first else '-'}")
    print(f"  Body-control first (n={len(body_first)}): {', '.join(body_first) if body_first else '-'}")
    if unknown:
        print(f"  Incomplete/unknown (n={len(unknown)}): {', '.join(unknown)}")


def load_selection_cache() -> list[Selection]:
    return load_selection_file(SELECTION_CACHE)


def load_selection_file(path: Path) -> list[Selection]:
    if not path.exists():
        return []
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return []
    items = []
    for row in data if isinstance(data, list) else []:
        if not isinstance(row, dict):
            continue
        group = normalize_group(str(row.get("group", "")))
        path = row.get("path")
        if group in GROUPS and path:
            items.append(Selection(
                group=group,
                path=Path(path),
                participant=str(row.get("participant", "")).strip(),
                included=bool(row.get("included", True)),
            ))
    return dedupe_selections(items)


def save_selection_cache(selections: Iterable[Selection]) -> None:
    save_selection_file(SELECTION_CACHE, selections)


def save_selection_file(path: Path, selections: Iterable[Selection]) -> None:
    data = [
        {
            "group": sel.group,
            "participant": sel.participant,
            "path": str(normalize_path(sel.path)),
            "included": bool(sel.included),
        }
        for sel in dedupe_selections(selections)
    ]
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def infer_participant_from_file(path: Path) -> str:
    try:
        text = Path(path).read_text(encoding="utf-8", errors="ignore")[:16384]
    except Exception:
        return ""
    match = re.search(r'"pid"\s*:\s*"([^"]*)"', text)
    return match.group(1).strip() if match else ""


def is_placeholder_participant(participant: str) -> bool:
    text = (participant or "").strip().lower()
    return text in {"", "default"}


TRIALS_PER_PARTICIPANT = 4


def next_participant_id(existing: Iterable[str], trials_per_participant: int = TRIALS_PER_PARTICIPANT) -> str:
    counts = {}
    for participant in existing:
        text = (participant or "").strip()
        match = re.fullmatch(r"P(\d+)", text, flags=re.IGNORECASE)
        if match:
            idx = int(match.group(1))
            counts[idx] = counts.get(idx, 0) + 1
    idx = 0
    while counts.get(idx, 0) >= trials_per_participant:
        idx += 1
    return f"P{idx}"


def default_participant_id(inferred: str, selections: Iterable[Selection]) -> str:
    if not is_placeholder_participant(inferred):
        return inferred.strip()
    return next_participant_id(sel.participant for sel in selections)


def default_participant_ids_for_paths(paths: Iterable[Path], selections: Iterable[Selection]) -> list[str]:
    out = []
    assigned = list(dedupe_selections(selections))
    for path in paths:
        inferred = infer_participant_from_file(path)
        participant = default_participant_id(inferred, assigned)
        out.append(participant)
        assigned.append(Selection(group="joystick", path=path, participant=participant, included=True))
    return out


def fill_default_participants(selections: Iterable[Selection]) -> list[Selection]:
    out = []
    for sel in dedupe_selections(selections):
        participant = sel.participant.strip()
        if is_placeholder_participant(participant):
            inferred = infer_participant_from_file(sel.path)
            participant = default_participant_id(inferred, out)
        out.append(Selection(group=sel.group, path=sel.path, participant=participant, included=sel.included))
    return out


def infer_group_from_path(path: Path) -> str | None:
    text = str(path).lower()
    if any(tok in text for tok in ("joystick", "joy", "traditional", "controller")):
        return "joystick"
    if any(tok in text for tok in ("body-control", "body_control", "bodycontrol", "body", "pose", "imu")):
        return "body-control"
    return None


def pick_group_for_files(root, default_group: str) -> str | None:
    if tk is None or ttk is None:
        return default_group
    win = tk.Toplevel(root)
    win.title("Choose group")
    win.geometry("+120+120")
    win.resizable(False, False)
    win.transient(root)
    chosen = {"value": None}

    tk.Label(win, text="Assign selected files to:").grid(row=0, column=0, columnspan=2, padx=12, pady=(12, 6), sticky="w")
    var = tk.StringVar(value=default_group)
    combo = ttk.Combobox(win, textvariable=var, state="readonly", values=GROUPS, width=18)
    combo.grid(row=1, column=0, columnspan=2, padx=12, pady=4, sticky="ew")

    def apply():
        chosen["value"] = normalize_group(var.get())
        try:
            win.attributes("-topmost", False)
        except Exception:
            pass
        win.destroy()

    def cancel():
        chosen["value"] = None
        try:
            win.attributes("-topmost", False)
        except Exception:
            pass
        win.destroy()

    tk.Button(win, text="Cancel", command=cancel).grid(row=2, column=0, padx=12, pady=12, sticky="w")
    tk.Button(win, text="Add", command=apply).grid(row=2, column=1, padx=12, pady=12, sticky="e")
    win.protocol("WM_DELETE_WINDOW", cancel)
    keep_window_on_top(win, root, modal=True)
    win.wait_window()
    return chosen["value"]


def ask_participant_id(root, default_participant: str = "") -> str | None:
    if tk is None:
        return default_participant
    win = tk.Toplevel(root)
    win.title("Participant ID")
    win.geometry("+140+140")
    win.resizable(False, False)
    win.transient(root)
    chosen = {"value": None}

    tk.Label(win, text="Participant ID for selected file(s):").grid(
        row=0, column=0, columnspan=2, padx=12, pady=(12, 4), sticky="w"
    )
    var = tk.StringVar(value=default_participant)
    entry = tk.Entry(win, textvariable=var, width=30)
    entry.grid(row=1, column=0, columnspan=2, padx=12, pady=4, sticky="ew")
    entry.focus_set()
    entry.select_range(0, tk.END)

    def apply() -> None:
        chosen["value"] = var.get().strip()
        try:
            win.attributes("-topmost", False)
        except Exception:
            pass
        win.destroy()

    def cancel() -> None:
        chosen["value"] = None
        try:
            win.attributes("-topmost", False)
        except Exception:
            pass
        win.destroy()

    tk.Button(win, text="Cancel", command=cancel).grid(row=2, column=0, padx=12, pady=12, sticky="w")
    tk.Button(win, text="Apply", command=apply).grid(row=2, column=1, padx=12, pady=12, sticky="e")
    win.bind("<Return>", lambda _event: apply())
    win.protocol("WM_DELETE_WINDOW", cancel)
    keep_window_on_top(win, root, modal=True)
    win.wait_window()
    return chosen["value"]


def show_full_path(root, path: Path) -> None:
    if tk is None:
        return
    win = tk.Toplevel(root)
    win.title("Full file path")
    win.geometry("760x120+160+160")
    win.minsize(520, 110)
    win.transient(root)

    tk.Label(win, text="Full path:").pack(anchor="w", padx=12, pady=(12, 4))
    text = tk.Text(win, height=2, wrap="none")
    text.pack(fill="both", expand=True, padx=12, pady=(0, 8))
    text.insert("1.0", str(path))
    text.configure(state="disabled")

    def close() -> None:
        try:
            win.attributes("-topmost", False)
        except Exception:
            pass
        win.destroy()

    tk.Button(win, text="Close", command=close).pack(anchor="e", padx=12, pady=(0, 10))
    win.protocol("WM_DELETE_WINDOW", close)
    keep_window_on_top(win, root, modal=False)


def _format_metric_value(value) -> str:
    try:
        val = float(value)
    except Exception:
        return ""
    if not np.isfinite(val):
        return ""
    return f"{val:.4g}"


def show_participant_results_window(root, rows: list[dict]) -> None:
    if tk is None or ttk is None or FigureCanvasTkAgg is None:
        return
    if not rows:
        if messagebox is not None:
            messagebox.showwarning("No results", "No participant results are available.")
        return

    participants = sorted({str(r.get("participant", "")).strip() for r in rows if str(r.get("participant", "")).strip()})
    if not participants:
        if messagebox is not None:
            messagebox.showwarning("No participants", "No participant IDs were found in the selected files.")
        return

    win = tk.Toplevel(root)
    win.title("Participant results")
    win.minsize(820, 480)
    fit_toplevel_to_screen(win, min_width=980, min_height=620)
    win.transient(root)

    left = tk.LabelFrame(win, text="Participants", padx=6, pady=6)
    left.grid(row=0, column=0, sticky="ns", padx=10, pady=10)
    right = tk.Frame(win)
    right.grid(row=0, column=1, sticky="nsew", padx=(0, 10), pady=10)
    win.rowconfigure(0, weight=1)
    win.columnconfigure(1, weight=1)
    right.rowconfigure(0, weight=1)
    right.columnconfigure(0, weight=1)

    nrows, ncols = metric_grid_shape(len(METRIC_KEYS))
    fig = Figure(figsize=figure_size_for_screen(ncols, nrows, win))
    axes = fig.subplots(nrows, ncols)
    fig.subplots_adjust(hspace=0.55, wspace=0.35)
    canvas = FigureCanvasTkAgg(fig, master=right)
    if NavigationToolbar2Tk is not None:
        toolbar = NavigationToolbar2Tk(canvas, right, pack_toolbar=False)
        toolbar.update()
        toolbar.grid(row=0, column=0, sticky="ew")
        canvas.get_tk_widget().grid(row=1, column=0, sticky="nsew")
        right.rowconfigure(0, weight=0)
        right.rowconfigure(1, weight=1)
    else:
        canvas.get_tk_widget().grid(row=0, column=0, sticky="nsew")

    def show_participant(participant: str) -> None:
        participant_rows = [
            r for r in rows
            if str(r.get("participant", "")).strip() == participant
        ]
        axes_arr = np.asarray(axes).ravel()
        rng = np.random.default_rng(42)
        for ax, key in zip(axes_arr, METRIC_KEYS):
            ax.clear()
            group_values = []
            means = []
            for group in GROUPS:
                vals = [
                    float(r[key])
                    for r in participant_rows
                    if r.get("group") == group and np.isfinite(float(r.get(key, np.nan)))
                ]
                group_values.append(np.array(vals, dtype=float))
                means.append(float(np.mean(vals)) if vals else np.nan)

            xs = np.arange(len(GROUPS), dtype=float)
            colors = [GROUP_COLORS[group] for group in GROUPS]
            ax.bar(xs, means, color=colors, alpha=0.32, edgecolor=colors, linewidth=1.2)
            for idx, vals in enumerate(group_values):
                if len(vals) == 0:
                    continue
                jitter = rng.uniform(-0.08, 0.08, size=len(vals))
                ax.scatter(
                    np.full(len(vals), xs[idx]) + jitter,
                    vals,
                    color=colors[idx],
                    s=38,
                    alpha=0.82,
                    edgecolor="white",
                    linewidth=0.5,
                    zorder=3,
                )
            ax.set_xticks(xs)
            ax.set_xticklabels([GROUP_LABELS[group] for group in GROUPS], rotation=20, ha="right")
            ax.set_title(METRIC_LABELS[key], fontsize=10)
            ax.grid(True, axis="y", alpha=0.25)
            finite = np.concatenate([vals for vals in group_values if len(vals)]) if any(len(vals) for vals in group_values) else np.array([])
            if len(finite) == 0:
                ax.text(0.5, 0.5, "no data", ha="center", va="center", transform=ax.transAxes)
        for ax in axes_arr[len(METRIC_KEYS):]:
            ax.clear()
            ax.axis("off")
        fig.suptitle(f"Participant {participant} results", fontsize=14)
        fig.tight_layout(rect=(0, 0, 1, 0.95))
        canvas.draw_idle()

    for participant in participants:
        tk.Button(left, text=participant, width=12, command=lambda p=participant: show_participant(p)).pack(
            fill="x", pady=2
        )

    def close() -> None:
        try:
            win.attributes("-topmost", False)
        except Exception:
            pass
        win.destroy()

    tk.Button(left, text="Close", command=close).pack(fill="x", pady=(12, 2))
    win.protocol("WM_DELETE_WINDOW", close)
    keep_window_on_top(win, root, modal=False)
    show_participant(participants[0])


def _selection_save_default_name() -> str:
    existing = sorted(SCRIPT_DIR.glob("plot_group_stats_selection*.json"))
    if not existing:
        return "plot_group_stats_selection.json"
    idx = 1
    while (SCRIPT_DIR / f"plot_group_stats_selection_{idx}.json").exists():
        idx += 1
    return f"plot_group_stats_selection_{idx}.json"


def select_files_gui(initial: list[Selection]) -> dict[str, list[Selection]] | None:
    if tk is None or filedialog is None:
        return None

    selections = dedupe_selections(initial)
    included_keys = {selection_key(sel) for sel in selections if sel.included}
    root = tk.Tk()
    root.title("Trajectory files for group statistics")
    root.geometry("+80+80")
    root.minsize(1120, 520)
    try:
        root.attributes("-topmost", True)
    except Exception:
        pass

    state = {"result": None}
    tk.Label(
        root,
        text=(
            "All selected trajectory files are shown. Click a file row to toggle whether it is "
            "highlighted/used in the final plots and statistics."
        ),
        anchor="w",
    ).grid(row=0, column=0, columnspan=2, sticky="ew", padx=10, pady=(10, 4))

    trees = {}
    if ttk is None:
        root.destroy()
        return None

    columns = ("participant", "file", "status")
    for col, group in enumerate(GROUPS):
        frame = tk.LabelFrame(root, text=f"{GROUP_LABELS[group]} files", padx=6, pady=6)
        frame.grid(row=1, column=col, sticky="nsew", padx=10, pady=6)
        frame.rowconfigure(0, weight=1)
        frame.columnconfigure(0, weight=1)

        tree = ttk.Treeview(frame, columns=columns, show="headings", selectmode="extended")
        tree.heading("participant", text="Participant ID")
        tree.heading("file", text="File")
        tree.heading("status", text="Status")
        tree.column("participant", width=125, stretch=False)
        tree.column("file", width=370, stretch=True)
        tree.column("status", width=82, stretch=False)
        tree.grid(row=0, column=0, sticky="nsew")
        sb = ttk.Scrollbar(frame, orient="vertical", command=tree.yview)
        sb.grid(row=0, column=1, sticky="ns")
        tree.configure(yscrollcommand=sb.set)
        tree.tag_configure("included", background="#e8f5e9")
        tree.tag_configure("skipped", foreground="#777777")
        trees[group] = tree

    def show_row_path(event) -> None:
        tree = event.widget
        iid = tree.identify_row(event.y)
        if not iid:
            return
        idx = int(iid)
        if 0 <= idx < len(selections):
            tree.selection_set(iid)
            show_full_path(root, selections[idx].path)

    def toggle_row_inclusion(event) -> None:
        tree = event.widget
        if tree.identify_region(event.x, event.y) not in ("cell", "tree"):
            return
        iid = tree.identify_row(event.y)
        if not iid:
            return
        idx = int(iid)
        if not (0 <= idx < len(selections)):
            return
        key = selection_key(selections[idx])
        if key in included_keys:
            included_keys.discard(key)
        else:
            included_keys.add(key)
        refresh()
        try:
            tree.selection_set(iid)
        except Exception:
            pass

    for tree in trees.values():
        tree.bind("<ButtonRelease-1>", toggle_row_inclusion)
        tree.bind("<Double-1>", show_row_path)

    def refresh() -> None:
        for tree in trees.values():
            tree.delete(*tree.get_children())
        for i, sel in enumerate(selections):
            included = selection_key(sel) in included_keys
            status = "USE" if included else "SKIP"
            tag = "included" if included else "skipped"
            trees[sel.group].insert(
                "",
                "end",
                iid=str(i),
                values=(sel.participant, str(sel.path), status),
                tags=(tag,),
            )

    def selected_indices(group: str | None = None) -> list[int]:
        idxs = []
        active_groups = [group] if group else GROUPS
        for g in active_groups:
            idxs.extend(int(iid) for iid in trees[g].selection())
        return sorted(set(idxs))

    def add_files(group: str | None = None) -> None:
        start = cached_dir("trajectory_dir", DEFAULT_TRAJ_DIR)
        paths = filedialog.askopenfilenames(
            parent=root,
            title="Select trajectory JSON files",
            initialdir=str(start),
            filetypes=[("Trajectory JSON", "*.json"), ("All files", "*.*")],
        )
        if not paths:
            return
        remember_dir("trajectory_dir", paths[0])
        target_group = group
        if target_group is None:
            inferred = infer_group_from_path(Path(paths[0])) or "joystick"
            target_group = pick_group_for_files(root, inferred)
        if target_group not in GROUPS:
            return
        existing_paths = {str(normalize_path(sel.path)) for sel in selections}
        path_objs = []
        duplicate_count = 0
        batch_seen = set()
        for p in paths:
            normalized = str(normalize_path(Path(p)))
            if normalized in existing_paths or normalized in batch_seen:
                duplicate_count += 1
                continue
            batch_seen.add(normalized)
            path_objs.append(Path(p))
        if duplicate_count and messagebox is not None:
            messagebox.showinfo(
                "Duplicate file skipped",
                f"Skipped {duplicate_count} duplicate file(s). The same JSON file can only be selected once.",
            )
        if not path_objs:
            return
        default_participants = default_participant_ids_for_paths(path_objs, selections)
        default_participant = default_participants[0] if default_participants else ""
        participant = ask_participant_id(root, default_participant)
        if participant is None:
            return
        if participant == default_participant and len(set(default_participants)) > 1:
            new_rows = [
                Selection(group=target_group, path=path, participant=pid)
                for path, pid in zip(path_objs, default_participants)
            ]
        else:
            new_rows = [
                Selection(group=target_group, path=path, participant=participant)
                for path in path_objs
            ]
        selections.extend(
            new_rows
        )
        selections[:] = dedupe_selections(selections)
        included_keys.update(selection_key(sel) for sel in new_rows)
        refresh()

    def edit_participant(group: str | None = None) -> None:
        picked = selected_indices(group)
        if not picked:
            if messagebox is not None:
                messagebox.showinfo("No row selected", "Select one or more rows first.")
            return

        current = ""
        first_idx = picked[0]
        if 0 <= first_idx < len(selections):
            current = selections[first_idx].participant

        win = tk.Toplevel(root)
        win.title("Edit participant ID")
        win.geometry("+140+140")
        win.resizable(False, False)
        win.transient(root)
        tk.Label(win, text="Participant ID:").grid(row=0, column=0, padx=12, pady=(12, 4), sticky="w")
        var = tk.StringVar(value=current)
        entry = tk.Entry(win, textvariable=var, width=28)
        entry.grid(row=1, column=0, columnspan=2, padx=12, pady=4, sticky="ew")
        entry.focus_set()

        def apply() -> None:
            participant = var.get().strip()
            for idx in picked:
                if 0 <= idx < len(selections):
                    old = selections[idx]
                    selections[idx] = Selection(
                        group=old.group,
                        path=old.path,
                        participant=participant,
                        included=old.included,
                    )
            try:
                win.attributes("-topmost", False)
            except Exception:
                pass
            win.destroy()
            refresh()

        def cancel_edit() -> None:
            try:
                win.attributes("-topmost", False)
            except Exception:
                pass
            win.destroy()

        tk.Button(win, text="Cancel", command=cancel_edit).grid(row=2, column=0, padx=12, pady=12, sticky="w")
        tk.Button(win, text="Apply", command=apply).grid(row=2, column=1, padx=12, pady=12, sticky="e")
        win.bind("<Return>", lambda _event: apply())
        win.protocol("WM_DELETE_WINDOW", cancel_edit)
        keep_window_on_top(win, root, modal=True)
        win.wait_window()

    def remove_selected(group: str | None = None) -> None:
        idxs = sorted(selected_indices(group), reverse=True)
        for idx in idxs:
            if 0 <= idx < len(selections):
                included_keys.discard(selection_key(selections[idx]))
                del selections[idx]
        refresh()

    def clear_all() -> None:
        selections.clear()
        included_keys.clear()
        refresh()

    def use_all_for_plot() -> None:
        included_keys.clear()
        included_keys.update(selection_key(sel) for sel in selections)
        refresh()

    def skip_all_for_plot() -> None:
        included_keys.clear()
        refresh()

    def apply_inclusion_state(items: Iterable[Selection]) -> list[Selection]:
        return [
            Selection(
                group=sel.group,
                path=sel.path,
                participant=sel.participant,
                included=selection_key(sel) in included_keys,
            )
            for sel in items
        ]

    def load_selection_from_file() -> None:
        path = filedialog.askopenfilename(
            parent=root,
            title="Load selection JSON",
            initialdir=str(cached_dir("selection_dir", SCRIPT_DIR)),
            initialfile=SELECTION_CACHE.name,
            filetypes=[("Selection JSON", "*.json"), ("All files", "*.*")],
        )
        if not path:
            return
        remember_dir("selection_dir", path)
        root.config(cursor="watch")
        root.update_idletasks()
        loaded = load_selection_file(Path(path))
        root.config(cursor="")
        root.update_idletasks()
        if not loaded:
            if messagebox is not None:
                messagebox.showwarning("No selection loaded", "The selected file did not contain valid trajectory selections.")
            return
        if selections and messagebox is not None:
            ok = messagebox.askyesno(
                "Replace current selection?",
                "Loading this file will replace the current selection in the window. Continue?",
            )
            if not ok:
                return
        selections[:] = loaded
        included_keys.clear()
        included_keys.update(selection_key(sel) for sel in selections if sel.included)
        refresh()
        print(f"Loaded {len(selections)} file(s) from {path}")

    def save_selection_as_file() -> None:
        current = fill_default_participants(apply_inclusion_state(selections))
        if not current:
            if messagebox is not None:
                messagebox.showwarning("No files", "There are no selected files to save.")
            return
        path = filedialog.asksaveasfilename(
            parent=root,
            title="Save selection as",
            initialdir=str(cached_dir("selection_dir", SCRIPT_DIR)),
            initialfile=_selection_save_default_name(),
            defaultextension=".json",
            filetypes=[("Selection JSON", "*.json"), ("All files", "*.*")],
        )
        if not path:
            return
        remember_dir("selection_dir", path)
        save_selection_file(Path(path), current)
        selections[:] = current
        included_keys.clear()
        included_keys.update(selection_key(sel) for sel in selections if sel.included)
        refresh()
        if messagebox is not None:
            messagebox.showinfo("Selection saved", f"Saved {len(current)} file(s) to:\n{path}")

    def view_participant_results() -> None:
        current = [s for s in fill_default_participants(apply_inclusion_state(selections)) if s.path.exists()]
        if not current:
            if messagebox is not None:
                messagebox.showwarning("No files", "Add at least one existing trajectory JSON file.")
            return
        root.config(cursor="watch")
        root.update_idletasks()
        rows = []
        try:
            for sel in current:
                rows.append(compute_metrics(sel))
        except Exception as exc:
            if messagebox is not None:
                messagebox.showerror("Could not compute results", str(exc))
            return
        finally:
            root.config(cursor="")
            root.update_idletasks()
        selections[:] = current
        included_keys.clear()
        included_keys.update(selection_key(sel) for sel in selections if sel.included)
        refresh()
        show_participant_results_window(root, average_trials_by_participant(rows))

    def continue_with_selection() -> None:
        filled = fill_default_participants(apply_inclusion_state(selections))
        valid = [s for s in filled if s.path.exists()]
        if not valid:
            if messagebox is not None:
                messagebox.showwarning("No files", "Add at least one existing trajectory JSON file.")
            return
        chosen = [
            sel for sel in valid
            if sel.included
        ]
        if not chosen:
            if messagebox is not None:
                messagebox.showwarning("No data selected", "Mark at least one valid row as USE for the final plot.")
            return
        selections[:] = filled
        refresh()
        state["result"] = {
            "all": list(filled),
            "included": list(chosen),
            "screen_size": screen_size_px(root),
        }
        root.destroy()

    def cancel() -> None:
        state["result"] = None
        root.destroy()

    for col, group in enumerate(GROUPS):
        controls = tk.Frame(root)
        controls.grid(row=2, column=col, sticky="ew", padx=10, pady=(0, 8))
        tk.Button(controls, text=f"Add {GROUP_LABELS[group]}", command=lambda g=group: add_files(g)).pack(side="left", padx=(0, 4))
        tk.Button(controls, text="Edit participant ID", command=lambda g=group: edit_participant(g)).pack(side="left", padx=4)
        tk.Button(controls, text="Remove selected", command=lambda g=group: remove_selected(g)).pack(side="right", padx=(4, 0))

    bottom_bar = tk.Frame(root)
    bottom_bar.grid(row=3, column=0, columnspan=2, sticky="ew", padx=10, pady=(0, 10))
    tk.Button(bottom_bar, text="Clear all", command=clear_all).pack(side="left", padx=(0, 4))
    tk.Button(bottom_bar, text="Use all", command=use_all_for_plot).pack(side="left", padx=4)
    tk.Button(bottom_bar, text="Skip all", command=skip_all_for_plot).pack(side="left", padx=4)
    tk.Button(bottom_bar, text="Load selection...", command=load_selection_from_file).pack(side="left", padx=4)
    tk.Button(bottom_bar, text="Save selection as...", command=save_selection_as_file).pack(side="left", padx=4)
    tk.Button(bottom_bar, text="Participant results...", command=view_participant_results).pack(side="left", padx=4)
    tk.Button(bottom_bar, text="Skip/cancel", command=cancel).pack(side="right", padx=(4, 0))
    tk.Button(bottom_bar, text="Calculate and plot", command=continue_with_selection).pack(side="right", padx=4)

    root.rowconfigure(1, weight=1)
    for col in range(2):
        root.columnconfigure(col, weight=1)
    root.protocol("WM_DELETE_WINDOW", cancel)
    refresh()
    root.mainloop()
    return state["result"]


def center_series(log: dict) -> tuple[np.ndarray, np.ndarray]:
    times, points = plot_run._swarm_center_series(log)
    if len(times) == 0:
        return times, points
    order = np.argsort(times)
    times = times[order]
    points = points[order]
    return times - times[0], points


def path_length(points: np.ndarray) -> float:
    if len(points) < 2:
        return float("nan")
    deltas = np.diff(points, axis=0)
    return float(np.sum(np.linalg.norm(deltas, axis=1)))


def normalized_derivative_energy(
    times: np.ndarray,
    values: np.ndarray,
    scale: float,
    derivative_order: int,
    sample_hz: float = 20.0,
) -> float:
    """Dimensionless squared-derivative energy for a scalar signal."""
    values = np.asarray(values, dtype=float).reshape(-1)
    min_samples = derivative_order + 2
    if (
        derivative_order < 1
        or len(times) < min_samples
        or len(values) < min_samples
        or not np.isfinite(scale)
        or scale <= 0
    ):
        return float("nan")

    times = np.asarray(times, dtype=float).reshape(-1)
    finite = np.isfinite(times) & np.isfinite(values)
    times = np.asarray(times[finite], dtype=float)
    values = np.asarray(values[finite], dtype=float)
    if len(times) < min_samples:
        return float("nan")

    order = np.argsort(times)
    times = times[order]
    values = values[order]
    unique = np.concatenate(([True], np.diff(times) > 1e-9))
    times = times[unique]
    values = values[unique]
    duration = float(times[-1] - times[0])
    if len(times) < min_samples or duration <= 0:
        return float("nan")

    sample_count = max(min_samples + 2, int(np.ceil(duration * sample_hz)) + 1)
    uniform_times = np.linspace(times[0], times[-1], sample_count)
    uniform_values = np.interp(uniform_times, times, values)

    # Suppress frame-level noise before numerical differentiation.
    window = max(3, int(round(0.25 * sample_hz)))
    if window % 2 == 0:
        window += 1
    if sample_count - window + 1 >= min_samples + 2:
        kernel = np.ones(window, dtype=float) / window
        uniform_values = np.convolve(uniform_values, kernel, mode="valid")
        half_window = window // 2
        uniform_times = uniform_times[half_window:-half_window]

    dt = float(uniform_times[1] - uniform_times[0])
    derivative = uniform_values
    for _ in range(derivative_order):
        derivative = np.gradient(derivative, dt, edge_order=2)
    trim = min(derivative_order, max((len(derivative) - 3) // 2, 0))
    if trim:
        derivative = derivative[trim:-trim]
    if len(derivative) == 0:
        return float("nan")

    integrated_squared_derivative = float(np.trapezoid(derivative * derivative, dx=dt))
    value = (duration ** (2 * derivative_order - 1) / scale ** 2) * integrated_squared_derivative
    return value if np.isfinite(value) else float("nan")


def normalized_acceleration(
    times: np.ndarray,
    values: np.ndarray,
    scale: float,
    sample_hz: float = 20.0,
) -> float:
    """Dimensionless state acceleration cost T^3/S^2 integral(q_ddot^2 dt)."""
    return normalized_derivative_energy(times, values, scale, derivative_order=2, sample_hz=sample_hz)


def normalized_command_rate(
    times: np.ndarray,
    values: np.ndarray,
    scale: float,
    sample_hz: float = 20.0,
) -> float:
    """Dimensionless command roughness T/S^2 integral(u_dot^2 dt)."""
    return normalized_derivative_energy(times, values, scale, derivative_order=1, sample_hz=sample_hz)


def spread_series(log: dict) -> tuple[np.ndarray, np.ndarray]:
    rows = [
        frame for frame in (log.get("swarmFrames") or [])
        if isinstance(frame, dict)
        and isinstance(frame.get("t"), (int, float))
        and isinstance(frame.get("spreadCur"), (int, float))
    ]
    if not rows:
        return np.array([], dtype=float), np.array([], dtype=float)
    rows.sort(key=lambda frame: float(frame["t"]))
    times = np.array([float(frame["t"]) for frame in rows], dtype=float)
    values = np.array([float(frame["spreadCur"]) for frame in rows], dtype=float)
    return times - times[0], values


def view_angle_series(log: dict) -> tuple[np.ndarray, np.ndarray]:
    rows = [
        frame for frame in (log.get("inputs") or [])
        if isinstance(frame, dict)
        and isinstance(frame.get("t"), (int, float))
        and isinstance(frame.get("fr"), (int, float))
    ]
    if len(rows) < 2:
        return np.array([], dtype=float), np.array([], dtype=float)
    rows.sort(key=lambda frame: float(frame["t"]))
    times = np.array([float(frame["t"]) for frame in rows], dtype=float)
    angular_velocity = np.array([float(frame["fr"]) for frame in rows], dtype=float)
    angular_velocity *= CAMERA_ROTATION_SPEED_RAD_S
    finite = np.isfinite(times) & np.isfinite(angular_velocity)
    times = times[finite]
    angular_velocity = angular_velocity[finite]
    if len(times) < 2:
        return np.array([], dtype=float), np.array([], dtype=float)
    unique = np.concatenate(([True], np.diff(times) > 1e-9))
    times = times[unique]
    angular_velocity = angular_velocity[unique]
    if len(times) < 2:
        return np.array([], dtype=float), np.array([], dtype=float)
    dt = np.diff(times)
    angle_steps = 0.5 * (angular_velocity[:-1] + angular_velocity[1:]) * dt
    angles = np.concatenate(([0.0], np.cumsum(angle_steps)))
    return times - times[0], angles


def run_realtime_bounds(log: dict) -> tuple[float, float] | None:
    trials = [
        trial for trial in (log.get("trials") or [])
        if isinstance(trial, dict)
        and str(trial.get("label", "")).strip().lower() == "run"
        and isinstance(trial.get("startRealtime"), (int, float))
        and isinstance(trial.get("endRealtime"), (int, float))
    ]
    if not trials:
        return None
    start = float(trials[0]["startRealtime"])
    end = float(trials[0]["endRealtime"])
    return (start, end) if np.isfinite(start) and np.isfinite(end) and end > start else None


def control_command_series(log: dict) -> tuple[np.ndarray, dict[str, np.ndarray]]:
    rows = [
        frame for frame in (log.get("inputs") or [])
        if isinstance(frame, dict) and isinstance(frame.get("t"), (int, float))
    ]
    if not rows:
        return np.array([], dtype=float), {}
    rows.sort(key=lambda frame: float(frame["t"]))
    bounds = run_realtime_bounds(log)
    if bounds is not None:
        start, end = bounds
        rows = [frame for frame in rows if start <= float(frame["t"]) <= end]
    if not rows:
        return np.array([], dtype=float), {}

    times = np.array([float(frame["t"]) for frame in rows], dtype=float)
    commands = {
        "x": np.array([float(frame.get("fmx", np.nan)) for frame in rows], dtype=float),
        "y": np.array([float(frame.get("fmy", np.nan)) for frame in rows], dtype=float),
        "z": np.array([float(frame.get("fmz", np.nan)) for frame in rows], dtype=float),
        "spread": np.array([float(frame.get("fs", np.nan)) for frame in rows], dtype=float),
        "view": np.array([float(frame.get("fr", np.nan)) for frame in rows], dtype=float),
    }

    spread_min = [
        float(frame["spreadMin"])
        for frame in (log.get("swarmFrames") or [])
        if isinstance(frame, dict)
        and isinstance(frame.get("spreadMin"), (int, float))
        and np.isfinite(float(frame["spreadMin"]))
    ]
    if spread_min:
        commands["spread"][commands["spread"] < min(spread_min) - 1e-6] = np.nan
    return times - times[0], commands


def run_duration(log: dict, rel_times: np.ndarray) -> float:
    elapsed = log.get("elapsedTime")
    if isinstance(elapsed, (int, float)) and float(elapsed) > 0:
        return float(elapsed)
    if len(rel_times) > 0:
        return float(rel_times[-1] - rel_times[0])
    return float("nan")


def compute_metrics(sel: Selection) -> dict:
    log = load_json(sel.path)
    participant = sel.participant or str(log.get("pid", "")).strip()
    if is_placeholder_participant(participant):
        participant = "P0"
    times, points = center_series(log)
    duration = run_duration(log, times)
    length = path_length(points)
    speed = length / duration if np.isfinite(length) and np.isfinite(duration) and duration > 0 else float("nan")
    acceleration_components = {
        f"normalized_acceleration_{axis}": normalized_acceleration(
            times,
            points[:, idx] if len(points) else np.array([], dtype=float),
            NORMALIZATION_SCALES[axis],
        )
        for idx, axis in enumerate(("x", "y", "z"))
    }
    spread_times, spread_values = spread_series(log)
    acceleration_components["normalized_acceleration_spread"] = normalized_acceleration(
        spread_times,
        spread_values,
        NORMALIZATION_SCALES["spread"],
    )
    view_times, view_angles = view_angle_series(log)
    acceleration_components["normalized_acceleration_view"] = normalized_acceleration(
        view_times,
        view_angles,
        NORMALIZATION_SCALES["view"],
    )
    component_values = np.array(list(acceleration_components.values()), dtype=float)
    acceleration_5dof = (
        float(np.mean(component_values))
        if len(component_values) == 5 and np.all(np.isfinite(component_values))
        else float("nan")
    )
    command_times, commands = control_command_series(log)
    command_components = {
        f"command_rate_{axis}": normalized_command_rate(
            command_times,
            commands.get(axis, np.array([], dtype=float)),
            COMMAND_NORMALIZATION_SCALES[axis],
        )
        for axis in ("x", "y", "z", "spread", "view")
    }
    command_component_values = np.array(list(command_components.values()), dtype=float)
    command_rate_5dof = (
        float(np.mean(command_component_values))
        if len(command_component_values) == 5 and np.all(np.isfinite(command_component_values))
        else float("nan")
    )

    swarm_frames = log.get("swarmFrames") or []
    n_disc = [float(f.get("nDisc", np.nan)) for f in swarm_frames if isinstance(f, dict)]

    gaps = plot_run.derive_gaps_from_obstacles(log.get("obstacles", []))
    if not gaps:
        course_path = plot_run.autodetect_course_json(sel.path)
        gaps = plot_run.load_course_gaps(course_path)
    gap_devs = plot_run.compute_gap_center_deviations(log, gaps)
    mean_gap_dev = float(np.mean([d["deviation"] for d in gap_devs])) if gap_devs else float("nan")

    total_collectibles = log.get("totalCollectibles", np.nan)
    collected = log.get("collectiblesPickedUp", 0)
    collection_pct = (
        100.0 * float(collected) / float(total_collectibles)
        if isinstance(total_collectibles, (int, float)) and float(total_collectibles) > 0
        else float("nan")
    )

    return {
        "group": sel.group,
        "group_label": GROUP_LABELS[sel.group],
        "path": str(sel.path),
        "file": sel.path.name,
        "trial": trial_label_from_path(sel.path),
        "scene": log.get("scene", ""),
        "participant": participant,
        "json_pid": log.get("pid", ""),
        "duration_s": duration,
        "path_length_m": length,
        "mean_speed_mps": speed,
        "collectibles": float(collected),
        "total_collectibles": float(total_collectibles) if isinstance(total_collectibles, (int, float)) else float("nan"),
        "collection_pct": collection_pct,
        "crash_count": float(log.get("crashCount", len(log.get("crashes", [])))),
        "mean_disconnected": float(np.nanmean(n_disc)) if n_disc else float("nan"),
        **acceleration_components,
        "normalized_acceleration_5dof": acceleration_5dof,
        **command_components,
        "command_rate_5dof": command_rate_5dof,
        "mean_gap_deviation_m": mean_gap_dev,
        "coordination": (lambda z: float(z["coord_norm"]) if z else float("nan"))(plot_run.zhai_coordination(log)),
        "_times": times,
        "_points": points,
        "_log": log,
    }


def average_trials_by_participant(rows: list[dict]) -> list[dict]:
    """Average repeated trials into one row per participant and control group."""
    buckets: dict[tuple[str, str], list[dict]] = {}
    for row in rows:
        participant = str(row.get("participant", "")).strip()
        group = str(row.get("group", "")).strip()
        if not participant or group not in GROUPS:
            continue
        buckets.setdefault((participant, group), []).append(row)

    averaged = []
    for (participant, group), trials in sorted(
        buckets.items(),
        key=lambda item: (item[0][0], GROUPS.index(item[0][1])),
    ):
        row = {
            "group": group,
            "group_label": GROUP_LABELS[group],
            "participant": participant,
            "trial_count": len(trials),
            "files": ";".join(str(trial.get("file", "")) for trial in trials),
            "paths": ";".join(str(trial.get("path", "")) for trial in trials),
        }
        candidate_keys = list(dict.fromkeys(
            key
            for trial in trials
            for key in trial
            if not key.startswith("_")
            and key not in {
                "group",
                "group_label",
                "participant",
                "file",
                "path",
                "scene",
                "json_pid",
                "files",
                "paths",
            }
        ))
        for key in candidate_keys:
            values = []
            for trial in trials:
                try:
                    value = float(trial.get(key, np.nan))
                except (TypeError, ValueError):
                    continue
                if np.isfinite(value):
                    values.append(value)
            if values:
                row[key] = float(np.mean(values))
        averaged.append(row)
    return averaged


def learning_curve_rows(rows: list[dict]) -> list[dict]:
    """Return duration deltas as t2 - t1 for each participant and interface."""
    buckets: dict[tuple[str, str], dict[str, list[dict]]] = {}
    for row in rows:
        participant = str(row.get("participant", "")).strip()
        group = str(row.get("group", "")).strip()
        trial = str(row.get("trial", "")).strip().lower()
        if not participant or group not in GROUPS or trial not in {"t1", "t2"}:
            continue
        buckets.setdefault((participant, group), {"t1": [], "t2": []})[trial].append(row)

    learning_rows = []
    for (participant, group), trial_rows in sorted(
        buckets.items(),
        key=lambda item: (item[0][0], GROUPS.index(item[0][1])),
    ):
        t1_values = [
            float(row.get("duration_s", np.nan))
            for row in trial_rows["t1"]
            if np.isfinite(float(row.get("duration_s", np.nan)))
        ]
        t2_values = [
            float(row.get("duration_s", np.nan))
            for row in trial_rows["t2"]
            if np.isfinite(float(row.get("duration_s", np.nan)))
        ]
        if not t1_values or not t2_values:
            continue
        t1_mean = float(np.mean(t1_values))
        t2_mean = float(np.mean(t2_values))
        learning_rows.append({
            "group": group,
            "group_label": GROUP_LABELS[group],
            "participant": participant,
            "t1_duration_s": t1_mean,
            "t2_duration_s": t2_mean,
            "duration_delta_t2_minus_t1_s": t2_mean - t1_mean,
            "t1_trial_count": len(t1_values),
            "t2_trial_count": len(t2_values),
            "t1_files": ";".join(str(row.get("file", "")) for row in trial_rows["t1"]),
            "t2_files": ";".join(str(row.get("file", "")) for row in trial_rows["t2"]),
        })
    return learning_rows


def finite_values(rows: list[dict], group: str, key: str) -> np.ndarray:
    vals = [float(r[key]) for r in rows if r["group"] == group and np.isfinite(float(r.get(key, np.nan)))]
    return np.array(vals, dtype=float)


def summarize(rows: list[dict], metric_keys: list[str]) -> list[dict]:
    summary = []
    for group in GROUPS:
        for key in metric_keys:
            vals = finite_values(rows, group, key)
            summary.append({
                "group": group,
                "metric": key,
                "n": len(vals),
                "mean": float(np.mean(vals)) if len(vals) else float("nan"),
                "std": float(np.std(vals, ddof=1)) if len(vals) > 1 else float("nan"),
                "median": float(np.median(vals)) if len(vals) else float("nan"),
                "min": float(np.min(vals)) if len(vals) else float("nan"),
                "max": float(np.max(vals)) if len(vals) else float("nan"),
            })
    return summary


def compare_groups(rows: list[dict], metric_keys: list[str]) -> list[dict]:
    comparisons = []
    if wilcoxon is None:
        return comparisons
    for key in metric_keys:
        paired = []
        participants = sorted({str(r.get("participant", "")).strip() for r in rows if str(r.get("participant", "")).strip()})
        for participant in participants:
            joy_vals = [
                float(r[key])
                for r in rows
                if r["group"] == "joystick"
                and str(r.get("participant", "")).strip() == participant
                and np.isfinite(float(r.get(key, np.nan)))
            ]
            body_vals = [
                float(r[key])
                for r in rows
                if r["group"] == "body-control"
                and str(r.get("participant", "")).strip() == participant
                and np.isfinite(float(r.get(key, np.nan)))
            ]
            if joy_vals and body_vals:
                paired.append((participant, float(np.mean(joy_vals)), float(np.mean(body_vals))))

        if not paired:
            continue
        a = np.array([p[1] for p in paired], dtype=float)
        b = np.array([p[2] for p in paired], dtype=float)
        try:
            stat, p = wilcoxon(a, b, alternative="two-sided", zero_method="wilcox")
        except Exception:
            continue
        comparisons.append({
            "metric": key,
            "test": "Wilcoxon signed-rank",
            "paired_n": len(paired),
            "participants": ";".join(p[0] for p in paired),
            "joystick_mean": float(np.mean(a)),
            "body_control_mean": float(np.mean(b)),
            "mean_difference_joystick_minus_body": float(np.mean(a - b)),
            "statistic": float(stat),
            "p_value": float(p),
        })
    return comparisons


def p_to_stars(p_value: float) -> str:
    if not np.isfinite(p_value) or p_value >= 0.05:
        return ""
    if p_value < 0.001:
        return "***"
    if p_value < 0.01:
        return "**"
    return "*"


def write_csv(path: Path, rows: list[dict], fieldnames: list[str]) -> None:
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def plot_metric_grid(
    rows: list[dict],
    metric_keys: list[str],
    metric_labels: dict[str, str],
    comparison_rows: list[dict],
    out_path: Path,
    show: bool,
    screen_size: tuple[int, int] | None = None,
    title: str = "Trajectory statistics by control group",
) -> None:
    nrows, ncols = metric_grid_shape(len(metric_keys))
    fig = Figure(figsize=figure_size_for_screen(ncols, nrows, screen_size=screen_size))
    axes = fig.subplots(nrows, ncols)
    axes_arr = np.atleast_1d(axes).ravel()
    rng = np.random.default_rng(42)
    comparison_by_metric = {row["metric"]: row for row in comparison_rows}

    for ax, key in zip(axes_arr, metric_keys):
        data = [finite_values(rows, group, key) for group in GROUPS]
        labels = [GROUP_LABELS[g] for g in GROUPS]
        try:
            ax.boxplot(data, tick_labels=labels, showmeans=True)
        except TypeError:
            ax.boxplot(data, labels=labels, showmeans=True)
        comp = comparison_by_metric.get(key)
        stars = p_to_stars(float(comp["p_value"])) if comp is not None else ""
        if stars:
            finite = np.concatenate([vals for vals in data if len(vals)])
            if len(finite):
                y_min = float(np.min(finite))
                y_max = float(np.max(finite))
                span = max(y_max - y_min, abs(y_max) * 0.08, 1.0)
                y = y_max + 0.12 * span
                h = 0.05 * span
                ax.plot([1, 1, 2, 2], [y, y + h, y + h, y], color="black", linewidth=1.2)
                ax.text(1.5, y + h, stars, ha="center", va="bottom", fontsize=13, fontweight="bold")
                ax.set_ylim(top=y + 0.28 * span)
        ax.set_title(metric_labels[key], fontsize=plt.rcParams["font.size"]-2)
        ax.grid(True, axis="y", alpha=0.25)
        ax.set_xlabel("")

    for ax in axes_arr[len(metric_keys):]:
        ax.axis("off")

    fig.suptitle(title, fontsize=15)
    fig.tight_layout(rect=(0, 0, 1, 0.97))
    fig.savefig(out_path, dpi=150)
    fig.savefig(out_path.with_suffix(".pdf"))
    fig.savefig(out_path.with_suffix(".svg"))
    if show:
        try:
            if not show_figure_window(fig, title, screen_size=screen_size):
                fit_matplotlib_window_to_screen(fig, screen_size=screen_size)
                plt.show()
        finally:
            plt.close(fig)
    else:
        plt.close(fig)


def plot_learning_curve(
    rows: list[dict],
    out_path: Path,
    show: bool,
    screen_size: tuple[int, int] | None = None,
) -> None:
    fig = Figure(figsize=figure_size_for_screen(2, 1, screen_size=screen_size))
    ax = fig.subplots()
    rng = np.random.default_rng(42)
    key = "duration_delta_t2_minus_t1_s"
    data = [finite_values(rows, group, key) for group in GROUPS]
    labels = [GROUP_LABELS[group] for group in GROUPS]
    try:
        ax.boxplot(data, tick_labels=labels, showmeans=True, patch_artist=True)
    except TypeError:
        ax.boxplot(data, labels=labels, showmeans=True, patch_artist=True)
    for idx, (group, vals) in enumerate(zip(GROUPS, data), start=1):
        if len(vals) == 0:
            continue
        x = idx + rng.uniform(-0.08, 0.08, size=len(vals))
        ax.scatter(
            x,
            vals,
            color=GROUP_COLORS[group],
            alpha=0.78,
            s=42,
            edgecolor="white",
            linewidth=0.6,
        )
    ax.axhline(0.0, color="black", linewidth=1.0, alpha=0.65)
    ax.set_ylabel("Trial 2 - Trial 1 duration (s)")
    ax.set_title("Learning curve by interface")
    ax.grid(True, axis="y", alpha=0.25)
    fig.tight_layout()
    fig.savefig(out_path, dpi=150)
    fig.savefig(out_path.with_suffix(".pdf"))
    fig.savefig(out_path.with_suffix(".svg"))
    if show:
        try:
            if not show_figure_window(fig, "Learning curve by interface", screen_size=screen_size):
                fit_matplotlib_window_to_screen(fig, screen_size=screen_size)
                plt.show()
        finally:
            plt.close(fig)
    else:
        plt.close(fig)


def _resample_path(times: np.ndarray, points: np.ndarray, n: int = 160) -> np.ndarray | None:
    if len(times) < 2 or len(points) < 2:
        return None
    t0 = float(times[0])
    t1 = float(times[-1])
    if not np.isfinite(t1 - t0) or t1 <= t0:
        return None
    target = np.linspace(t0, t1, n)
    xs = np.interp(target, times, points[:, 0])
    zs = np.interp(target, times, points[:, 2])
    return np.column_stack([xs, zs])


def plot_trajectory_overlay(rows: list[dict], out_path: Path, show: bool) -> None:
    fig, ax = plt.subplots(figsize=(10, 10))

    first_log = next((r["_log"] for r in rows if r.get("_log")), None)
    if first_log is not None:
        for patch in plot_run._obstacle_patches_xz(first_log.get("obstacles", []), facecolor="#888888", alpha=0.12):
            ax.add_patch(patch)

    for group in GROUPS:
        color = GROUP_COLORS[group]
        resampled = []
        for row in rows:
            if row["group"] != group:
                continue
            times = row["_times"]
            points = row["_points"]
            if len(points) < 2:
                continue
            ax.plot(points[:, 0], points[:, 2], color=color, alpha=0.22, linewidth=1.0)
            rp = _resample_path(times, points)
            if rp is not None:
                resampled.append(rp)
        if resampled:
            mean_path = np.mean(np.stack(resampled, axis=0), axis=0)
            ax.plot(
                mean_path[:, 0],
                mean_path[:, 1],
                color=color,
                linewidth=3.0,
                label=f"{GROUP_LABELS[group]} mean (n={len(resampled)})",
            )

    ax.set_aspect("equal", adjustable="datalim")
    ax.set_xlabel("X (m)")
    ax.set_ylabel("Z (m)")
    ax.set_title("Swarm-center trajectories by control group")
    ax.grid(True, alpha=0.25)
    ax.legend(loc="best", framealpha=0.9)
    plot_run._match_unity_scene_top_view(ax)
    fig.tight_layout()
    fig.savefig(out_path, dpi=150)
    if show:
        try:
            plt.show()
        finally:
            plt.close(fig)
    else:
        plt.close(fig)


def printable_summary(summary_rows: list[dict], metric_keys: list[str], metric_labels: dict[str, str]) -> str:
    lines = []
    for key in metric_keys:
        lines.append(metric_labels[key])
        for group in GROUPS:
            row = next(r for r in summary_rows if r["group"] == group and r["metric"] == key)
            mean = row["mean"]
            std = row["std"]
            mean_text = f"{mean:.3g}" if np.isfinite(mean) else "nan"
            std_text = f"{std:.3g}" if np.isfinite(std) else "nan"
            lines.append(f"  {GROUP_LABELS[group]}: n={row['n']}, mean={mean_text}, std={std_text}")
    return "\n".join(lines)


def run_analysis(
    selections: list[Selection],
    show: bool,
    screen_size: tuple[int, int] | None = None,
    progress: AnalysisProgress | None = None,
) -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    selections = [s for s in fill_default_participants(selections) if s.path.exists()]
    if not selections:
        raise SystemExit("No existing trajectory files selected.")

    rows = []
    for i, sel in enumerate(selections, start=1):
        if progress is not None:
            progress.update(
                i - 1,
                len(selections),
                f"Processing {i}/{len(selections)}: {sel.path.name}",
            )
        print(f"[{i}/{len(selections)}] Processing {GROUP_LABELS[sel.group]}: {sel.path}")
        try:
            rows.append(compute_metrics(sel))
        except Exception as exc:
            print(f"  Skipped {sel.path}: {exc}")
        if progress is not None:
            progress.update(i, len(selections), f"Finished {i}/{len(selections)}: {sel.path.name}")

    if not rows:
        raise SystemExit("No trajectory files could be processed.")

    public_rows = [{k: v for k, v in row.items() if not k.startswith("_")} for row in rows]
    learning_rows = learning_curve_rows(rows)
    averaged_rows = average_trials_by_participant(rows)
    if not averaged_rows:
        raise SystemExit("No participant-level averages could be calculated.")
    stats_metric_keys = list(dict.fromkeys([
        *METRIC_KEYS,
        *ACCELERATION_METRIC_KEYS,
        *COMMAND_RATE_METRIC_KEYS,
    ]))
    summary_rows = summarize(averaged_rows, stats_metric_keys)
    comparison_rows = compare_groups(averaged_rows, stats_metric_keys)

    metrics_csv = OUT_DIR / "group_run_metrics.csv"
    averaged_metrics_csv = OUT_DIR / "participant_averaged_metrics.csv"
    summary_csv = OUT_DIR / "group_summary_stats.csv"
    comparison_csv = OUT_DIR / "group_comparisons.csv"
    learning_csv = OUT_DIR / "learning_curve_duration_delta.csv"
    metric_plot = OUT_DIR / "group_metric_boxplots.png"
    acceleration_plot = OUT_DIR / "group_5dof_acceleration.png"
    command_rate_plot = OUT_DIR / "group_5dof_command_rate.png"
    learning_plot = OUT_DIR / "learning_curve_duration_delta.png"
    metric_plot_pdf = metric_plot.with_suffix(".pdf")
    metric_plot_svg = metric_plot.with_suffix(".svg")

    write_csv(metrics_csv, public_rows, list(public_rows[0].keys()))
    write_csv(averaged_metrics_csv, averaged_rows, list(averaged_rows[0].keys()))
    if learning_rows:
        write_csv(
            learning_csv,
            learning_rows,
            [
                "group",
                "group_label",
                "participant",
                "t1_duration_s",
                "t2_duration_s",
                "duration_delta_t2_minus_t1_s",
                "t1_trial_count",
                "t2_trial_count",
                "t1_files",
                "t2_files",
            ],
        )
    write_csv(summary_csv, summary_rows, ["group", "metric", "n", "mean", "std", "median", "min", "max"])
    if comparison_rows:
        write_csv(
            comparison_csv,
            comparison_rows,
            [
                "metric",
                "test",
                "paired_n",
                "participants",
                "joystick_mean",
                "body_control_mean",
                "mean_difference_joystick_minus_body",
                "statistic",
                "p_value",
            ],
        )

    if progress is not None:
        progress.update(len(selections), len(selections), "Creating and saving plots...")
        progress.close()

    plot_metric_grid(
        averaged_rows,
        METRIC_KEYS,
        METRIC_LABELS,
        comparison_rows,
        metric_plot,
        show=show,
        screen_size=screen_size,
    )
    plot_metric_grid(
        averaged_rows,
        ACCELERATION_METRIC_KEYS,
        METRIC_LABELS,
        comparison_rows,
        acceleration_plot,
        show=show,
        screen_size=screen_size,
        title="Normalized acceleration across five degrees of freedom",
    )
    plot_metric_grid(
        averaged_rows,
        COMMAND_RATE_METRIC_KEYS,
        METRIC_LABELS,
        comparison_rows,
        command_rate_plot,
        show=show,
        screen_size=screen_size,
        title="Control-command rate smoothness across five degrees of freedom",
    )
    if learning_rows:
        plot_learning_curve(
            learning_rows,
            learning_plot,
            show=show,
            screen_size=screen_size,
        )
    print("\nSummary:")
    print(printable_summary(summary_rows, METRIC_KEYS, METRIC_LABELS))
    if comparison_rows:
        print(f"\nWrote Wilcoxon signed-rank group comparisons to {comparison_csv}")
    elif wilcoxon is None:
        print("\nSciPy is not installed; skipped Wilcoxon signed-rank group comparisons.")
    else:
        print("\nNo paired joystick/body-control participant data found; skipped Wilcoxon signed-rank comparisons.")
    print(f"\nWrote run metrics to {metrics_csv}")
    print(f"Wrote participant-averaged metrics to {averaged_metrics_csv}")
    if learning_rows:
        print(f"Wrote learning-curve duration deltas to {learning_csv}")
    print(f"Wrote summary stats to {summary_csv}")
    print(f"Wrote plot to {metric_plot}")
    print(f"Wrote vector plots to {metric_plot_pdf} and {metric_plot_svg}")
    print(f"Wrote 5-DOF acceleration plots to {acceleration_plot}, "
          f"{acceleration_plot.with_suffix('.pdf')}, and {acceleration_plot.with_suffix('.svg')}")
    print(f"Wrote 5-DOF command-rate plots to {command_rate_plot}, "
          f"{command_rate_plot.with_suffix('.pdf')}, and "
          f"{command_rate_plot.with_suffix('.svg')}")
    if learning_rows:
        print(f"Wrote learning-curve plots to {learning_plot}, "
              f"{learning_plot.with_suffix('.pdf')}, and "
              f"{learning_plot.with_suffix('.svg')}")


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--joystick", type=Path, nargs="*", default=[], help="Joystick trajectory JSON files.")
    p.add_argument("--body-control", type=Path, nargs="*", default=[], help="Body-control trajectory JSON files.")
    p.add_argument("--joystick-participant", default="", help="Participant ID to assign to all --joystick files.")
    p.add_argument("--body-control-participant", default="", help="Participant ID to assign to all --body-control files.")
    p.add_argument("--selection-file", type=Path, default=None,
                   help=f"Load selections from a JSON file (default cache is {SELECTION_CACHE}).")
    p.add_argument("--save-selection-as", type=Path, default=None,
                   help=f"Save the final selection to this JSON file under {SCRIPT_DIR} or an absolute path.")
    p.add_argument("--no-gui", action="store_true", help="Do not open the Tk file-selection window.")
    p.add_argument("--show", action="store_true", help="Show plots interactively after saving them.")
    p.add_argument("--no-cache", action="store_true", help="Do not load/save the selected-file cache.")
    return p.parse_args()


def main() -> None:
    args = parse_args()
    selections = [
        *(Selection("joystick", p, args.joystick_participant.strip()) for p in args.joystick),
        *(Selection("body-control", p, args.body_control_participant.strip()) for p in args.body_control),
    ]

    if args.selection_file is not None:
        selection_path = args.selection_file if args.selection_file.is_absolute() else SCRIPT_DIR / args.selection_file
        selections = [*load_selection_file(selection_path), *selections]
    elif not args.no_cache:
        selections = [*load_selection_cache(), *selections]
    selections = fill_default_participants(selections)
    analysis_selections = [sel for sel in selections if sel.included]
    plot_screen_size = None
    show_plot = args.show

    if not args.no_gui:
        gui_result = select_files_gui(selections)
        if gui_result is None:
            print("Selection skipped/canceled; nothing to plot.")
            return
        selections = gui_result["all"]
        analysis_selections = gui_result["included"]
        plot_screen_size = gui_result.get("screen_size")
        show_plot = True
    else:
        analysis_selections = [sel for sel in selections if sel.included]

    if not args.no_cache:
        selections = fill_default_participants(selections)
        save_selection_cache(selections)
    if args.save_selection_as is not None:
        save_path = args.save_selection_as if args.save_selection_as.is_absolute() else SCRIPT_DIR / args.save_selection_as
        save_selection_file(save_path, fill_default_participants(selections))

    print_participants_by_start_interface(analysis_selections)

    progress = AnalysisProgress(len(analysis_selections), plot_screen_size) if not args.no_gui else None
    try:
        run_analysis(
            analysis_selections,
            show=show_plot,
            screen_size=plot_screen_size,
            progress=progress,
        )
    finally:
        if progress is not None:
            progress.close()


if __name__ == "__main__":
    main()
