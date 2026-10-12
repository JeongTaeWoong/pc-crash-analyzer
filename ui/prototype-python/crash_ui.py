"""PC Crash Analyzer - 안내형 데스크톱 UI **Python 시안** (Windows용, 파이썬 표준 tkinter)

※ 최종 UI 는 수집기와 같은 C#(WPF)으로 만든다. 이 파일은 C# 버전이 기능을 다 따라잡을 때까지
   시연·비교용으로만 두고, 새 기능은 C# 버전에만 추가한다. (2026-10-12 팀 결정)

화면 구성 (제안서 아키텍처의 '안내 UI' + 멘토링 1차의 확인 → 안내 → 해결 → 검증 흐름)
  ┌ 머리글: 스냅샷 · 증상 · 대상 프로그램 · 안내/자동 모드 · [스냅샷 열기]
  ├ 단계 표시: ① 진단 ② 원인 확인 ③ 해결 안내 ④ 검증
  ├ 탭 [진단 결과]  층 판정 막대, 원인 후보 순위와 근거, 엔진이 정한 확인 순서
  │   [원인 확인]  지금 할 점검 1개(단계·되돌리기·실행 방식) + 결과 버튼, 후보 확률 변화
  │   [해결 · 검증] 확정된 원인, 해결책 목록, 적용 기록, 사후 스냅샷/사용자 응답으로 검증
  └ 오른쪽: 상담 창 (LLM 에이전트. API 키가 없으면 오프라인 대역)

UI 는 판단하지 않는다. 모든 숫자와 순서는 엔진(crash_engine tool ...)이 돌려준 값을 그대로 보여 준다.

실행
    python ui/prototype-python/crash_ui.py                  # 실행 후 [스냅샷 열기]
    python ui/prototype-python/crash_ui.py engine/data/samples/s1_anticheat_crash.json
    (엔진 경로를 따로 줄 때)  set CE_ENGINE=C:\\...\\crash_engine.exe
"""
import datetime
import glob
import json
import functools
import os
import queue
import sys
import tempfile
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
import tkinter.font as tkfont

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))  # ui/prototype-python → 저장소 최상위
sys.path.insert(0, HERE)  # ce_agent.py 는 같은 폴더
sys.path.insert(0, os.path.join(REPO, "engine", "tools"))

from ce_tools import CrashEngine, EngineError  # noqa: E402
from ce_agent import find_engine, make_agent  # noqa: E402

# ─────────────────────────────────────────────────────────────────────────────
# 색 (Windows 11 밝은 테마 느낌)
# ─────────────────────────────────────────────────────────────────────────────
BG = "#F3F3F3"
CARD = "#FFFFFF"
LINE = "#E1E1E1"
TEXT = "#1B1B1B"
SUB = "#5F5F5F"
ACCENT = "#0067C0"
ACCENT_SOFT = "#E5F0FA"
LAYER_COLOR = {"SW": "#0067C0", "DRIVER": "#7A4FD6", "HW": "#C4581B"}
LAYER_NAME = {"SW": "소프트웨어", "DRIVER": "드라이버·시스템", "HW": "하드웨어"}
STAGE_NAME = {1: "1차 보안", 2: "2차 드라이버", 3: "3차 하드웨어", 0: "범위 밖"}
EXEC_STYLE = {  # 실행 방식 표시: (글자, 글자색, 배경색)
    "guide": ("안내", "#3B3B3B", "#EDEDED"),
    "auto": ("자동 가능", "#0E6B2F", "#DFF6E3"),
    "confirm": ("동의 후 실행", "#8A5300", "#FFF1D6"),
    "manual": ("직접 작업", "#A4262C", "#FDE7E9"),
}
OUTCOME_STYLE = {
    "resolved": ("해결됨", "#0E6B2F"),
    "recurred": ("재발", "#A4262C"),
    "observing": ("관찰 중", "#8A5300"),
    "need_answer": ("사용자 응답 필요", "#8A5300"),
}
SYMPTOM_NAME = {
    "app_crash": "프로그램 튕김", "freeze": "멈춤(프리징)", "fps_drop": "프레임 저하", "flicker": "화면 깜빡임",
    "input_loss": "입력 누락", "launch_fail": "실행 안 됨", "network_fail": "네트워크 끊김",
    "unexpected_reboot": "갑자기 재부팅",
}
SCOPE_NAME = {"single_app": "특정 프로그램 하나", "multiple_apps": "여러 프로그램", "system_wide": "PC 전체"}
STEPS = ["진단", "원인 확인", "해결 안내", "검증"]
STATUS_STEP = {"checking": 1, "exhausted": 1, "confirmed": 2, "fixing": 3, "failed": 3, "resolved": 4}


def pick_font_family(root):
    families = set(tkfont.families(root))
    wanted = ["Malgun Gothic", "맑은 고딕", "Noto Sans CJK KR", "NanumGothic", "Segoe UI"]
    for i in range(len(wanted)):
        if wanted[i] in families:
            return wanted[i]
    return "TkDefaultFont"


def pct(p):
    return str(int(round(p * 100))) + "%"


def duration_text(seconds):
    if seconds < 60:
        return str(int(seconds)) + "초"
    minutes = seconds / 60.0
    if minutes < 60:
        return str(int(round(minutes))) + "분"
    return ("%.1f" % (minutes / 60.0)) + "시간"


class Card(tk.Frame):
    """흰 바탕 + 얇은 테두리 + 제목이 있는 묶음 상자"""

    def __init__(self, parent, title, fonts, **kw):
        tk.Frame.__init__(self, parent, bg=CARD, highlightbackground=LINE, highlightthickness=1, **kw)
        self.title = tk.Label(self, text=title, bg=CARD, fg=TEXT, font=fonts["h3"], anchor="w")
        self.title.pack(fill="x", padx=14, pady=(10, 4))
        self.body = tk.Frame(self, bg=CARD)
        self.body.pack(fill="both", expand=True, padx=14, pady=(0, 12))


class App:
    def __init__(self, root, engine_path):
        self.root = root
        self.engine_path = engine_path
        self.engine = CrashEngine(engine_path)
        self.snapshot_path = None
        self.session_path = None
        self.diag = None
        self.view = None
        self.agent = None
        self.mode = tk.StringVar(value="guide")
        self.answer_recurred = tk.StringVar(value="no")
        self.answer_minutes = tk.StringVar(value="60")
        self.applied_at = tk.StringVar(value="")
        self.chat_queue = queue.Queue()
        self.busy = False

        family = pick_font_family(root)
        self.fonts = {
            "body": (family, 10), "small": (family, 9), "bold": (family, 10, "bold"),
            "h1": (family, 15, "bold"), "h2": (family, 13, "bold"), "h3": (family, 11, "bold"),
            "big": (family, 18, "bold"),
        }
        root.title("PC Crash Analyzer (Python 시안)")
        self.fit_to_screen()
        root.configure(bg=BG)
        self.setup_style()
        self.build()
        self.root.after(100, self.poll_chat)

    # ------------------------------------------------------------------ 모양
    def fit_to_screen(self):
        """창이 화면(작업 표시줄 제외)보다 커서 아래쪽 버튼이 잘리지 않게 한다.
        Windows 에서는 최대화로 시작하고, 그 밖에서는 화면 크기 안으로 줄인다."""
        root = self.root
        sw = root.winfo_screenwidth()
        sh = root.winfo_screenheight()
        w = min(1360, sw - 40)
        h = min(860, sh - 100)
        root.minsize(min(1000, w), min(600, h))
        root.geometry("%dx%d+%d+%d" % (w, h, max(0, (sw - w) // 2), max(0, (sh - h) // 4)))
        if os.name == "nt":
            # 화면을 다 만든 뒤에 최대화해야 위젯 크기 계산이 최대화를 덮어쓰지 않는다
            root.after(50, self.maximize)

    def maximize(self):
        try:
            self.root.update_idletasks()
            self.root.state("zoomed")
        except tk.TclError:
            pass

    def setup_style(self):
        st = ttk.Style(self.root)
        if "vista" in st.theme_names():
            st.theme_use("vista")
        else:
            st.theme_use("clam")
        st.configure(".", font=self.fonts["body"])
        st.configure("Treeview", rowheight=28, font=self.fonts["body"], background=CARD, fieldbackground=CARD)
        st.configure("Treeview.Heading", font=self.fonts["bold"])
        st.configure("TNotebook", background=BG, borderwidth=0)
        st.configure("TNotebook.Tab", font=self.fonts["bold"], padding=(16, 6))
        st.configure("Accent.TButton", font=self.fonts["bold"])
        st.configure("TRadiobutton", background=CARD)

    def button(self, parent, text, command, primary=False):
        b = tk.Button(parent, text=text, command=command, font=self.fonts["bold"] if primary else self.fonts["body"],
                      bg=ACCENT if primary else "#FBFBFB", fg="#FFFFFF" if primary else TEXT,
                      activebackground="#005A9E" if primary else "#F0F0F0",
                      activeforeground="#FFFFFF" if primary else TEXT,
                      relief="flat", bd=0, padx=14, pady=6, cursor="hand2",
                      highlightthickness=1, highlightbackground=ACCENT if primary else "#D0D0D0")
        return b

    def on_wrap(self, event):
        """라벨 폭이 바뀌면 줄바꿈 폭도 맞춘다"""
        event.widget.configure(wraplength=max(120, event.width - 8))

    def set_enabled(self, btn, on, primary=False):
        if on:
            btn.configure(state="normal", bg=ACCENT if primary else "#FBFBFB", fg="#FFFFFF" if primary else TEXT)
        else:
            btn.configure(state="disabled", bg="#EDEDED", disabledforeground="#9A9A9A")

    def badge(self, parent, execution):
        text, fg, bg = EXEC_STYLE.get(execution, (execution, TEXT, "#EDEDED"))
        return tk.Label(parent, text=" " + text + " ", fg=fg, bg=bg, font=self.fonts["small"], padx=4, pady=1)

    # ------------------------------------------------------------------ 뼈대
    def build(self):
        self.build_header()
        self.build_stepper()
        body = tk.PanedWindow(self.root, orient="horizontal", bg=BG, sashwidth=6, bd=0)
        body.pack(fill="both", expand=True, padx=12, pady=(0, 6))
        left = tk.Frame(body, bg=BG)
        right = tk.Frame(body, bg=BG)
        body.add(left, minsize=820, width=900, stretch="always")
        body.add(right, minsize=340, width=420)
        self.notebook = ttk.Notebook(left)
        self.notebook.pack(fill="both", expand=True)
        self.tab_diag = tk.Frame(self.notebook, bg=BG)
        self.tab_check = tk.Frame(self.notebook, bg=BG)
        self.tab_fix = tk.Frame(self.notebook, bg=BG)
        self.notebook.add(self.tab_diag, text="진단 결과")
        self.notebook.add(self.tab_check, text="원인 확인")
        self.notebook.add(self.tab_fix, text="해결 · 검증")
        self.build_diag_tab()
        self.build_check_tab()
        self.build_fix_tab()
        self.build_chat(right)
        self.status_bar = tk.Label(self.root, text="", bg="#E9E9E9", fg=SUB, anchor="w",
                                   font=self.fonts["small"], padx=10)
        self.status_bar.pack(fill="x", side="bottom")
        self.update_status_bar()

    def build_header(self):
        bar = tk.Frame(self.root, bg=CARD, highlightbackground=LINE, highlightthickness=1)
        bar.pack(fill="x")
        tk.Label(bar, text="PC Crash Analyzer", bg=CARD, fg=ACCENT, font=self.fonts["h1"]).pack(
            side="left", padx=(16, 18), pady=10)
        # 오른쪽 버튼·모드를 먼저 배치해야 창이 좁아져도 잘리지 않는다 (설명 글이 대신 줄어든다)
        self.button(bar, "스냅샷 열기…", self.on_open, primary=True).pack(side="right", padx=(6, 16), pady=8)
        self.button(bar, "최근 덤프 열기", self.on_open_latest_dump).pack(side="right", padx=6, pady=8)
        mode_box = tk.Frame(bar, bg=CARD)
        mode_box.pack(side="right", padx=6)
        tk.Label(mode_box, text="실행 방식", bg=CARD, fg=SUB, font=self.fonts["small"]).pack(side="left", padx=(0, 6))
        ttk.Radiobutton(mode_box, text="안내 모드", value="guide", variable=self.mode,
                        command=self.on_mode_change).pack(side="left")
        ttk.Radiobutton(mode_box, text="자동 점검 모드", value="auto", variable=self.mode,
                        command=self.on_mode_change).pack(side="left", padx=(6, 0))
        self.header_info = tk.Label(bar, text="스냅샷을 열어 진단을 시작하세요.", bg=CARD, fg=SUB,
                                    font=self.fonts["body"], anchor="w")
        self.header_info.pack(side="left", fill="x", expand=True)

    def build_stepper(self):
        self.stepper = tk.Canvas(self.root, height=56, bg=BG, highlightthickness=0)
        self.stepper.pack(fill="x", padx=12, pady=(8, 4))
        self.stepper.bind("<Configure>", self.draw_stepper)

    def draw_stepper(self, event=None):
        c = self.stepper
        c.delete("all")
        width = c.winfo_width()
        if width < 50:
            return
        current = 0
        if self.view is not None:
            current = STATUS_STEP.get(self.view.get("status"), 1)
        elif self.diag is not None:
            current = 0
        n = len(STEPS)
        seg = (width - 40) / float(n)
        for i in range(n):
            x = 20 + seg * i + seg / 2
            done = self.diag is not None and (i < current or (i == 3 and current == 4))
            active = self.diag is not None and i == current and current < 4
            color = ACCENT if (done or active) else "#C8C8C8"
            if i > 0:
                px = 20 + seg * (i - 1) + seg / 2
                c.create_line(px + 16, 18, x - 16, 18, fill=ACCENT if done or active else "#C8C8C8", width=2)
            fill = ACCENT if done else (CARD if not active else ACCENT_SOFT)
            c.create_oval(x - 13, 5, x + 13, 31, fill=fill, outline=color, width=2)
            mark = "✓" if done else str(i + 1)
            c.create_text(x, 18, text=mark, fill="#FFFFFF" if done else color, font=self.fonts["bold"])
            c.create_text(x, 46, text=STEPS[i], fill=TEXT if (done or active) else SUB,
                          font=self.fonts["bold"] if active else self.fonts["small"])

    # ------------------------------------------------------------------ 탭 1: 진단 결과
    def build_diag_tab(self):
        t = self.tab_diag
        top = tk.Frame(t, bg=BG)
        top.pack(fill="x", padx=8, pady=(10, 6))
        top.columnconfigure(0, weight=3, uniform="d")
        top.columnconfigure(1, weight=2, uniform="d")
        self.card_layer = Card(top, "어느 층의 문제인가 (층 판정)", self.fonts)
        self.card_layer.grid(row=0, column=0, sticky="nsew", padx=(0, 6))
        self.layer_canvas = tk.Canvas(self.card_layer.body, height=112, bg=CARD, highlightthickness=0)
        self.layer_canvas.pack(fill="x")
        self.layer_note = tk.Label(self.card_layer.body, text="", bg=CARD, fg=SUB, font=self.fonts["small"],
                                   anchor="w", justify="left")
        self.layer_note.pack(fill="x")
        self.layer_canvas.bind("<Configure>", self.draw_layer)

        self.card_summary = Card(top, "요약", self.fonts)
        self.card_summary.grid(row=0, column=1, sticky="nsew", padx=(6, 0))
        self.summary_label = tk.Label(self.card_summary.body, text="", bg=CARD, fg=TEXT, font=self.fonts["body"],
                                      anchor="nw", justify="left", wraplength=290)
        self.summary_label.pack(fill="both", expand=True)
        self.summary_label.bind("<Configure>", self.on_wrap)

        mid = tk.Frame(t, bg=BG)
        mid.pack(fill="both", expand=True, padx=8, pady=6)
        mid.columnconfigure(0, weight=3, uniform="m")
        mid.columnconfigure(1, weight=2, uniform="m")
        mid.rowconfigure(0, weight=1)
        card_c = Card(mid, "원인 후보 순위", self.fonts)
        card_c.grid(row=0, column=0, sticky="nsew", padx=(0, 6))
        cols = ("rank", "label", "stage", "prob")
        self.tree_cand = ttk.Treeview(card_c.body, columns=cols, show="headings", height=6, selectmode="browse")
        heads = [("rank", "순위", 44), ("label", "후보", 250), ("stage", "범위", 96), ("prob", "확률", 60)]
        for i in range(len(heads)):
            self.tree_cand.heading(heads[i][0], text=heads[i][1])
            self.tree_cand.column(heads[i][0], width=heads[i][2], minwidth=40, stretch=(i == 1),
                                  anchor="w" if i == 1 else "center")
        self.tree_cand.pack(fill="both", expand=True)
        self.tree_cand.bind("<<TreeviewSelect>>", self.on_select_candidate)
        card_e = Card(mid, "근거 (왜 이 후보인가)", self.fonts)
        card_e.grid(row=0, column=1, sticky="nsew", padx=(6, 0))
        self.evidence_text = tk.Text(card_e.body, wrap="word", bd=0, bg=CARD, fg=TEXT, font=self.fonts["body"],
                                     height=8, spacing1=2, spacing3=4)
        self.evidence_text.pack(fill="both", expand=True)
        self.evidence_text.configure(state="disabled")

        bottom = Card(t, "엔진이 정한 원인 확인 순서 (비용 가중 v1)", self.fonts)
        bottom.pack(fill="both", expand=True, padx=8, pady=(6, 10))
        cols = ("no", "check", "time", "chance", "exec")
        self.tree_plan = ttk.Treeview(bottom.body, columns=cols, show="headings", height=6)
        heads = [("no", "순서", 44), ("check", "점검 → 대상", 380), ("time", "예상 시간", 76),
                 ("chance", "드러날 확률", 90), ("exec", "실행 방식", 96)]
        for i in range(len(heads)):
            self.tree_plan.heading(heads[i][0], text=heads[i][1])
            self.tree_plan.column(heads[i][0], width=heads[i][2], minwidth=40, stretch=(i == 1),
                                  anchor="w" if i == 1 else "center")
        self.tree_plan.pack(fill="both", expand=True)
        self.plan_note = tk.Label(bottom.body, text="", bg=CARD, fg=SUB, font=self.fonts["small"], anchor="w")
        self.plan_note.pack(fill="x", pady=(6, 0))

    def draw_layer(self, event=None):
        c = self.layer_canvas
        c.delete("all")
        if self.diag is None:
            c.create_text(10, 50, text="아직 진단 전입니다.", anchor="w", fill=SUB, font=self.fonts["body"])
            return
        width = max(c.winfo_width(), 300)
        probs = self.diag["layer"]["probability"]
        keys = ["SW", "DRIVER", "HW"]
        label_w = 130
        bar_w = width - label_w - 70
        for i in range(len(keys)):
            k = keys[i]
            p = probs.get(k, 0)
            y = 10 + i * 34
            top = self.diag["layer"]["top"] == k
            c.create_text(0, y + 11, text=LAYER_NAME[k], anchor="w", fill=TEXT,
                          font=self.fonts["bold"] if top else self.fonts["body"])
            c.create_rectangle(label_w, y + 2, label_w + bar_w, y + 20, fill="#EFEFEF", outline="")
            c.create_rectangle(label_w, y + 2, label_w + max(2, bar_w * p), y + 20, fill=LAYER_COLOR[k], outline="")
            c.create_text(label_w + bar_w + 10, y + 11, text=pct(p), anchor="w", fill=TEXT,
                          font=self.fonts["bold"] if top else self.fonts["body"])

    def on_select_candidate(self, event=None):
        sel = self.tree_cand.selection()
        if not sel or self.diag is None:
            return
        idx = int(sel[0])
        cand = self.diag["candidates"][idx]
        ev = sorted(cand.get("evidence", []), key=functools.cmp_to_key(compare_evidence))
        lines = [cand["label"]]
        for i in range(len(ev)):
            if ev[i].get("kind") in ("prior", "layer"):
                continue
            lines.append("• " + ev[i].get("detail", ""))
        if len(lines) == 1:
            lines.append("• 직접 근거는 없고, 증상별 사전 비중만 있습니다.")
        self.set_text(self.evidence_text, "\n".join(lines))

    # ------------------------------------------------------------------ 탭 2: 원인 확인
    def build_check_tab(self):
        t = self.tab_check
        t.columnconfigure(0, weight=3, uniform="c")
        t.columnconfigure(1, weight=2, uniform="c")
        t.rowconfigure(0, weight=1)
        left = tk.Frame(t, bg=BG)
        left.grid(row=0, column=0, sticky="nsew", padx=(8, 6), pady=10)
        right = tk.Frame(t, bg=BG)
        right.grid(row=0, column=1, sticky="nsew", padx=(6, 8), pady=10)

        self.card_next = Card(left, "지금 할 점검", self.fonts)
        self.card_next.pack(fill="both", expand=True)
        b = self.card_next.body
        self.next_title = tk.Label(b, text="", bg=CARD, fg=TEXT, font=self.fonts["h2"], anchor="w", justify="left")
        self.next_title.pack(fill="x")
        self.next_target = tk.Label(b, text="", bg=CARD, fg=ACCENT, font=self.fonts["bold"], anchor="w")
        self.next_target.pack(fill="x", pady=(2, 6))
        self.next_meta = tk.Frame(b, bg=CARD)
        self.next_meta.pack(fill="x", pady=(0, 8))
        self.next_advice = tk.Label(b, text="", bg=CARD, fg=TEXT, font=self.fonts["body"], anchor="w",
                                    justify="left", wraplength=400)
        self.next_advice.pack(fill="x", pady=(0, 8))
        self.next_advice.bind("<Configure>", self.on_wrap)
        self.next_steps = tk.Frame(b, bg="#FAFAFA", highlightbackground=LINE, highlightthickness=1)
        self.next_steps.pack(fill="x", pady=(0, 8))
        self.next_rollback = tk.Label(b, text="", bg=CARD, fg=SUB, font=self.fonts["small"], anchor="w",
                                      justify="left", wraplength=400)
        self.next_rollback.pack(fill="x")
        self.next_rollback.bind("<Configure>", self.on_wrap)
        self.next_reason = tk.Label(b, text="", bg=CARD, fg=SUB, font=self.fonts["small"], anchor="w",
                                    justify="left", wraplength=400)
        self.next_reason.pack(fill="x", pady=(2, 0))
        self.next_reason.bind("<Configure>", self.on_wrap)
        btns = tk.Frame(b, bg=CARD)
        btns.pack(fill="x", side="bottom", pady=(12, 0))
        tk.Label(btns, text="해 본 결과는?", bg=CARD, fg=TEXT, font=self.fonts["bold"]).pack(side="left", padx=(0, 10))
        self.btn_solved = self.button(btns, "증상이 사라졌어요", functools.partial(self.on_report, "solved"), True)
        self.btn_solved.pack(side="left", padx=4)
        self.btn_not = self.button(btns, "그대로예요", functools.partial(self.on_report, "not_solved"))
        self.btn_not.pack(side="left", padx=4)
        self.btn_skip = self.button(btns, "할 수 없어요", functools.partial(self.on_report, "skipped"))
        self.btn_skip.pack(side="left", padx=4)

        card_b = Card(right, "원인 후보 확률 (실시간)", self.fonts)
        card_b.pack(fill="x")
        self.belief_canvas = tk.Canvas(card_b.body, width=200, height=210, bg=CARD, highlightthickness=0)
        self.belief_canvas.pack(fill="x")
        self.belief_canvas.bind("<Configure>", self.on_belief_resize)
        card_h = Card(right, "지금까지 한 점검", self.fonts)
        card_h.pack(fill="both", expand=True, pady=(12, 0))
        self.tree_hist = ttk.Treeview(card_h.body, columns=("check", "result"), show="headings", height=6)
        self.tree_hist.heading("check", text="점검")
        self.tree_hist.heading("result", text="결과")
        self.tree_hist.column("check", width=170, minwidth=80, stretch=True, anchor="w")
        self.tree_hist.column("result", width=64, minwidth=50, stretch=False, anchor="center")
        self.tree_hist.pack(fill="both", expand=True)

    def on_belief_resize(self, event=None):
        self.draw_belief()

    def draw_belief(self):
        c = self.belief_canvas
        c.delete("all")
        if self.view is None:
            return
        belief = self.view.get("belief", [])
        width = max(c.winfo_width(), 200)
        confirmed = self.view.get("confirmed", {}).get("id")
        n = min(6, len(belief))
        for i in range(n):
            b = belief[i]
            y = 4 + i * 34
            name = b["label"]
            if len(name) > 22:
                name = name[:21] + "…"
            c.create_text(0, y + 7, text=name, anchor="w", fill=TEXT,
                          font=self.fonts["bold"] if b["id"] == confirmed else self.fonts["small"])
            bar = width - 52
            c.create_rectangle(0, y + 17, bar, y + 27, fill="#EFEFEF", outline="")
            color = "#0E6B2F" if b["id"] == confirmed else ACCENT
            c.create_rectangle(0, y + 17, max(2, bar * b["probability"]), y + 27, fill=color, outline="")
            c.create_text(width - 4, y + 22, text=pct(b["probability"]), anchor="e", fill=TEXT,
                          font=self.fonts["small"])

    # ------------------------------------------------------------------ 탭 3: 해결 · 검증
    def build_fix_tab(self):
        t = self.tab_fix
        bottom = tk.Frame(t, bg=BG)
        bottom.pack(fill="x", side="bottom", padx=8, pady=(6, 10))
        top = tk.Frame(t, bg=BG)
        top.pack(fill="both", expand=True, padx=8, pady=(10, 6))
        top.columnconfigure(0, weight=2, uniform="f")
        top.columnconfigure(1, weight=3, uniform="f")
        top.rowconfigure(0, weight=1)
        self.card_cause = Card(top, "확인된 원인", self.fonts)
        self.card_cause.grid(row=0, column=0, sticky="nsew", padx=(0, 6))
        self.cause_text = tk.Text(self.card_cause.body, wrap="word", bd=0, bg=CARD, fg=TEXT,
                                  font=self.fonts["body"], spacing1=2, spacing3=4)
        self.cause_text.tag_configure("title", font=self.fonts["h2"])
        self.cause_text.tag_configure("note", foreground="#8A5300")
        self.cause_text.pack(fill="both", expand=True)

        card_r = Card(top, "해결 방법 (기대 해결률 순)", self.fonts)
        card_r.grid(row=0, column=1, sticky="nsew", padx=(6, 0))
        self.tree_rem = ttk.Treeview(card_r.body, columns=("no", "label", "rate", "state"), show="headings",
                                     height=3, selectmode="browse")
        heads = [("no", "", 36), ("label", "해결책", 260), ("rate", "기대 해결률", 90), ("state", "상태", 70)]
        for i in range(len(heads)):
            self.tree_rem.heading(heads[i][0], text=heads[i][1])
            self.tree_rem.column(heads[i][0], width=heads[i][2], minwidth=30, stretch=(i == 1),
                                 anchor="w" if i == 1 else "center")
        self.tree_rem.pack(fill="x")
        self.tree_rem.bind("<<TreeviewSelect>>", self.on_select_remedy)
        self.remedy_text = tk.Text(card_r.body, wrap="word", bd=0, bg="#FAFAFA", fg=TEXT, font=self.fonts["body"],
                                   height=5, spacing1=2, spacing3=3, padx=8, pady=6)
        self.remedy_text.tag_configure("h", font=self.fonts["bold"])
        self.remedy_text.tag_configure("sub", foreground=SUB, font=self.fonts["small"])
        row = tk.Frame(card_r.body, bg=CARD)
        row.pack(fill="x", side="bottom")  # 설명 칸보다 먼저 배치해 높이가 모자라도 버튼이 보이게
        self.remedy_text.pack(fill="both", expand=True, pady=(8, 6))
        tk.Label(row, text="적용 시각", bg=CARD, fg=SUB, font=self.fonts["small"]).pack(side="left")
        ttk.Entry(row, textvariable=self.applied_at, width=24).pack(side="left", padx=6)
        self.btn_apply = self.button(row, "해결책 적용 기록", self.on_apply, True)
        self.btn_apply.pack(side="left", padx=6)

        bottom.columnconfigure(0, weight=3, uniform="v")
        bottom.columnconfigure(1, weight=2, uniform="v")
        card_v = Card(bottom, "해결됐는지 검증", self.fonts)
        card_v.grid(row=0, column=0, sticky="nsew", padx=(0, 6))
        vb = card_v.body
        row1 = tk.Frame(vb, bg=CARD)
        row1.pack(fill="x", pady=(0, 6))
        self.btn_verify_snap = self.button(row1, "사후 스냅샷으로 검증…", self.on_verify_snapshot, True)
        self.btn_verify_snap.pack(side="left")
        tk.Label(row1, text="  수집기가 해결 후 만든 스냅샷으로 재발 신호를 셉니다.", bg=CARD, fg=SUB,
                 font=self.fonts["small"]).pack(side="left")
        row2 = tk.Frame(vb, bg=CARD)
        row2.pack(fill="x")
        tk.Label(row2, text="또는 직접 답하기:", bg=CARD, fg=TEXT, font=self.fonts["body"]).pack(side="left")
        ttk.Radiobutton(row2, text="재발 없음", value="no", variable=self.answer_recurred).pack(side="left", padx=6)
        ttk.Radiobutton(row2, text="다시 발생", value="yes", variable=self.answer_recurred).pack(side="left")
        row3 = tk.Frame(vb, bg=CARD)
        row3.pack(fill="x", pady=(6, 0))
        tk.Label(row3, text="적용 후 써 본 시간(분)", bg=CARD, fg=SUB, font=self.fonts["small"]).pack(side="left")
        ttk.Spinbox(row3, from_=0, to=10000, increment=10, width=6, textvariable=self.answer_minutes).pack(
            side="left", padx=6)
        self.btn_verify_ans = self.button(row3, "응답으로 검증", self.on_verify_answer)
        self.btn_verify_ans.pack(side="left", padx=6)

        self.card_result = Card(bottom, "검증 결과", self.fonts)
        self.card_result.grid(row=0, column=1, sticky="nsew", padx=(6, 0))
        self.result_title = tk.Label(self.card_result.body, text="아직 검증 전", bg=CARD, fg=SUB,
                                     font=self.fonts["big"], anchor="w")
        self.result_title.pack(fill="x")
        self.result_detail = tk.Label(self.card_result.body, text="", bg=CARD, fg=TEXT, font=self.fonts["small"],
                                      anchor="nw", justify="left", wraplength=300)
        self.result_detail.pack(fill="both", expand=True)
        self.result_detail.bind("<Configure>", self.on_wrap)

    def on_select_remedy(self, event=None):
        if self.view is None:
            return
        sel = self.tree_rem.selection()
        rem = self.view.get("remedies", [])
        if not sel or not rem:
            return
        r = rem[int(sel[0])]
        t = self.remedy_text
        t.configure(state="normal")
        t.delete("1.0", "end")
        t.insert("end", r["label"] + "\n", "h")
        t.insert("end", r.get("description", "") + "\n", "sub")
        acts = r.get("actions", [])
        for k in range(len(acts)):
            a = acts[k]
            exe = EXEC_STYLE.get(a.get("execution"), (a.get("execution"),))[0]
            t.insert("end", "\n▸ " + a["label"] + "  [" + exe + "]\n", "h")
            steps = a.get("steps", [])
            for s in range(len(steps)):
                t.insert("end", "   " + str(s + 1) + ") " + steps[s] + "\n")
            if a.get("rollback"):
                t.insert("end", "   되돌리기: " + a["rollback"] + "\n", "sub")
            elif not a.get("reversible", True):
                t.insert("end", "   되돌리기 어려운 조치입니다. 실행 전에 한 번 더 확인하세요.\n", "sub")
        t.configure(state="disabled")

    # ------------------------------------------------------------------ 상담 창 (에이전트)
    def build_chat(self, parent):
        card = Card(parent, "상담 도우미", self.fonts)
        card.pack(fill="both", expand=True, padx=(6, 0))
        self.chat_mode = tk.Label(card.body, text="", bg=CARD, fg=SUB, font=self.fonts["small"], anchor="w")
        self.chat_mode.pack(fill="x", pady=(0, 6))
        row = tk.Frame(card.body, bg=CARD)
        row.pack(fill="x", side="bottom")
        self.chat_entry = ttk.Entry(row, font=self.fonts["body"])
        self.chat_entry.pack(side="left", fill="x", expand=True, ipady=4)
        self.chat_entry.bind("<Return>", self.on_chat_enter)
        self.button(row, "보내기", self.on_chat_enter, True).pack(side="left", padx=(6, 0))
        quick = tk.Frame(card.body, bg=CARD)
        quick.pack(fill="x", side="bottom", pady=(8, 6))
        q = ["그대로예요", "증상이 사라졌어요", "왜 이게 원인이에요?"]
        for i in range(len(q)):
            b = tk.Button(quick, text=q[i], command=functools.partial(self.send_chat, q[i]), relief="flat",
                          bg=ACCENT_SOFT, fg=ACCENT, font=self.fonts["small"], padx=8, pady=2, bd=0, cursor="hand2")
            b.pack(side="left", padx=(0, 6))
        self.chat = tk.Text(card.body, wrap="word", bd=0, bg="#FAFAFA", fg=TEXT, font=self.fonts["body"],
                            padx=10, pady=8, spacing1=2, spacing3=6, highlightthickness=1, highlightbackground=LINE)
        self.chat.tag_configure("user", foreground=ACCENT, font=self.fonts["bold"], lmargin1=40, lmargin2=40)
        self.chat.tag_configure("bot", foreground=TEXT, lmargin1=0, lmargin2=12)
        self.chat.tag_configure("tool", foreground=SUB, font=self.fonts["small"])
        self.chat.pack(fill="both", expand=True)
        self.chat.configure(state="disabled")
        self.chat_add("bot", "스냅샷을 열면 엔진의 진단 결과를 바탕으로 원인 확인을 도와드릴게요.\n"
                             "점검 결과를 말로 알려 주셔도 됩니다. (예: '그대로예요')")

    def chat_add(self, who, text):
        self.chat.configure(state="normal")
        if who == "user":
            self.chat.insert("end", text + "\n", "user")
        elif who == "tool":
            self.chat.insert("end", text + "\n", "tool")
        else:
            self.chat.insert("end", text + "\n\n", "bot")
        self.chat.configure(state="disabled")
        self.chat.see("end")

    def on_chat_enter(self, event=None):
        text = self.chat_entry.get().strip()
        if text:
            self.chat_entry.delete(0, "end")
            self.send_chat(text)

    def send_chat(self, text):
        if self.agent is None:
            self.chat_add("bot", "먼저 스냅샷을 열어 주세요.")
            return
        if self.busy:
            return
        self.busy = True
        self.chat_add("user", text)
        before = len(self.agent.trace)
        worker = threading.Thread(target=self.chat_worker, args=(text, before), daemon=True)
        worker.start()

    def chat_worker(self, text, before):
        try:
            answer = self.agent.ask(text)
        except Exception as e:  # 네트워크·API 오류도 대화창에 보여 준다
            answer = "오류: " + str(e)
        calls = self.agent.trace[before:]
        self.chat_queue.put((answer, calls))

    def poll_chat(self):
        try:
            while True:
                answer, calls = self.chat_queue.get_nowait()
                names = []
                for i in range(len(calls)):
                    names.append(calls[i][0])
                if names:
                    self.chat_add("tool", "엔진 도구 호출: " + " → ".join(names))
                self.chat_add("bot", answer)
                self.busy = False
                self.refresh()
        except queue.Empty:
            pass
        self.root.after(150, self.poll_chat)

    # ------------------------------------------------------------------ 동작
    def on_open(self):
        start = os.path.join(REPO, "engine", "data", "samples")
        path = filedialog.askopenfilename(title="스냅샷 열기", initialdir=start if os.path.isdir(start) else None,
                                          filetypes=[("스냅샷 JSON", "*.json"), ("모든 파일", "*.*")])
        if path:
            self.open_snapshot(path)

    def on_mode_change(self):
        if self.snapshot_path:
            if messagebox.askyesno("실행 방식 변경", "실행 방식을 바꾸면 세션을 처음부터 다시 시작합니다. 계속할까요?"):
                self.open_snapshot(self.snapshot_path)
            else:
                self.mode.set("auto" if self.mode.get() == "guide" else "guide")

    def on_open_latest_dump(self):
        """수집기(collector/BlackBox)가 만든 가장 최근 덤프의 engine-snapshot.json 을 연다."""
        path = find_latest_dump()
        if path is None:
            messagebox.showinfo("최근 덤프 없음",
                                "수집기 덤프(engine-snapshot.json)를 찾지 못했습니다.\n"
                                "수집기를 실행해 마킹하거나, [스냅샷 열기…]로 파일을 직접 고르세요.\n"
                                "(덤프 폴더가 다른 곳이면 CE_DUMPS 환경 변수로 알려 주세요)")
            return
        self.open_snapshot(path)

    def prepare_snapshot(self, path):
        """증상 정보가 비어 있으면(수집기 수동 마킹 등) 사용자에게 물어 채운 사본을 만든다.
        원본 덤프는 바꾸지 않는다. 취소하면 None."""
        try:
            with open(path, "r", encoding="utf-8-sig") as f:
                snap = json.load(f)
        except (OSError, ValueError) as e:
            messagebox.showerror("스냅샷 오류", "스냅샷을 읽지 못했습니다: " + str(e))
            return None
        sym = snap.get("symptom") or {}
        if sym.get("type") in SYMPTOM_NAME and sym.get("scope") in SCOPE_NAME:
            return path
        dlg = SymptomDialog(self.root, self.fonts, sym)
        self.root.wait_window(dlg.top)
        if dlg.result is None:
            return None
        sym.update(dlg.result)
        snap["symptom"] = sym
        folder = os.path.join(tempfile.gettempdir(), "pc_crash_analyzer")
        os.makedirs(folder, exist_ok=True)
        out = os.path.join(folder, "filled_" + os.path.basename(os.path.dirname(path) or "snapshot") + "_" +
                           os.path.basename(path))
        with open(out, "w", encoding="utf-8") as f:
            json.dump(snap, f, ensure_ascii=False, indent=2)
        return out

    def open_snapshot(self, path):
        source = path
        path = self.prepare_snapshot(path)
        if path is None:
            return
        try:
            self.diag = self.engine.call("diagnose", snapshot_path=path, mode=self.mode.get())
            folder = os.path.join(tempfile.gettempdir(), "pc_crash_analyzer")
            os.makedirs(folder, exist_ok=True)
            stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
            self.session_path = os.path.join(folder, "session_" + stamp + ".json")
            self.view = self.engine.call("session_start", session_path=self.session_path, snapshot_path=path,
                                         mode=self.mode.get())
        except EngineError as e:
            messagebox.showerror("엔진 오류", str(e))
            return
        self.snapshot_path = path
        self.agent = make_agent(self.engine_path, self.session_path)
        self.chat_mode.configure(text="연결: " + ("Claude API (엔진 도구 사용)" if self.agent.name == "Claude"
                                                 else "오프라인 대역 (API 키 없음, 정해진 표현만 이해)"))
        self.chat.configure(state="normal")
        self.chat.delete("1.0", "end")  # 새 스냅샷이면 대화도 새로 시작 (세션이 바뀌므로)
        self.chat.configure(state="disabled")
        self.chat_add("tool", "── 새 진단: " + os.path.basename(source) + " ──")
        self.fill_diag()
        self.refresh()
        self.notebook.select(self.tab_diag)
        top = self.diag["candidates"][0]["label"] if self.diag["candidates"] else "-"
        self.chat_add("bot", "진단을 마쳤어요. 가장 의심되는 원인은 '" + top + "'입니다.\n"
                             "'원인 확인' 탭의 점검부터 해 보시고 결과를 알려 주세요.")

    def call_session(self, tool, **args):
        try:
            res = self.engine.call(tool, session_path=self.session_path, **args)
        except EngineError as e:
            messagebox.showerror("엔진 오류", str(e))
            return None
        self.refresh()
        return res

    def on_report(self, result):
        if self.view is None or not self.view.get("next_check"):
            return
        check_id = self.view["next_check"]["check_id"]
        label = self.view["next_check"]["label"]
        if self.call_session("session_report", check_id=check_id, result=result) is not None:
            word = {"solved": "증상이 사라짐", "not_solved": "그대로", "skipped": "할 수 없음"}[result]
            self.chat_add("tool", "[화면] " + label + " → " + word)
            if self.view.get("status") == "confirmed":
                self.notebook.select(self.tab_fix)

    def on_apply(self):
        if self.view is None or self.view.get("status") != "confirmed":
            messagebox.showinfo("안내", "원인이 확인된 뒤에 해결책을 적용할 수 있습니다.")
            return
        args = {}
        sel = self.tree_rem.selection()
        rem = self.view.get("remedies", [])
        if sel and rem:
            args["remedy_id"] = rem[int(sel[0])]["remedy_id"]
        if self.applied_at.get().strip():
            args["applied_at"] = self.applied_at.get().strip()
        if self.call_session("session_apply_fix", **args) is not None:
            self.chat_add("tool", "[화면] 해결책 적용 기록")

    def on_verify_snapshot(self):
        if self.view is None or self.view.get("status") != "fixing":
            messagebox.showinfo("안내", "해결책을 적용한 뒤에 검증할 수 있습니다.")
            return
        path = filedialog.askopenfilename(title="사후 스냅샷 열기", filetypes=[("스냅샷 JSON", "*.json")],
                                          initialdir=os.path.dirname(self.snapshot_path))
        if path:
            self.verify(after_snapshot_path=path)

    def on_verify_answer(self):
        if self.view is None or self.view.get("status") != "fixing":
            messagebox.showinfo("안내", "해결책을 적용한 뒤에 검증할 수 있습니다.")
            return
        try:
            minutes = float(self.answer_minutes.get())
        except ValueError:
            minutes = 0.0
        self.verify(recurred=self.answer_recurred.get() == "yes", observed_minutes=minutes)

    def verify(self, **args):
        res = self.call_session("session_verify", **args)
        if res is not None:
            self.show_verify(res.get("verify", {}))

    # ------------------------------------------------------------------ 화면 갱신
    def refresh(self):
        if self.session_path:
            try:
                self.view = self.engine.call("session_status", session_path=self.session_path)
            except EngineError as e:
                messagebox.showerror("엔진 오류", str(e))
        self.fill_header()
        self.draw_stepper()
        self.fill_check()
        self.fill_fix()
        self.update_status_bar()

    def fill_header(self):
        if self.diag is None:
            return
        sym = SYMPTOM_NAME.get(self.diag.get("symptom"), self.diag.get("symptom"))
        target = self.diag.get("target_process") or "시스템 전체"
        status = self.view.get("status_text", "") if self.view else ""
        self.header_info.configure(text="스냅샷 " + self.diag.get("snapshot_id", "") + "   ·   증상: " + sym +
                                   "   ·   대상: " + target + "   ·   상태: " + status, fg=TEXT)

    def fill_diag(self):
        d = self.diag
        self.draw_layer()
        self.layer_note.configure(text="판정 신뢰도 " + pct(d["layer"].get("confidence", 0)) +
                                  "  (수집된 항목이 적으면 낮아집니다)")
        lines = []
        cands = d.get("candidates", [])
        if cands:
            lines.append("1순위: " + cands[0]["label"])
            lines.append("범위: " + STAGE_NAME.get(cands[0].get("stage", 0), "") + "  ·  확률 " +
                         pct(cands[0]["probability"]))
        notes = d.get("notes", []) + d.get("skipped", [])
        for i in range(len(notes)):
            lines.append("· " + notes[i])
        warns = d.get("warnings", [])
        if warns:
            lines.append("· 수집 경고 " + str(len(warns)) + "건 (일부 항목 확인 불가)")
        self.summary_label.configure(text="\n".join(lines))

        self.tree_cand.delete(*self.tree_cand.get_children())
        for i in range(min(10, len(cands))):
            c = cands[i]
            self.tree_cand.insert("", "end", iid=str(i), values=(
                str(i + 1), c["label"], STAGE_NAME.get(c.get("stage", 0), ""), pct(c["probability"])))
        if cands:
            self.tree_cand.selection_set("0")
            self.on_select_candidate()

        self.tree_plan.delete(*self.tree_plan.get_children())
        steps = d["plan"].get("steps", [])
        for i in range(len(steps)):
            s = steps[i]
            exe = EXEC_STYLE.get(s.get("action", {}).get("execution"), ("-",))[0]
            self.tree_plan.insert("", "end", values=(str(i + 1), s["label"] + "  →  " + s["target"],
                                                     duration_text(s["cost_seconds"]),
                                                     pct(s["success_probability"]), exe))
        plan = d["plan"]
        self.plan_note.configure(text="기대 점검 시간 " + duration_text(plan.get("expected_cost_seconds", 0)) +
                                 "  ·  이 순서로 원인을 찾을 확률 " + pct(plan.get("resolved_probability", 0)) +
                                 "  ·  싼 점검으로 다른 후보를 먼저 걸러 내는 순서입니다.")

    def fill_check(self):
        v = self.view
        for w in self.next_meta.winfo_children():
            w.destroy()
        for w in self.next_steps.winfo_children():
            w.destroy()
        self.draw_belief()
        self.tree_hist.delete(*self.tree_hist.get_children())
        if v is None:
            return
        labels = {}
        if self.diag is not None:
            steps = self.diag["plan"].get("steps", [])
            for i in range(len(steps)):
                labels[steps[i]["check_id"]] = steps[i]["label"] + " → " + steps[i]["target"]
        hist = v.get("history", [])
        for i in range(len(hist)):
            h = hist[i]
            if h.get("type") != "check":
                continue
            ref = h.get("ref", "")
            res = {"solved": "사라짐", "not_solved": "그대로", "skipped": "건너뜀"}.get(h.get("result"), h.get("result"))
            self.tree_hist.insert("", "end", values=(labels.get(ref, ref.split("@")[0]), res))

        nc = v.get("next_check")
        enabled = v.get("status") == "checking" and nc is not None
        self.set_enabled(self.btn_solved, enabled, True)
        self.set_enabled(self.btn_not, enabled)
        self.set_enabled(self.btn_skip, enabled)
        if not enabled:
            self.next_title.configure(text="원인 확인이 끝났습니다." if v.get("status") != "checking"
                                      else "남은 점검이 없습니다.")
            self.next_target.configure(text=v.get("status_text", ""))
            self.next_advice.configure(text=v.get("next_action", ""))
            self.next_rollback.configure(text="")
            self.next_reason.configure(text="")
            return
        act = nc.get("action", {})
        self.next_title.configure(text=nc["label"])
        self.next_target.configure(text="대상: " + nc["target"])
        tk.Label(self.next_meta, text="⏱ " + duration_text(nc.get("cost_seconds", 0)), bg=CARD, fg=TEXT,
                 font=self.fonts["body"]).pack(side="left", padx=(0, 14))
        tk.Label(self.next_meta, text="원인이 드러날 확률 " + pct(nc.get("chance_to_reveal_cause", 0)), bg=CARD,
                 fg=TEXT, font=self.fonts["body"]).pack(side="left", padx=(0, 14))
        self.badge(self.next_meta, act.get("execution", "guide")).pack(side="left")
        if act.get("requires_reboot"):
            tk.Label(self.next_meta, text="  재부팅 필요", bg=CARD, fg="#8A5300",
                     font=self.fonts["small"]).pack(side="left")
        self.next_advice.configure(text=nc.get("advice", ""))
        steps = act.get("steps", [])
        for i in range(len(steps)):
            lb = tk.Label(self.next_steps, text=str(i + 1) + ".  " + steps[i], bg="#FAFAFA", fg=TEXT,
                          font=self.fonts["body"], anchor="w", justify="left", wraplength=400)
            lb.pack(fill="x", padx=12, pady=(8 if i == 0 else 2, 8 if i == len(steps) - 1 else 2))
            lb.bind("<Configure>", self.on_wrap)
        self.next_rollback.configure(text="↩ 되돌리기: " + act["rollback"] if act.get("rollback")
                                     else "↩ 되돌리기 어려운 조치입니다.")
        self.next_reason.configure(text="실행 방식: " + act.get("reason", ""))

    def fill_fix(self):
        v = self.view
        self.tree_rem.delete(*self.tree_rem.get_children())
        self.set_text(self.remedy_text, "")
        t = self.cause_text
        t.configure(state="normal")
        t.delete("1.0", "end")
        if v is None or not v.get("confirmed"):
            t.insert("end", "아직 원인이 확정되지 않았습니다.\n'원인 확인' 탭에서 점검을 진행해 주세요.")
            self.set_text(self.remedy_text, "원인이 확정되면 여기에 해결 방법이 나옵니다.\n"
                                            "'원인 확인' 탭에서 점검 결과를 알려 주세요.")
            t.configure(state="disabled")
            self.set_enabled(self.btn_apply, False)
            self.show_verify(None)
            return
        c = v["confirmed"]
        t.insert("end", c["label"] + "\n", "title")
        t.insert("end", STAGE_NAME.get(c.get("stage", 0), "") + " 범위\n\n")
        ev = c.get("evidence", [])
        for i in range(len(ev)):
            if ev[i].get("kind") in ("prior", "layer"):
                continue
            t.insert("end", "• " + ev[i].get("detail", "") + "\n")
        if v.get("restore_after_check") and v.get("status") == "confirmed":
            t.insert("end", "\n※ " + v["restore_after_check"] + "\n", "note")
        t.configure(state="disabled")

        rem = v.get("remedies", [])
        cur = v.get("current_remedy")
        applied = v.get("applied_remedy")
        cur_index = v.get("current_remedy_index", 0)
        select = None
        for i in range(len(rem)):
            r = rem[i]
            if r["remedy_id"] == applied and v.get("status") in ("fixing", "resolved"):
                state = "적용함" if v.get("status") == "fixing" else "해결됨"
            elif r["remedy_id"] == cur:
                state = "추천"
            elif i < cur_index:
                state = "재발"
            else:
                state = ""
            self.tree_rem.insert("", "end", iid=str(i), values=(str(i + 1), r["label"],
                                                                pct(r.get("expected_success", 0)), state))
            if r["remedy_id"] == cur:
                select = str(i)
        if select is not None:
            self.tree_rem.selection_set(select)
            self.on_select_remedy()
        self.set_enabled(self.btn_apply, v.get("status") == "confirmed", True)
        if not self.applied_at.get():
            self.applied_at.set(datetime.datetime.now().astimezone().replace(microsecond=0).isoformat())
        self.show_verify(v.get("last_verify"))

    def show_verify(self, ver):
        fixing = self.view is not None and self.view.get("status") == "fixing"
        self.set_enabled(self.btn_verify_snap, fixing, True)
        self.set_enabled(self.btn_verify_ans, fixing)
        if not ver:
            self.result_title.configure(text="아직 검증 전", fg=SUB)
            self.result_detail.configure(text="해결책을 적용하고 한동안 사용한 뒤 검증합니다.\n"
                                              "적용 이후의 재발 신호만 세고, 관찰 시간이 60분 이상이어야 '해결됨'입니다.")
            return
        name, color = OUTCOME_STYLE.get(ver.get("outcome"), (ver.get("outcome", ""), TEXT))
        self.result_title.configure(text=name, fg=color)
        lines = ["관찰 " + str(int(ver.get("observed_minutes", 0))) + "분 · 재발 " + str(ver.get("recurrences", 0)) + "건"]
        det = ver.get("details", [])
        for i in range(len(det)):
            lines.append("· " + det[i])
        if ver.get("feedback"):
            lines.append("· " + ver["feedback"])
        self.result_detail.configure(text="\n".join(lines))

    def update_status_bar(self):
        text = "엔진: " + self.engine_path
        if self.session_path:
            text += "   ·   세션: " + self.session_path
        text += "   ·   모드: " + ("자동 점검" if self.mode.get() == "auto" else "안내")
        self.status_bar.configure(text=text)

    def set_text(self, widget, text):
        widget.configure(state="normal")
        widget.delete("1.0", "end")
        widget.insert("end", text)
        widget.configure(state="disabled")


class SymptomDialog:
    """증상 종류·범위·대상 프로그램을 묻는 창 (수집기 수동 마킹은 증상이 unknown 으로 들어온다)"""

    def __init__(self, parent, fonts, symptom):
        self.result = None
        top = tk.Toplevel(parent)
        self.top = top
        top.title("어떤 증상인가요?")
        top.configure(bg=CARD)
        top.transient(parent)
        top.resizable(False, False)
        tk.Label(top, text="어떤 증상인가요?", bg=CARD, fg=TEXT, font=fonts["h2"]).pack(anchor="w", padx=20, pady=(16, 2))
        tk.Label(top, text="수집기 스냅샷에 증상 정보가 없어 직접 골라 주셔야 합니다.", bg=CARD, fg=SUB,
                 font=fonts["small"]).pack(anchor="w", padx=20, pady=(0, 10))
        self.type_var = tk.StringVar(value=symptom.get("type") if symptom.get("type") in SYMPTOM_NAME else "app_crash")
        self.scope_var = tk.StringVar(value=symptom.get("scope") if symptom.get("scope") in SCOPE_NAME else "single_app")
        self.target_var = tk.StringVar(value=symptom.get("target_process", ""))
        box = tk.Frame(top, bg=CARD)
        box.pack(fill="x", padx=20)
        keys = list(SYMPTOM_NAME.keys())
        for i in range(len(keys)):
            ttk.Radiobutton(box, text=SYMPTOM_NAME[keys[i]], value=keys[i], variable=self.type_var).grid(
                row=i // 2, column=i % 2, sticky="w", padx=(0, 24), pady=2)
        tk.Label(top, text="어디에서 나나요?", bg=CARD, fg=TEXT, font=fonts["bold"]).pack(anchor="w", padx=20, pady=(12, 2))
        box2 = tk.Frame(top, bg=CARD)
        box2.pack(fill="x", padx=20)
        scopes = list(SCOPE_NAME.keys())
        for i in range(len(scopes)):
            ttk.Radiobutton(box2, text=SCOPE_NAME[scopes[i]], value=scopes[i], variable=self.scope_var).pack(
                side="left", padx=(0, 16))
        tk.Label(top, text="문제가 나는 프로그램 (예: game.exe, 모르면 비워 두기)", bg=CARD, fg=TEXT,
                 font=fonts["bold"]).pack(anchor="w", padx=20, pady=(12, 2))
        ttk.Entry(top, textvariable=self.target_var, width=40).pack(anchor="w", padx=20)
        btns = tk.Frame(top, bg=CARD)
        btns.pack(fill="x", padx=20, pady=16)
        tk.Button(btns, text="진단 시작", command=self.ok, bg=ACCENT, fg="#FFFFFF", relief="flat", padx=14, pady=5,
                  font=fonts["bold"]).pack(side="right")
        tk.Button(btns, text="취소", command=self.cancel, relief="flat", padx=14, pady=5,
                  font=fonts["body"]).pack(side="right", padx=8)
        top.protocol("WM_DELETE_WINDOW", self.cancel)
        top.grab_set()

    def ok(self):
        self.result = {"type": self.type_var.get(), "scope": self.scope_var.get(),
                       "target_process": self.target_var.get().strip()}
        self.top.destroy()

    def cancel(self):
        self.top.destroy()


def find_latest_dump():
    """가장 최근 수집기 덤프의 engine-snapshot.json 경로 (없으면 None).
    찾는 곳: CE_DUMPS 환경 변수 → collector 폴더 아래의 dumps 폴더들"""
    roots = []
    if os.environ.get("CE_DUMPS"):
        roots.append(os.environ["CE_DUMPS"])
    roots.append(os.path.join(REPO, "collector"))
    best = None
    best_time = 0.0
    for r in range(len(roots)):
        found = glob.glob(os.path.join(roots[r], "**", "engine-snapshot.json"), recursive=True)
        for i in range(len(found)):
            t = os.path.getmtime(found[i])
            if t > best_time:
                best = found[i]
                best_time = t
    return best


def compare_evidence(a, b):
    """근거를 기여도(contribution) 큰 순으로"""
    ca = a.get("contribution", 0)
    cb = b.get("contribution", 0)
    if ca > cb:
        return -1
    if ca < cb:
        return 1
    return 0


def main():
    root = tk.Tk()
    try:
        engine_path = find_engine()
    except EngineError as e:
        root.withdraw()
        messagebox.showerror("엔진을 찾을 수 없음", str(e))
        return 1
    app = App(root, engine_path)
    if len(sys.argv) > 1 and os.path.exists(sys.argv[1]):
        root.after(200, functools.partial(app.open_snapshot, sys.argv[1]))
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
