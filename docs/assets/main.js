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

window.addEventListener("resize", () => {
  if (window.innerWidth > 820) closeNav();
});

window.addEventListener("scroll", updateHeader, { passive: true });
updateHeader();

const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
const revealItems = document.querySelectorAll("[data-reveal]");

if (reducedMotion || !("IntersectionObserver" in window)) {
  revealItems.forEach((item) => item.classList.add("is-visible"));
} else {
  const revealObserver = new IntersectionObserver(
    (entries, observer) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        entry.target.classList.add("is-visible");
        observer.unobserve(entry.target);
      });
    },
    { rootMargin: "0px 0px -8%", threshold: 0.08 },
  );

  revealItems.forEach((item) => revealObserver.observe(item));
}

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
