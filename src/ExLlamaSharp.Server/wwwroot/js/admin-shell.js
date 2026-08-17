// ExLlamaSharp Admin shell — sidebar toggle (Kortexio body classes).
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
