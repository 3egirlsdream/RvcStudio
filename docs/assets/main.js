(() => {
  /* Sticky Header scroll elevation + post-hero CTA reveal (macexplorer.top pattern) */
  const header = document.querySelector("[data-header]");
  const heroActions = document.querySelector("[data-hero-actions]");
  const mobileMenu = document.querySelector("[data-mobile-menu]");

  const updateHeader = () => {
    if (!header) return;
    header.classList.toggle("is-scrolled", window.scrollY > 8);
    if (heroActions) {
      const rect = heroActions.getBoundingClientRect();
      header.classList.toggle("is-past-hero", rect.bottom < header.offsetHeight);
    }
  };

  window.addEventListener("scroll", updateHeader, { passive: true });
  window.addEventListener("resize", updateHeader, { passive: true });
  updateHeader();

  if (mobileMenu) {
    mobileMenu.querySelectorAll("a").forEach((link) => {
      link.addEventListener("click", () => {
        mobileMenu.removeAttribute("open");
      });
    });

    document.addEventListener("click", (event) => {
      if (mobileMenu.open && !mobileMenu.contains(event.target)) {
        mobileMenu.removeAttribute("open");
      }
    });

    document.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && mobileMenu.open) {
        mobileMenu.removeAttribute("open");
      }
    });
  }

  /* Hero Layered Showcase Stack (click back window or pill to bring to front) */
  const showcase = document.querySelector("[data-showcase]");
  if (showcase) {
    const layers = Array.from(showcase.querySelectorAll("[data-layer-id]"));
    const triggers = Array.from(showcase.querySelectorAll("[data-layer-trigger]"));
    let order = ["settings", "file", "store", "workbench"];

    const applyOrder = () => {
      const frontId = order[order.length - 1];
      layers.forEach((layer) => {
        const id = layer.dataset.layerId;
        const idx = order.indexOf(id);
        const isFront = id === frontId;
        layer.style.setProperty("--layer", String(idx));
        layer.classList.toggle("is-front", isFront);
        layer.setAttribute("aria-pressed", String(isFront));
      });
      triggers.forEach((btn) => {
        const active = btn.dataset.layerTrigger === frontId;
        btn.classList.toggle("is-active", active);
        btn.setAttribute("aria-selected", String(active));
      });
    };

    const bringToFront = (id) => {
      if (!order.includes(id) || order[order.length - 1] === id) return;
      order = order.filter((item) => item !== id);
      order.push(id);
      applyOrder();
    };

    layers.forEach((layer) => {
      layer.addEventListener("click", () => {
        if (layer.dataset.layerId) bringToFront(layer.dataset.layerId);
      });
    });

    triggers.forEach((btn) => {
      btn.addEventListener("click", () => {
        if (btn.dataset.layerTrigger) bringToFront(btn.dataset.layerTrigger);
      });
    });

    applyOrder();
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

    const resetButton = (btn) => {
      if (!btn) return;
      btn.classList.remove("is-playing");
      btn.setAttribute("aria-pressed", "false");
      const label = btn.querySelector(".audio-btn-label");
      if (label) {
        label.textContent = btn.dataset.audioId === "original" ? "播放原声" : "试听变声";
      }
      const bar = btn.querySelector(".audio-track i");
      if (bar) bar.style.width = "0%";
      const time = btn.querySelector("[data-audio-time]");
      if (time) time.textContent = "0:03";
      btn.closest("[data-voice-card]")?.classList.remove("is-playing");
    };

    const stopActive = () => {
      player.pause();
      player.currentTime = 0;
      resetButton(activeBtn);
      activeBtn = null;
    };

    player.addEventListener("timeupdate", () => {
      if (!activeBtn || !player.duration) return;
      const pct = Math.min(100, (player.currentTime / player.duration) * 100);
      const bar = activeBtn.querySelector(".audio-track i");
      if (bar) bar.style.width = `${pct}%`;
      const time = activeBtn.querySelector("[data-audio-time]");
      if (time) time.textContent = formatDuration(player.duration - player.currentTime);
    });

    player.addEventListener("ended", stopActive);

    player.addEventListener("error", () => {
      if (!activeBtn) return;
      const label = activeBtn.querySelector(".audio-btn-label");
      if (label) label.textContent = "加载失败";
      activeBtn.classList.remove("is-playing");
      activeBtn.setAttribute("aria-pressed", "false");
      activeBtn.closest("[data-voice-card]")?.classList.remove("is-playing");
      activeBtn = null;
    });

    audioButtons.forEach((btn) => {
      btn.addEventListener("click", () => {
        if (activeBtn === btn && !player.paused) {
          stopActive();
          return;
        }
        if (activeBtn) resetButton(activeBtn);
        activeBtn = btn;

        const src = btn.dataset.audioSrc;
        if (!src) return;

        player.src = src;
        btn.classList.add("is-playing");
        btn.setAttribute("aria-pressed", "true");
        btn.closest("[data-voice-card]")?.classList.add("is-playing");
        const label = btn.querySelector(".audio-btn-label");
        if (label) label.textContent = "正在播放";

        player.play().catch(() => {
          resetButton(btn);
          activeBtn = null;
        });
      });
    });
  }

  /* FAQ Single-Open Accordion */
  document.querySelectorAll("[data-faq] details").forEach((details) => {
    details.addEventListener("toggle", () => {
      if (!details.open) return;
      document.querySelectorAll("[data-faq] details[open]").forEach((other) => {
        if (other !== details) other.removeAttribute("open");
      });
    });
  });

  /* Copy QQ Group Number */
  const copyBtn = document.querySelector("[data-copy-qq]");
  const copyLabel = document.querySelector("[data-copy-qq-label]");
  if (copyBtn && copyLabel) {
    let resetTimer = null;
    const qq = copyBtn.dataset.copyQq || "791129392";
    copyBtn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(qq);
        copyLabel.textContent = `已复制群号 ${qq}`;
      } catch {
        copyLabel.textContent = `QQ 群：${qq}`;
      }
      clearTimeout(resetTimer);
      resetTimer = setTimeout(() => {
        copyLabel.textContent = `QQ 群 ${qq}`;
      }, 2200);
    });
  }

  /* Footer Year */
  const yearEl = document.querySelector("[data-year]");
  if (yearEl) yearEl.textContent = String(new Date().getFullYear());
})();
