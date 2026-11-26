const header = document.querySelector("[data-header]");
const nav = document.querySelector("[data-nav]");
const navToggle = document.querySelector("[data-nav-toggle]");

const updateHeader = () => {
  header?.classList.toggle("is-scrolled", window.scrollY > 20);
};

const closeNav = () => {
  if (!nav || !navToggle) return;
  nav.classList.remove("is-open");
  navToggle.setAttribute("aria-expanded", "false");
  navToggle.setAttribute("aria-label", "打开导航");
  document.body.classList.remove("nav-open");
};

navToggle?.addEventListener("click", () => {
  const isOpen = nav?.classList.toggle("is-open") ?? false;
  navToggle.setAttribute("aria-expanded", String(isOpen));
  navToggle.setAttribute("aria-label", isOpen ? "关闭导航" : "打开导航");
  document.body.classList.toggle("nav-open", isOpen);
});

nav?.querySelectorAll("a").forEach((link) => link.addEventListener("click", closeNav));

window.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && nav?.classList.contains("is-open")) {
    closeNav();
    navToggle?.focus();
  }
});

window.addEventListener("resize", () => {
  if (window.innerWidth > 860) closeNav();
});

window.addEventListener("scroll", updateHeader, { passive: true });
updateHeader();

/* Progressive Scroll Reveal with anchor-jump safety */
const revealItems = Array.from(document.querySelectorAll("[data-reveal]"));
const revealElement = (el) => el.classList.add("is-visible");

if (!document.documentElement.classList.contains("js-reveal")) {
  revealItems.forEach(revealElement);
} else {
  const revealAboveOrInView = () => {
    const viewBottom = window.innerHeight * 1.05;
    revealItems.forEach((item) => {
      if (item.classList.contains("is-visible")) return;
      const rect = item.getBoundingClientRect();
      if (rect.top <= viewBottom) revealElement(item);
    });
  };

  const revealObserver = new IntersectionObserver(
    (entries, observer) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        revealElement(entry.target);
        observer.unobserve(entry.target);
      });
    },
    { rootMargin: "0px 0px -6%", threshold: 0.04 },
  );

  revealItems.forEach((item) => revealObserver.observe(item));
  revealAboveOrInView();

  window.addEventListener("hashchange", () => {
    setTimeout(revealAboveOrInView, 80);
  });
  window.addEventListener("scroll", revealAboveOrInView, { passive: true });
}

/* Interactive Control Demo (Pitch & Formant presets/sliders) */
const controlDemo = document.querySelector("[data-control-demo]");
if (controlDemo) {
  const pitchInput = controlDemo.querySelector("[data-demo-pitch]");
  const formantInput = controlDemo.querySelector("[data-demo-formant]");
  const pitchValue = controlDemo.querySelector("[data-demo-pitch-value]");
  const formantValue = controlDemo.querySelector("[data-demo-formant-value]");
  const presetButtons = Array.from(controlDemo.querySelectorAll("[data-preset-pitch]"));

  const formatSignedInt = (n) => (n > 0 ? `+${n}` : String(n));
  const formatSignedFloat = (n) => (n > 0 ? `+${n.toFixed(2)}` : n.toFixed(2));

  const syncControlReadout = () => {
    if (!pitchInput || !formantInput) return;
    const p = Number(pitchInput.value);
    const f = Number(formantInput.value);
    if (pitchValue) pitchValue.textContent = formatSignedInt(p);
    if (formantValue) formantValue.textContent = formatSignedFloat(f);
    presetButtons.forEach((btn) => {
      const match =
        Number(btn.dataset.presetPitch) === p &&
        Math.abs(Number(btn.dataset.presetFormant) - f) < 0.01;
      btn.classList.toggle("is-active", match);
    });
  };

  presetButtons.forEach((btn) => {
    btn.addEventListener("click", () => {
      if (pitchInput) pitchInput.value = btn.dataset.presetPitch ?? "0";
      if (formantInput) formantInput.value = btn.dataset.presetFormant ?? "0";
      syncControlReadout();
    });
  });

  pitchInput?.addEventListener("input", syncControlReadout);
  formantInput?.addEventListener("input", syncControlReadout);
  syncControlReadout();
}

/* Real Audio Audition Player (Before / After Comparison) */
const audioButtons = Array.from(document.querySelectorAll("[data-audio-src]"));
if (audioButtons.length > 0) {
  const player = new Audio();
  player.preload = "none";

  let activeBtn = null;

  const formatDuration = (sec) => {
    if (!Number.isFinite(sec) || sec <= 0) return "0:03";
    const s = Math.max(0, Math.round(sec));
    const m = Math.floor(s / 60);
    const rem = String(s % 60).padStart(2, "0");
    return `${m}:${rem}`;
  };

  const resetButtonState = (btn) => {
    if (!btn) return;
    btn.classList.remove("is-playing");
    btn.setAttribute("aria-pressed", "false");
    const label = btn.querySelector(".play-label");
    if (label) {
      label.textContent = btn.dataset.audioId === "original" ? "播放原声" : "试听变声";
    }
    const bar = btn.querySelector(".audio-progress i");
    if (bar) bar.style.width = "0%";
    const time = btn.querySelector("[data-audio-time]");
    if (time) time.textContent = "0:03";
    btn.closest("[data-voice-card]")?.classList.remove("is-playing");
  };

  const stopActive = () => {
    player.pause();
    player.currentTime = 0;
    resetButtonState(activeBtn);
    activeBtn = null;
  };

  player.addEventListener("timeupdate", () => {
    if (!activeBtn || !player.duration) return;
    const pct = Math.min(100, (player.currentTime / player.duration) * 100);
    const bar = activeBtn.querySelector(".audio-progress i");
    if (bar) bar.style.width = `${pct}%`;
    const time = activeBtn.querySelector("[data-audio-time]");
    if (time) time.textContent = formatDuration(player.duration - player.currentTime);
  });

  player.addEventListener("ended", stopActive);

  player.addEventListener("error", () => {
    if (!activeBtn) return;
    const label = activeBtn.querySelector(".play-label");
    if (label) label.textContent = "加载失败";
    activeBtn.classList.remove("is-playing");
    activeBtn.setAttribute("aria-pressed", "false");
    activeBtn.closest("[data-voice-card]")?.classList.remove("is-playing");
    activeBtn = null;
  });

  audioButtons.forEach((btn) => {
    btn.addEventListener("click", () => {
      const isSame = activeBtn === btn;
      if (isSame && !player.paused) {
        stopActive();
        return;
      }

      if (activeBtn) resetButtonState(activeBtn);
      activeBtn = btn;

      const src = btn.dataset.audioSrc;
      if (!src) return;

      player.src = src;
      btn.classList.add("is-playing");
      btn.setAttribute("aria-pressed", "true");
      btn.closest("[data-voice-card]")?.classList.add("is-playing");

      const label = btn.querySelector(".play-label");
      if (label) label.textContent = "正在播放";

      player.play().catch(() => {
        resetButtonState(btn);
        activeBtn = null;
      });
    });
  });
}

/* Workspace Interactive Tabs (Realtime / Store / File) */
const wsTabs = Array.from(document.querySelectorAll("[data-ws-tab]"));
const wsImage = document.querySelector("[data-ws-image]");
const wsCaption = document.querySelector("[data-ws-caption]");
const wsPanel = document.querySelector("#workspace-stage");

const wsData = {
  realtime: {
    src: "assets/rvc-studio-workbench.png",
    alt: "RVC Studio v1.1.2 实时变声工作台实拍界面",
    caption: "实时变声工作台 · 选音色、接设备、调音高与监听，一屏完成",
  },
  store: {
    src: "assets/rvc-studio-store.png",
    alt: "RVC Studio v1.1.2 音色商城界面，支持免费与会员音色筛选、搜索、先试听再使用",
    caption: "音色商城 · 先听样音满意再点「使用」，支持搜索与免费/会员筛选",
  },
  file: {
    src: "assets/rvc-studio-file.png",
    alt: "RVC Studio v1.1.2 文件变声界面，支持常见音频导入、一键转换、原声对比试听并另存为 WAV",
    caption: "文件变声 · 导入录音一键换声线，试听对比后直接另存为 WAV",
  },
};

wsTabs.forEach((tab) => {
  tab.addEventListener("click", () => {
    const key = tab.dataset.wsTab;
    const item = key ? wsData[key] : null;
    if (!item || !wsImage) return;

    wsTabs.forEach((t) => {
      const selected = t === tab;
      t.classList.toggle("is-active", selected);
      t.setAttribute("aria-selected", String(selected));
    });

    if (wsPanel) wsPanel.setAttribute("aria-labelledby", tab.id);
    if (wsCaption) wsCaption.textContent = item.caption;

    if (wsImage.getAttribute("src") !== item.src) {
      wsImage.classList.add("is-switching");
      wsImage.setAttribute("src", item.src);
      wsImage.setAttribute("alt", item.alt);
      setTimeout(() => wsImage.classList.remove("is-switching"), 140);
    }
  });
});

/* FAQ Single-Open Accordion */
document.querySelectorAll("[data-faq] details").forEach((details) => {
  details.addEventListener("toggle", () => {
    if (!details.open) return;
    document.querySelectorAll("[data-faq] details[open]").forEach((openDetails) => {
      if (openDetails !== details) openDetails.removeAttribute("open");
    });
  });
});

const year = document.querySelector("[data-year]");
if (year) year.textContent = new Date().getFullYear();
