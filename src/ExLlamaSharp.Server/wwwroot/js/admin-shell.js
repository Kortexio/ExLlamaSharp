// ExLlamaSharp Admin shell — sidebar toggle + kill stale service workers.
(function () {
  if (!('serviceWorker' in navigator)) return;
  navigator.serviceWorker.getRegistrations().then(function (regs) {
    regs.forEach(function (reg) { reg.unregister(); });
  });
  if (window.caches) {
    caches.keys().then(function (keys) {
      keys.forEach(function (key) { caches.delete(key); });
    });
  }
})();

window.exLlamaSharpAdmin = window.exLlamaSharpAdmin || {
  toggleSidebar: function () {
    var body = document.body;
    if (!body) return;
    if (window.innerWidth <= 992) {
      body.classList.toggle("kx-sidebar-open");
      body.classList.remove("kx-sidebar-collapsed");
    } else {
      body.classList.toggle("kx-sidebar-collapsed");
      body.classList.remove("kx-sidebar-open");
    }
  },
  closeSidebarOverlay: function () {
    document.body.classList.remove("kx-sidebar-open");
  },
  setApiKeyCookie: function (key) {
    if (!key) return;
    document.cookie = "exllamasharp_key=" + encodeURIComponent(key) + "; path=/; SameSite=Lax";
  },
  hasApiKeyCookie: function () {
    return document.cookie.split(";").some(function (c) {
      return c.trim().indexOf("exllamasharp_key=") === 0;
    });
  },
  clearApiKeyCookie: function () {
    document.cookie = "exllamasharp_key=; path=/; Max-Age=0";
  }
};
