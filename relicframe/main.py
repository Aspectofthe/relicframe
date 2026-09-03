"""
main.py
Load a relic CSV, fetch live Warframe Market prices (rewards + relic packs,
online AND offline, symmetrically), and find the best relics to buy.

Core concepts:
  - Every relic gets a category badge based on ONE shared calculation path
    (relic_data.classify_category), applied identically to both the online
    and offline channel, then combined - never two separate code paths that
    can drift out of sync:
        green  - Guaranteed Profit: worst-case profit > 0
        yellow - Expected Profit Only: positive on average, but a real
                 possible loss exists
        red    - Guaranteed Loss: negative even on average
        white  - Unknown Price: not enough market data to judge at all
  - "Unknown" is never silently treated as "free" or "zero" - it's its own
    state, always ranked below anything actually verified.
  - Prices are always the true cheapest listing (after normalizing bulk
    perTrade pricing), never an average. Unusual prices are flagged, not
    deleted - a single listing is always shown, just marked unconfirmed.

Run with:  python main.py
"""

import os
import threading
import time
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

import wfm_api
import item_catalog_cache
import relic_row
import ranking
import filtering
from relic_data import (
    load_relics_from_csv, all_reward_names, REFINEMENTS,
    classify_category, combine_categories, compute_risk_score,
    find_relics_for_reward, cheapest_combination_cost, chance_of_at_least_one,
)

ITEM_CATALOG_CACHE_PATH = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), ".cache", "item_catalog.json"
)
DUCAT_MAP_CACHE_PATH = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), ".cache", "ducat_map.json"
)

CATEGORY_LABELS = {
    "green": "\U0001F7E2 Guaranteed Profit",
    "yellow": "\U0001F7E1 Expected Only",
    "red": "\U0001F534 Guaranteed Loss",
    "white": "\u26AA Unknown",
}
CATEGORY_TAGS = {"green": "cat_green", "yellow": "cat_yellow", "red": "cat_red", "white": "cat_white"}

VAULT_LABELS = {True: "\U0001F7E0 Vaulted", False: "\U0001F7E2 Unvaulted", None: "\u26AA Unknown"}

def _column_sort_key(v: str):
    """
    Sort key for clicking a table column header. Numeric-looking values
    ("16.7% \u00b7 6.0 exp. opens", "45", "12.3") sort numerically;
    everything else (item names, "Fallback \u26A0", "-") falls back to
    plain string sort. Module-level (not a closure inside sort_by) so it's
    unit-testable without a live Tk widget.

    Splits FIRST, then strips each resulting token - stripping the whole
    string up front only reaches characters at the very start/end of the
    ENTIRE string, so a value like "16.7% \u00b7 6.0 exp. opens" (the "%"
    sitting in the middle, not at either boundary) would never actually
    get its "%" removed, float() would fail, and the column would
    silently fall back to alphabetical string order instead of numeric -
    sorting "23.1%" before "5.2%" by comparing the leading '2' and '5'
    characters, the wrong order for a percentage column.
    """
    token = v.split(" ")[0].split("\u00b7")[0].strip("%+~\u26A0 ")
    try:
        return (0, float(token))
    except ValueError:
        return (1, v)


RANK_MODES = ranking.RANK_MODES


def _format_age(age_seconds: float | None) -> str:
    if age_seconds is None:
        return "unknown"
    if age_seconds < 60:
        return f"{age_seconds:.0f}s"
    if age_seconds < 3600:
        return f"{age_seconds / 60:.0f}m"
    return f"{age_seconds / 3600:.1f}h"


class RelicToolApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Warframe Relic Value Tool")
        self.geometry("1400x700")

        self.relics = {}
        self.prices = {}
        self.relic_prices = {}
        self.name_to_slug = {}
        self.ducats = {}
        self.relic_prices_refinement = None
        self.row_data = {}  # tree item id -> full computed row dict, for the details panel
        self.row_by_name = {}  # relic_name -> full computed row dict, for the item-search feature
        self._fetching = False
        self._auto_refresh_after_id = None
        self._force_catalog_refresh = False

        self._build_widgets()

    # ---------- UI ----------

    def _build_widgets(self):
        top = ttk.Frame(self, padding=8)
        top.pack(fill="x")

        ttk.Button(top, text="Load Relic CSV", command=self.load_csv).pack(side="left", padx=4)
        ttk.Button(top, text="Auto-Fetch All Relics", command=self.auto_fetch_relics).pack(side="left", padx=4)

        self.refinement_var = tk.StringVar(value="radiant")
        ttk.Label(top, text="Refinement:").pack(side="left", padx=(16, 4))
        ttk.Combobox(
            top, textvariable=self.refinement_var, values=REFINEMENTS,
            state="readonly", width=12
        ).pack(side="left")

        ttk.Label(top, text="Plat/trace:").pack(side="left", padx=(16, 4))
        self.trace_rate_var = tk.StringVar(value="0.15")
        ttk.Entry(top, textvariable=self.trace_rate_var, width=6).pack(side="left")

        ttk.Button(top, text="Fetch Prices & Analyze", command=self.run_analysis).pack(side="left", padx=16)

        self.catalog_status_var = tk.StringVar(value="Item catalog: not loaded yet")
        ttk.Label(top, textvariable=self.catalog_status_var).pack(side="left", padx=(16, 4))
        ttk.Button(
            top, text="Refresh Item Catalog", command=self.refresh_item_catalog
        ).pack(side="left", padx=4)

        ttk.Button(
            top, text="Find Relics for Item...", command=self.open_item_search
        ).pack(side="left", padx=(16, 4))

        self.auto_refresh_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            top, text="Keep auto-refreshing", variable=self.auto_refresh_var,
            command=self._on_auto_refresh_toggle,
        ).pack(side="left", padx=(16, 4))
        ttk.Label(top, text="every").pack(side="left")
        self.refresh_interval_var = tk.StringVar(value="5")
        ttk.Entry(top, textvariable=self.refresh_interval_var, width=4).pack(side="left", padx=2)
        ttk.Label(top, text="min").pack(side="left")

        # --- Filters row ---
        filters = ttk.Frame(self, padding=(8, 0))
        filters.pack(fill="x")

        ttk.Label(filters, text="Vault:").pack(side="left")
        self.vault_filter_var = tk.StringVar(value="All")
        ttk.Combobox(
            filters, textvariable=self.vault_filter_var,
            values=["All", "Unvaulted", "Vaulted", "Unknown"],
            state="readonly", width=10
        ).pack(side="left", padx=(2, 12))

        ttk.Label(filters, text="Channel:").pack(side="left")
        self.channel_filter_var = tk.StringVar(value="Both (best of either)")
        ttk.Combobox(
            filters, textvariable=self.channel_filter_var,
            values=["Both (best of either)", "Online only", "Offline only"],
            state="readonly", width=18
        ).pack(side="left", padx=(2, 12))

        ttk.Label(filters, text="Min ROI %:").pack(side="left")
        self.min_roi_var = tk.StringVar(value="")
        ttk.Entry(filters, textvariable=self.min_roi_var, width=6).pack(side="left", padx=(2, 12))

        ttk.Label(filters, text="Max Relic Cost:").pack(side="left")
        self.max_cost_var = tk.StringVar(value="")
        ttk.Entry(filters, textvariable=self.max_cost_var, width=6).pack(side="left", padx=(2, 12))

        ttk.Label(filters, text="Min Best Reward Price:").pack(side="left")
        self.min_reward_var = tk.StringVar(value="")
        ttk.Entry(filters, textvariable=self.min_reward_var, width=6).pack(side="left", padx=(2, 12))

        ttk.Button(filters, text="Apply Filters", command=self.populate_table).pack(side="left", padx=(8, 0))

        # --- Ranking row ---
        ranking = ttk.Frame(self, padding=(8, 0))
        ranking.pack(fill="x")

        ttk.Label(ranking, text="Rank by:").pack(side="left")
        self.rank_mode_var = tk.StringVar(value="Best Overall")
        ttk.Combobox(
            ranking, textvariable=self.rank_mode_var, values=RANK_MODES,
            state="readonly", width=18
        ).pack(side="left", padx=(2, 16))

        self.green_only_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            ranking, text="\U0001F7E2 Guaranteed Profit only",
            variable=self.green_only_var, command=self.populate_table
        ).pack(side="left")

        self.status_var = tk.StringVar(value="Load a relic CSV to begin.")
        ttk.Label(self, textvariable=self.status_var, padding=(8, 4)).pack(fill="x")

        self.last_updated_var = tk.StringVar(value="")
        ttk.Label(self, textvariable=self.last_updated_var, padding=(8, 0)).pack(fill="x")

        self.progress = ttk.Progressbar(self, mode="determinate")
        self.progress.pack(fill="x", padx=8, pady=(0, 4))

        # --- Main table (compact) ---
        columns = (
            "vault", "relic", "category", "risk", "best_item", "odds", "expected_value",
            "online_price", "online_qty", "online_match",
            "offline_price", "offline_qty", "offline_match",
        )
        table_frame = ttk.Frame(self)
        table_frame.pack(fill="both", expand=True, padx=8, pady=(4, 4))

        self.tree = ttk.Treeview(table_frame, columns=columns, show="headings", height=16)
        headings = {
            "vault": "Vault",
            "relic": "Relic",
            "category": "Status",
            "risk": "Risk",
            "best_item": "Best Item",
            "odds": "Odds",
            "expected_value": "Exp. Plat/Open",
            "online_price": "Online",
            "online_qty": "Qty",
            "online_match": "Tier Match",
            "offline_price": "Offline",
            "offline_qty": "Qty",
            "offline_match": "Tier Match",
        }
        widths = {
            "vault": 100, "relic": 80, "category": 170, "risk": 60, "best_item": 190, "odds": 150,
            "expected_value": 100, "online_price": 70, "online_qty": 50, "online_match": 90,
            "offline_price": 70, "offline_qty": 50, "offline_match": 90,
        }
        for col in columns:
            self.tree.heading(col, text=headings[col], command=lambda c=col: self.sort_by(c, False))
            self.tree.column(col, width=widths[col], anchor="center")

        self.tree.tag_configure("cat_green", background="#dff5df")
        self.tree.tag_configure("cat_yellow", background="#fff8d8")
        self.tree.tag_configure("cat_red", background="#fdeaea")
        self.tree.tag_configure("cat_white", background="#eeeeee")

        yscroll = ttk.Scrollbar(table_frame, orient="vertical", command=self.tree.yview)
        xscroll = ttk.Scrollbar(table_frame, orient="horizontal", command=self.tree.xview)
        self.tree.configure(yscrollcommand=yscroll.set, xscrollcommand=xscroll.set)
        self.tree.grid(row=0, column=0, sticky="nsew")
        yscroll.grid(row=0, column=1, sticky="ns")
        xscroll.grid(row=1, column=0, sticky="ew")
        table_frame.rowconfigure(0, weight=1)
        table_frame.columnconfigure(0, weight=1)
        self.tree.bind("<<TreeviewSelect>>", self._on_select)

        # --- Details panel ---
        details_frame = ttk.LabelFrame(self, text="Details (click a relic above)", padding=8)
        details_frame.pack(fill="x", padx=8, pady=(0, 8))

        n_row = ttk.Frame(details_frame)
        n_row.pack(fill="x", pady=(0, 6))
        ttk.Label(n_row, text="If I buy N of this relic, N =").pack(side="left")
        self.n_opens_var = tk.StringVar(value="10")
        ttk.Entry(n_row, textvariable=self.n_opens_var, width=6).pack(side="left", padx=4)
        ttk.Button(n_row, text="Calculate", command=self._calculate_n_opens).pack(side="left", padx=4)
        ttk.Label(
            n_row,
            text="(exact probability up to ~120 opens, Monte Carlo estimate beyond that)",
        ).pack(side="left", padx=(4, 0))

        ttk.Button(
            n_row, text="Compare Refinements", command=self._show_refinement_comparison
        ).pack(side="left", padx=(16, 0))

        self.details_text = tk.Text(details_frame, height=15, wrap="word", state="disabled")
        self.details_text.pack(fill="x")
        self._selected_row = None

    # ---------- Actions ----------

    def load_csv(self):
        if self._fetching:
            messagebox.showwarning(
                "Fetch in progress",
                "A price fetch is currently running. Wait for it to finish (or turn off "
                "auto-refresh) before loading a different relic set - switching datasets "
                "mid-fetch could mix results from two different relic lists."
            )
            return
        path = filedialog.askopenfilename(title="Select relic CSV", filetypes=[("CSV files", "*.csv")])
        if not path:
            return
        try:
            self.relics = load_relics_from_csv(path)
        except Exception as e:  # noqa: BLE001
            messagebox.showerror("Failed to load CSV", str(e))
            return
        self.relic_prices = {}
        self.relic_prices_refinement = None
        self.status_var.set(f"Loaded {len(self.relics)} relics. Click 'Fetch Prices & Analyze'.")

    def auto_fetch_relics(self):
        if self._fetching:
            messagebox.showwarning(
                "Fetch in progress",
                "A price fetch is currently running. Wait for it to finish (or turn off "
                "auto-refresh) before loading a different relic set."
            )
            return
        threading.Thread(target=self._auto_fetch_worker, daemon=True).start()

    def _auto_fetch_worker(self):
        self.status_var.set("Downloading full relic drop-table data...")
        try:
            import wfinfo_data
            filtered = wfm_api.get_filtered_items()
        except Exception as e:  # noqa: BLE001
            self.status_var.set(f"Failed to download relic data: {e}")
            return
        relics, vaulted = wfinfo_data.build_relics_from_filtered(filtered)
        self.relics = relics
        vaulted_count = sum(1 for v in vaulted.values() if v)
        self.status_var.set(
            f"Loaded {len(relics)} relics automatically ({vaulted_count} vaulted). "
            f"Click 'Fetch Prices & Analyze'. (For DE's own official numbers instead of "
            f"this community feed, use parse_official_drops.py on a droptables export.)"
        )

    def run_analysis(self):
        if not self.relics:
            messagebox.showwarning("No data", "Load a relic CSV first.")
            return
        if self._fetching:
            # Already running (e.g. auto-refresh fired while a manual fetch was in flight) -
            # let the in-progress one finish rather than overlapping two fetches.
            return
        self._fetching = True
        threading.Thread(target=self._analyze_worker, daemon=True).start()

    def refresh_item_catalog(self):
        """
        Forces a live re-download of the item catalog on the NEXT analysis
        run, bypassing the cache even if it's still fresh - for when the
        person knows new items were just added (e.g. right after a game
        update) and doesn't want to wait out the normal cache window.
        """
        self._force_catalog_refresh = True
        if self.relics and not self._fetching:
            self.run_analysis()
        else:
            self.catalog_status_var.set("Item catalog: will refresh on next fetch")

    def _update_catalog_status_label(self, source: str, age_seconds: float | None):
        if source == "live":
            self.catalog_status_var.set("Item catalog: freshly downloaded")
        elif source == "cache":
            self.catalog_status_var.set(f"Item catalog: cached ({_format_age(age_seconds)} old)")
        elif source == "stale_fallback":
            self.catalog_status_var.set(
                f"Item catalog: OFFLINE - using stale cache ({_format_age(age_seconds)} old)"
            )
        else:
            self.catalog_status_var.set("Item catalog: unknown state")

    def open_item_search(self):
        """
        "Specific Prime part" mode (review item #12) - the question this
        answers isn't "what's the best relic overall" but "I want THIS
        item, what's my cheapest realistic route to it." Works even
        before prices are fetched (drop chance alone is price-independent);
        cost columns simply show "-" until an analysis run has priced the
        relics that came back.
        """
        if not self.relics:
            messagebox.showwarning("No data", "Load a relic CSV first.")
            return

        win = tk.Toplevel(self)
        win.title("Find Relics for a Specific Item")
        win.geometry("820x420")

        top = ttk.Frame(win, padding=8)
        top.pack(fill="x")
        ttk.Label(top, text="Item name (or part of it):").pack(side="left")
        query_var = tk.StringVar()
        entry = ttk.Entry(top, textvariable=query_var, width=32)
        entry.pack(side="left", padx=8)
        entry.focus_set()

        ttk.Label(top, text="Min ROI %:").pack(side="left", padx=(12, 4))
        # Pre-filled from the main table's Min ROI filter so the two stay in
        # sync by default - editable independently here without touching the
        # main table's filter.
        roi_var = tk.StringVar(value=self.min_roi_var.get())
        ttk.Entry(top, textvariable=roi_var, width=6).pack(side="left")

        cols = ("relic", "vault", "reward", "rarity", "chance", "exp_openings",
                "relic_cost", "exp_total_cost", "roi")
        headers = {
            "relic": "Relic", "vault": "Vault", "reward": "Matched Reward",
            "rarity": "Slot", "chance": "Chance", "exp_openings": "Exp. Openings",
            "relic_cost": "Relic Cost", "exp_total_cost": "Exp. Cost / Copy",
            "roi": "Relic's ROI %",
        }
        tree = ttk.Treeview(win, columns=cols, show="headings", height=14)
        for c in cols:
            tree.heading(c, text=headers[c])
            tree.column(c, width=95, anchor="center")
        tree.column("relic", width=110, anchor="w")
        tree.column("reward", width=150, anchor="w")
        tree.pack(fill="both", expand=True, padx=8, pady=(0, 8))

        status_var = tk.StringVar(value="")
        ttk.Label(win, textvariable=status_var).pack(fill="x", padx=8, pady=(0, 8))

        # Keyed by tree item id -> the matched reward's chance_pct, so
        # double-clicking a row can show "chance of >= 1 copy after N
        # opens" (review item #5) for that specific relic/reward pair
        # without recomputing the search.
        row_chance_by_item: dict[str, float] = {}

        def do_search(_event=None):
            self._run_item_search(query_var.get(), roi_var.get(), tree, status_var, row_chance_by_item)

        def on_row_double_click(_event=None):
            selection = tree.selection()
            if not selection:
                return
            item_id = selection[0]
            chance_pct = row_chance_by_item.get(item_id)
            if chance_pct is None:
                return
            relic_name, _vault, reward_name = tree.item(item_id, "values")[:3]
            self._show_at_least_one_table(relic_name, reward_name, chance_pct)

        ttk.Button(top, text="Search", command=do_search).pack(side="left", padx=(12, 0))
        entry.bind("<Return>", do_search)
        tree.bind("<Double-1>", on_row_double_click)
        ttk.Label(
            win, text="Double-click a row to see the chance of getting it within N opens.",
            foreground="#555555",
        ).pack(fill="x", padx=8)

    def _show_at_least_one_table(self, relic_name: str, reward_name: str, chance_pct: float):
        """Review item #5: cumulative 'chance of getting at least one copy
        within N opens' - genuinely different information from the average
        (expected_openings) already shown in the search results table."""
        win = tk.Toplevel(self)
        win.title(f"Odds - {reward_name}")
        win.geometry("360x260")

        ttk.Label(
            win, text=f"{reward_name}\nfrom {relic_name}  ({chance_pct:.2f}% per open)",
            justify="center", font=("", 10, "bold"),
        ).pack(pady=(10, 6))

        tree = ttk.Treeview(win, columns=("n", "chance"), show="headings", height=6)
        tree.heading("n", text="Opens")
        tree.heading("chance", text="Chance of \u2265 1")
        tree.column("n", width=100, anchor="center")
        tree.column("chance", width=140, anchor="center")
        tree.pack(padx=12, pady=(0, 12), fill="x")

        for row in chance_of_at_least_one(chance_pct, [1, 3, 6, 10, 20, 50]):
            tree.insert("", "end", values=(row["n"], f"{row['chance_at_least_one_pct']:.1f}%"))

    def _run_item_search(
        self, query: str, min_roi_str: str, tree: ttk.Treeview, status_var: tk.StringVar,
        row_chance_by_item: dict[str, float],
    ):
        tree.delete(*tree.get_children())
        row_chance_by_item.clear()
        query = query.strip()
        if not query:
            status_var.set("Enter part of an item name and press Search.")
            return

        refinement = self.refinement_var.get()
        results = find_relics_for_reward(query, self.relics, refinement)
        if not results:
            status_var.set(f"No relics found containing an item matching '{query}'.")
            return

        try:
            min_roi = float(min_roi_str) if min_roi_str.strip() else None
        except ValueError:
            min_roi = None

        priced_rows = []
        unpriced_rows = []
        filtered_out_by_roi = 0
        for r in results:
            row = self.row_by_name.get(r["relic_name"])
            # Prefer the cheapest ACTIONABLE cost: online first (buyable right
            # now), offline only as a fallback (real price, but you'd have to
            # wait for that seller) - never silently average the two.
            relic_cost = None
            roi_pct = None
            if row is not None:
                if row["online_cost"] is not None:
                    relic_cost = row["online_cost"]
                    roi_pct = row["online_profit"]["expected_roi_pct"]
                elif row["offline_cost"] is not None:
                    relic_cost = row["offline_cost"]
                    roi_pct = row["offline_profit"]["expected_roi_pct"]

            # Min ROI filter applies to the RELIC's own expected ROI (same
            # metric and same "unknown is never treated as passing" rule as
            # the main table's Min ROI % filter) - not to the searched
            # item's drop chance, which is a different question entirely.
            if min_roi is not None:
                if roi_pct is None or roi_pct < min_roi:
                    filtered_out_by_roi += 1
                    continue

            entry = dict(r)
            entry["relic_cost"] = relic_cost
            entry["roi_pct"] = roi_pct
            if relic_cost is not None and r["expected_openings"] is not None:
                entry["cost_per_expected_copy"] = relic_cost * r["expected_openings"]
                # cost_per_expected_copy IS the expected total cost to get one
                # copy on average - same number, kept as one field so the two
                # display columns can never silently drift apart.
                priced_rows.append(entry)
            else:
                unpriced_rows.append(entry)

        priced_rows.sort(key=lambda e: e["cost_per_expected_copy"])
        vault_tag = {True: "Vaulted", False: "Unvaulted", None: "Unknown"}

        for entry in priced_rows + unpriced_rows:
            chance_str = f"{entry['chance_pct']:.2f}%"
            opens_str = f"{entry['expected_openings']:.1f}" if entry["expected_openings"] else "-"
            cost_str = f"{entry['relic_cost']:.1f}p" if entry["relic_cost"] is not None else "-"
            per_copy_str = (
                f"{entry['cost_per_expected_copy']:.1f}p" if "cost_per_expected_copy" in entry else "-"
            )
            roi_str = f"{entry['roi_pct']:.0f}%" if entry["roi_pct"] is not None else "-"
            item_id = tree.insert("", "end", values=(
                entry["relic_name"], vault_tag[entry["vaulted"]], entry["reward_name"],
                entry["rarity"], chance_str, opens_str, cost_str, per_copy_str, roi_str,
            ))
            row_chance_by_item[item_id] = entry["chance_pct"]

        note = ""
        if unpriced_rows:
            note += (
                f"  ({len(unpriced_rows)} relic(s) shown without cost - run "
                f"'Fetch Prices & Analyze' first for full pricing.)"
            )
        if min_roi is not None and filtered_out_by_roi:
            note += f"  ({filtered_out_by_roi} relic(s) hidden below {min_roi:.0f}% ROI.)"
        shown = len(priced_rows) + len(unpriced_rows)
        status_var.set(
            f"{shown} match(es) shown at {refinement}, sorted by cheapest expected cost first.{note}"
        )

    def _on_auto_refresh_toggle(self):
        if self.auto_refresh_var.get():
            if not self.relics:
                messagebox.showwarning("No data", "Load a relic CSV first.")
                self.auto_refresh_var.set(False)
                return
            # Kick off immediately rather than waiting a full interval for the first run.
            if not self._fetching:
                self.run_analysis()
        else:
            if self._auto_refresh_after_id is not None:
                self.after_cancel(self._auto_refresh_after_id)
                self._auto_refresh_after_id = None

    def _schedule_auto_refresh(self):
        """
        Called once a fetch finishes. If auto-refresh is still on, schedules
        the next one via Tk's .after() - not a blocking loop, so the GUI
        stays responsive between refreshes, and toggling the checkbox off
        at any point (including mid-countdown) cleanly stops it via
        after_cancel in _on_auto_refresh_toggle.
        """
        if not self.auto_refresh_var.get():
            return
        try:
            minutes = float(self.refresh_interval_var.get())
        except ValueError:
            minutes = 5.0
        minutes = max(1.0, minutes)  # floor of 1 min - a full relic sweep can itself take minutes
        ms = int(minutes * 60 * 1000)
        self._auto_refresh_after_id = self.after(ms, self._auto_refresh_tick)
        next_time = time.strftime("%H:%M:%S", time.localtime(time.time() + minutes * 60))
        current = self.last_updated_var.get().split(" | ")[0]
        self.last_updated_var.set(f"{current} | Next auto-refresh: {next_time}")

    def _auto_refresh_tick(self):
        self._auto_refresh_after_id = None
        if not self.auto_refresh_var.get() or not self.relics:
            return
        if self._fetching:
            # Shouldn't normally happen (a fetch running long past its own interval) -
            # just check again shortly rather than stacking fetches.
            self._auto_refresh_after_id = self.after(5000, self._auto_refresh_tick)
            return
        self.run_analysis()

    def _analyze_worker(self):
        success = False
        try:
            success = self._analyze_worker_body()
        finally:
            self._fetching = False
            if success:
                self.last_updated_var.set(f"Last updated: {time.strftime('%H:%M:%S')}")
            # Reschedule regardless of success/failure - a transient network hiccup
            # shouldn't permanently kill the auto-refresh loop, just skip this cycle.
            self._schedule_auto_refresh()

    def _analyze_worker_body(self) -> bool:
        reward_names = all_reward_names(self.relics)
        relic_names = list(self.relics.keys())
        refinement = self.refinement_var.get()

        self.status_var.set("Loading item catalog...")
        try:
            catalog = item_catalog_cache.load_or_fetch(
                ITEM_CATALOG_CACHE_PATH,
                wfm_api.get_all_items,
                force_refresh=self._force_catalog_refresh,
            )
        except Exception as e:  # noqa: BLE001
            self.status_var.set(f"Failed to reach Warframe Market: {e}")
            return False
        finally:
            self._force_catalog_refresh = False  # one-shot - don't force every subsequent run too

        items = catalog["items"]
        self.name_to_slug = wfm_api.build_name_to_slug_map(items)
        id_to_slug = wfm_api.build_id_to_slug_map(items)
        self._update_catalog_status_label(catalog["source"], catalog["age_seconds"])

        # Ducat values are supplementary (Phase 3/4 per the review notes, not
        # core profitability) - a failure here must never abort the whole
        # analysis run. self.ducats simply stays at whatever it was before
        # (empty on first run), so ducat_efficiency() falls back to its
        # normal "no data" behavior rather than crashing anything.
        try:
            ducat_result = item_catalog_cache.load_or_fetch_json(
                DUCAT_MAP_CACHE_PATH, wfm_api.get_ducat_map,
            )
            self.ducats = ducat_result["data"]
        except Exception:  # noqa: BLE001
            pass  # keep previous self.ducats, if any - see comment above

        # One shared bulk snapshot (up to 500 recent online orders) covers
        # as many reward/relic slugs as it can in a single request, so the
        # per-item /top fallback below only runs for whatever it missed -
        # instead of one /top call per reward and per relic every time.
        self.status_var.set("Downloading recent-orders snapshot...")
        try:
            recent_orders = wfm_api.get_recent_orders()
            recent_index = wfm_api.build_recent_sell_index(recent_orders, id_to_slug)
        except Exception:  # noqa: BLE001
            recent_index = {}  # snapshot is a speed optimization, not required for correctness

        self.progress["maximum"] = max(len(reward_names), 1)
        self.progress["value"] = 0

        def reward_progress_cb(done, total):
            self.progress["value"] = done
            self.status_var.set(f"Fetching reward prices... {done}/{total}")

        self.prices = wfm_api.get_price_batch(
            reward_names, self.name_to_slug, reward_progress_cb, recent_index
        )

        self.progress["maximum"] = max(len(relic_names), 1)
        self.progress["value"] = 0

        def relic_progress_cb(done, total):
            self.progress["value"] = done
            self.status_var.set(f"Fetching relic prices (online + offline)... {done}/{total}")

        self.relic_prices = wfm_api.get_relic_price_batch(
            self.name_to_slug, relic_names, refinement, relic_progress_cb, recent_index
        )
        self.relic_prices_refinement = refinement

        unmatched = [n for n in reward_names if self.prices.get(n.lower()) is None]
        no_relic_price = [n for n in relic_names if (self.relic_prices.get(n) or {}).get("online") is None]
        msg = "Done."
        if unmatched:
            msg += f" {len(unmatched)} reward name(s) have no price."
        if no_relic_price:
            msg += f" {len(no_relic_price)} relic(s) have no {refinement} listing online."
        self.status_var.set(msg)

        self.populate_table()
        return True

    # ---------- Computation ----------

    def _compute_channel(self, relic, cost, qty, refinement, trace_rate, prices_snapshot):
        return relic_row.compute_channel(relic, cost, qty, refinement, trace_rate, prices_snapshot)

    def _compute_row(self, relic, refinement, trace_rate):
        # Freeze the exact prices dict this row is built from. self.prices gets
        # REASSIGNED (not mutated) to a brand-new dict on every fetch cycle, so if
        # auto-refresh finishes in the background while a relic's details panel is
        # still open, a later *live* self.prices lookup (e.g. in _render_details)
        # can silently disagree with the numbers already shown for this same row -
        # same reward, two different snapshots, no warning. relic_row.compute_row
        # takes self.prices as an explicit snapshot argument (not a live read), so
        # every downstream read for this row - table, details, N-open calculator -
        # stays internally consistent even if self.prices moves on afterward.
        return relic_row.compute_row(
            relic, refinement, trace_rate, self.prices, self.relic_prices, self.ducats,
            channel_scope=self.channel_filter_var.get(),
        )

    # ---------- Filtering & ranking ----------

    def _passes_filters(self, row) -> bool:
        try:
            min_roi = float(self.min_roi_var.get()) if self.min_roi_var.get().strip() else None
        except ValueError:
            min_roi = None
        try:
            max_cost = float(self.max_cost_var.get()) if self.max_cost_var.get().strip() else None
        except ValueError:
            max_cost = None
        try:
            min_reward = float(self.min_reward_var.get()) if self.min_reward_var.get().strip() else None
        except ValueError:
            min_reward = None

        return filtering.passes_filters(
            row,
            vault_filter=self.vault_filter_var.get(),
            channel_scope=self.channel_filter_var.get(),
            min_roi=min_roi, max_cost=max_cost, min_reward=min_reward,
            green_only=self.green_only_var.get(),
        )

    def _sort_key_for_mode(self, mode: str):
        return ranking.sort_key_for_mode(mode, channel_scope=self.channel_filter_var.get())

    # ---------- Rendering ----------

    def populate_table(self):
        if not self.relics:
            return
        refinement = self.refinement_var.get()
        try:
            trace_rate = float(self.trace_rate_var.get())
        except ValueError:
            trace_rate = 0.0

        if trace_rate > 5:
            self.status_var.set(
                f"Warning: Plat/trace = {trace_rate} looks very high (traces are usually "
                f"worth well under 1 plat each) - double-check that field."
            )
        elif self.relic_prices_refinement and self.relic_prices_refinement != refinement:
            self.status_var.set(
                f"Note: relic prices were fetched at '{self.relic_prices_refinement}' but the "
                f"dropdown is now '{refinement}' - click 'Fetch Prices & Analyze' again to refresh."
            )

        # Remember which relic (if any) had its details panel open, by name - each row's
        # own item_id gets thrown away and rebuilt below, so the id itself can't survive
        # a refresh, but the relic name can. Used below to keep the details panel in sync
        # with the new snapshot instead of silently showing an old, frozen one forever.
        previously_selected_name = self._selected_row["relic"].relic_name if self._selected_row else None

        self.tree.delete(*self.tree.get_children())
        self.row_data = {}
        self.row_by_name = {}

        rows = [self._compute_row(relic, refinement, trace_rate) for relic in self.relics.values()]
        rows = [r for r in rows if self._passes_filters(r)]
        rows.sort(key=self._sort_key_for_mode(self.rank_mode_var.get()), reverse=True)

        reselect_item_id = None
        for row in rows:
            relic = row["relic"]
            odds = row["odds"]

            vault_str = VAULT_LABELS[relic.vaulted]
            category = row["overall_category"]
            category_str = CATEGORY_LABELS[category]

            best_str = f"{odds['name']} ({odds['price']:.1f}p)" if odds["name"] else "-"
            if odds["expected_openings"]:
                odds_str = f"{odds['chance_pct']:.1f}% \u00b7 {odds['expected_openings']:.1f} exp. opens"
            else:
                odds_str = "-"

            def fmt_channel(cost, qty, zero, matched, outlier):
                if cost is None:
                    return ("0 avail." if zero else "-"), (str(qty) if qty is not None else "-"), "-"
                match_str = "Exact" if matched else "Fallback"
                if outlier:
                    match_str += " \u26A0"
                qty_str = str(qty) if qty is not None else "-"
                return f"{cost:.1f}", qty_str, match_str

            oc_price, oc_qty, oc_match = fmt_channel(
                row["online_cost"], row["online_qty"], row["online_zero"],
                row["online_matched"], row["online_outlier"]
            )
            of_price, of_qty, of_match = fmt_channel(
                row["offline_cost"], row["offline_qty"], row["offline_zero"],
                row["offline_matched"], row["offline_outlier"]
            )

            item_id = self.tree.insert(
                "", "end", tags=(CATEGORY_TAGS[category],),
                values=(
                    vault_str, relic.relic_name, category_str, row["best_risk"], best_str, odds_str,
                    f"{row['online_profit']['expected_value']:.1f}",
                    oc_price, oc_qty, oc_match, of_price, of_qty, of_match,
                )
            )
            self.row_data[item_id] = row
            self.row_by_name[relic.relic_name] = row
            if relic.relic_name == previously_selected_name:
                reselect_item_id = item_id

        # If the relic that was selected before this refresh is still in the (filtered,
        # re-sorted) table, keep it selected and re-render its details from the NEW
        # snapshot - otherwise the panel would keep showing whatever was frozen there
        # before the refresh, with no indication it's now out of date relative to the
        # table around it. If it's gone (filtered out this time), just clear the
        # selection rather than leaving stale details attached to nothing visible.
        if reselect_item_id is not None:
            self.tree.selection_set(reselect_item_id)
            self.tree.see(reselect_item_id)
            self._selected_row = self.row_data[reselect_item_id]
            self._render_details(self._selected_row)
        else:
            self._selected_row = None
            self.details_text.config(state="normal")
            self.details_text.delete("1.0", "end")
            self.details_text.config(state="disabled")

    def _on_select(self, _event):
        selection = self.tree.selection()
        if not selection:
            return
        row = self.row_data.get(selection[0])
        if not row:
            return
        self._selected_row = row
        self._render_details(row)

    def _render_details(self, row, extra_lines=None):
        relic = row["relic"]
        refinement = self.refinement_var.get()
        lines = [f"{relic.relic_name}  ({VAULT_LABELS[relic.vaulted]}, {refinement})", ""]

        for label, cost, qty, matched, outlier, zero, profit, risk in (
            ("ONLINE (buy now)", row["online_cost"], row["online_qty"], row["online_matched"],
             row["online_outlier"], row["online_zero"], row["online_profit"], row["online_risk"]),
            ("OFFLINE (seller not online - not immediately buyable, still calculated)",
             row["offline_cost"], row["offline_qty"], row["offline_matched"],
             row["offline_outlier"], row["offline_zero"], row["offline_profit"], row["offline_risk"]),
        ):
            lines.append(label)
            if not profit["price_known"]:
                # Must check profit["price_known"] - the already-corrected state - not raw
                # cost/qty directly, or a 0-quantity listing's raw price leaks through here
                # even though the table (and the profit numbers themselves) correctly
                # excluded it. One source of truth, not two that can drift apart.
                reason = "0 quantity available at cheapest listing" if zero else "no listing found"
                lines.append(f"  Price: - ({reason})")
            else:
                tier_note = "" if matched else f" [FALLBACK - no listing at '{refinement}' specifically, this is another tier's price]"
                outlier_note = " [\u26A0 unusual price - only/atypical listing, verify before buying]" if outlier else ""
                lines.append(f"  Price: {cost:.2f}p  Qty available: {qty if qty is not None else 'unknown'}{tier_note}{outlier_note}")
                lines.append(f"  Expected profit: {profit['expected_profit']:+.2f}p   "
                              f"Worst-case (guaranteed floor): {profit['worst_case_profit']:+.2f}p")
                roi = f"{profit['expected_roi_pct']:.0f}%" if profit['expected_roi_pct'] is not None else "-"
                lines.append(f"  Expected ROI: {roi}   Risk score: {risk}/100 (higher = riskier)")
            lines.append("")

        odds = row["odds"]
        if odds["name"]:
            lines.append(
                f"Best single item: {odds['name']} - {odds['chance_pct']:.2f}% chance "
                f"({odds['expected_openings']:.1f} expected openings; "
                f"{odds['chance_within_10']:.0f}% chance within 10 opens)"
            )
        top = relic.top_rewards(refinement, row["prices_snapshot"], n=6)  # 6 = every slot, full pool breakdown
        if top:
            lines.append("All rewards in this relic, by value:")
            for i, t in enumerate(top, 1):
                lines.append(f"  {i}. {t['name']}  -  {t['price']:.1f}p  ({t['chance_pct']:.2f}% chance)")

        if row["plat_per_trace"] is not None:
            lines.append(f"Plat/trace at {refinement}: {row['plat_per_trace']:.3f}")

        ducat_info = row["ducat_info"]
        if ducat_info["price_known"] and ducat_info["expected_ducats"] > 0:
            lines.append("")
            lines.append("Ducat farming (separate objective from plat profit):")
            lines.append(f"  Expected ducats/open: {ducat_info['expected_ducats']:.1f}")
            if ducat_info["plat_per_ducat"] is not None:
                lines.append(
                    f"  Cost per expected ducat: {ducat_info['plat_per_ducat']:.2f}p "
                    f"({ducat_info['ducats_per_plat']:.2f} ducats/plat)"
                )
            best_ducat = row["best_ducat_reward"]
            if best_ducat:
                lines.append(
                    f"  Best ducat item: {best_ducat['name']} - {best_ducat['ducats']} ducats "
                    f"({best_ducat['chance_pct']:.2f}% chance)"
                )

        if extra_lines:
            lines.append("")
            lines.extend(extra_lines)

        self.details_text.config(state="normal")
        self.details_text.delete("1.0", "end")
        self.details_text.insert("1.0", "\n".join(lines))
        self.details_text.config(state="disabled")

    def _calculate_n_opens(self):
        if self._selected_row is None:
            messagebox.showinfo("No relic selected", "Click a relic in the table first.")
            return
        try:
            n = int(self.n_opens_var.get())
        except ValueError:
            messagebox.showerror("Invalid N", "N must be a whole number.")
            return
        if n <= 0:
            messagebox.showerror("Invalid N", "N must be at least 1.")
            return

        self.status_var.set(f"Pricing the cheapest way to buy {n}...")
        threading.Thread(
            target=self._calculate_n_opens_worker, args=(self._selected_row, n), daemon=True
        ).start()

    def _calculate_n_opens_worker(self, row, n):
        """
        Runs off the UI thread since this makes its own network call (the
        full order book, not just the cached single-cheapest price used
        elsewhere) - the "Best Available Deal" combination pricing needs
        to know EVERY seller's price and quantity to sweep correctly, not
        just what's already sitting in row_data from the last analysis run.
        """
        relic = row["relic"]
        refinement = self.refinement_var.get()
        try:
            trace_rate = float(self.trace_rate_var.get())
        except ValueError:
            trace_rate = 0.0

        extra = [f"=== Buying N={n} of this relic (cheapest real combination of sellers) ==="]
        for label, include_offline, cost, qty, zero in (
            ("Online", False, row["online_cost"], row["online_qty"], row["online_zero"]),
            ("Offline", True, row["offline_cost"], row["offline_qty"], row["offline_zero"]),
        ):
            if cost is None:
                reason = "0 quantity available" if zero else "no listing"
                extra.append(f"{label}: can't calculate - {reason}.")
                continue

            try:
                order_book = wfm_api.get_relic_order_book(
                    self.name_to_slug, relic.relic_name, refinement, include_offline=include_offline,
                )
            except Exception as e:  # noqa: BLE001
                extra.append(f"{label}: couldn't fetch the order book ({e}) - skipping.")
                continue
            if not order_book:
                extra.append(f"{label}: can't calculate - no listing.")
                continue

            combo = cheapest_combination_cost(order_book, n)
            if combo["units_filled"] == 0:
                extra.append(f"{label}: can't calculate - no listing.")
                continue

            result = relic.buy_n_analysis(
                refinement, row["prices_snapshot"], cost, trace_rate, n,
                total_relic_cost_override=combo["total_cost"],
            )
            method = "Monte Carlo estimate" if result["approx"] else "exact"
            fill_note = ""
            if not combo["fully_filled"]:
                # For online, "not fully filled" means "not fillable from the API's
                # top-5 sell orders" (see get_relic_order_book's docstring) - there
                # may be more than 5 online sellers in reality, so this is a lower
                # bound on availability for online, not necessarily the true ceiling.
                cap_note = (
                    " (checked the 5 best online listings only - there may be more sellers)"
                    if not include_offline else ""
                )
                fill_note = (
                    f"  [only {combo['units_filled']}/{n} actually available across all "
                    f"{label.lower()} sellers right now{cap_note} - numbers below are for "
                    f"that many, not {n}]"
                )
            extra.append(
                f"{label} ({method}): {combo['units_filled']} unit(s) across "
                f"{len(combo['orders_used'])} seller(s), avg {combo['avg_price_per_unit']:.1f}p/relic, "
                f"total cost {result['total_cost']:.1f}p -> "
                f"expected profit {result['expected_profit']:+.1f}p, "
                f"worst case {result['worst_case_profit']:+.1f}p{fill_note}"
            )
            extra.append(
                f"  P(profit) = {result['prob_profit_pct']:.1f}%   "
                f"P(loss) = {result['prob_loss_pct']:.1f}%   "
                f"P(exact breakeven) = {result['prob_breakeven_pct']:.1f}%"
            )

        self.after(0, lambda: self._finish_n_opens(row, extra))

    def _finish_n_opens(self, row, extra):
        self.status_var.set("Ready.")
        self._render_details(row, extra_lines=extra)

    def _show_refinement_comparison(self):
        """
        Review item #6: Intact -> Radiant side by side, with the explicit
        "which tier is actually recommended" call the review asked for -
        Radiant is not assumed to be best. Uses the relic's own purchase
        cost held fixed across tiers (see refinement_comparison()'s
        docstring for why) - this is about whether refining pays off, not
        about which tier's market listing is cheapest to buy pre-refined.
        """
        if self._selected_row is None:
            messagebox.showinfo("No relic selected", "Click a relic in the table first.")
            return
        row = self._selected_row
        relic = row["relic"]
        try:
            trace_rate = float(self.trace_rate_var.get())
        except ValueError:
            trace_rate = 0.0
        # Prefer the currently-selected channel's cost; fall back to
        # whichever channel actually has a price, same rule used
        # everywhere else - never averaged, never invented.
        cost = row["online_cost"] if row["online_cost"] is not None else row["offline_cost"]

        win = tk.Toplevel(self)
        win.title(f"Refinement Comparison - {relic.relic_name}")
        win.geometry("760x360")

        cols = ("tier", "traces", "ev", "exp_profit", "roi", "chance_profit",
                "chance_loss", "rare_chance", "incremental")
        headers = {
            "tier": "Tier", "traces": "Traces", "ev": "Exp. Value", "exp_profit": "Exp. Profit",
            "roi": "ROI", "chance_profit": "P(profit)", "chance_loss": "P(loss)",
            "rare_chance": "Rare Slot %", "incremental": "EV Gain/Trace",
        }
        tree = ttk.Treeview(win, columns=cols, show="headings", height=6)
        for c in cols:
            tree.heading(c, text=headers[c])
            tree.column(c, width=85, anchor="center")
        tree.pack(fill="x", padx=8, pady=8)

        rows = relic.refinement_comparison(row["prices_snapshot"], cost, trace_rate)
        for r in rows:
            incr_str = f"{r['incremental_ev_per_trace']:+.4f}p" if r["incremental_ev_per_trace"] is not None else "-"
            tree.insert("", "end", values=(
                r["tier"].capitalize(), r["trace_cost"],
                f"{r['expected_value']:.1f}p", f"{r['expected_profit']:+.1f}p",
                f"{r['expected_roi_pct']:.0f}%" if r['expected_roi_pct'] is not None else "-",
                f"{r['chance_of_profit_pct']:.0f}%", f"{r['chance_of_loss_pct']:.0f}%",
                f"{r['rare_slot_chance_pct']:.1f}%", incr_str,
            ))

        summary = ttk.Frame(win, padding=8)
        summary.pack(fill="x")
        # Two SEPARATE variables on purpose: objective_key_var holds the
        # internal computation key ("expected_roi_pct") that best_refinement()
        # consumes, while the Combobox's own textvariable holds the friendly
        # DISPLAYED label ("ROI"). Binding the Combobox directly to
        # objective_key_var would mean every objective_key_var.set(key) call
        # also overwrites the box's visible text with that raw key string
        # instead of the friendly label - a real bug, not just untidy, since
        # it would make the dropdown display "expected_roi_pct" after the
        # user picks "ROI".
        objective_key_var = tk.StringVar(value="expected_profit")
        objective_labels = {
            "expected_profit": "Expected Profit", "expected_roi_pct": "ROI",
            "worst_case_profit": "Guaranteed (Worst-Case) Profit", "chance_of_profit_pct": "Chance of Profit",
        }
        result_var = tk.StringVar(value="")

        def update_recommendation(*_args):
            best = relic.best_refinement(row["prices_snapshot"], cost, trace_rate, objective_key_var.get())
            if best:
                result_var.set(
                    f"Recommended for {objective_labels[objective_key_var.get()]}: "
                    f"{best['tier'].capitalize()}"
                )

        ttk.Label(summary, text="Optimize for:").pack(side="left")
        display_var = tk.StringVar(value=objective_labels["expected_profit"])
        combo = ttk.Combobox(
            summary, textvariable=display_var, values=list(objective_labels.values()),
            state="readonly", width=22,
        )

        def on_combo_change(_event=None):
            selected_label = display_var.get()
            key = next(k for k, v in objective_labels.items() if v == selected_label)
            objective_key_var.set(key)
            update_recommendation()

        combo.bind("<<ComboboxSelected>>", on_combo_change)
        combo.pack(side="left", padx=6)
        ttk.Label(summary, textvariable=result_var, font=("", 10, "bold")).pack(side="left", padx=12)

        if cost is None:
            ttk.Label(
                win, text="No relic price available yet - numbers above use 0p relic cost as a placeholder.",
                foreground="#a05a00",
            ).pack(fill="x", padx=8)

        update_recommendation()

    def sort_by(self, col, descending):
        data = [(self.tree.set(k, col), k) for k in self.tree.get_children("")]
        data.sort(key=lambda t: _column_sort_key(t[0]), reverse=descending)
        for index, (_, k) in enumerate(data):
            self.tree.move(k, "", index)
        self.tree.heading(col, command=lambda: self.sort_by(col, not descending))


if __name__ == "__main__":
    app = RelicToolApp()
    app.mainloop()

