// ExLlamaSharp Admin shell — AdminLTE sidebar + session helpers + kill stale SWs.
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
    // Prefer AdminLTE PushMenu when available
    try {
      if (window.$ && typeof window.$.fn.PushMenu === 'function') {
        window.$('[data-widget="pushmenu"]').PushMenu('toggle');
        return;
      }
    } catch (e) { /* fall through */ }

    if (window.innerWidth <= 992) {
      body.classList.toggle('sidebar-open');
      body.classList.remove('sidebar-collapse');
    } else {
      body.classList.toggle('sidebar-collapse');
      body.classList.remove('sidebar-open');
    }
  },
  closeSidebarOverlay: function () {
    document.body.classList.remove('sidebar-open');
  },
  setApiKeyCookie: function (key) {
    if (!key) return;
    this.clearApiKeyCookie();
    var secure = window.location.protocol === 'https:' ? '; Secure' : '';
    document.cookie = 'exllamasharp_key=' + encodeURIComponent(key) +
      '; path=/; SameSite=Lax; Max-Age=604800' + secure;
  },
  hasApiKeyCookie: function () {
    return !!this.getApiKeyCookie();
  },
  getApiKeyCookie: function () {
    var parts = document.cookie.split(';');
    for (var i = 0; i < parts.length; i++) {
      var c = parts[i].trim();
      if (c.indexOf('exllamasharp_key=') === 0) {
        return decodeURIComponent(c.substring('exllamasharp_key='.length));
      }
    }
    return '';
  },
  clearApiKeyCookie: function () {
    document.cookie = 'exllamasharp_key=; path=/; Max-Age=0';
    document.cookie = 'exllamasharp_key=; path=/; Max-Age=0; SameSite=Lax';
  },
  openSession: function (key) {
    this.setApiKeyCookie(key);
    return fetch('/api/v1/ui-session', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin',
      body: JSON.stringify({ key: key })
    }).then(function (res) {
      if (!res.ok) {
        throw new Error('ui-session ' + res.status);
      }
      return res;
    });
  },
  logoutSession: function () {
    var self = this;
    return fetch('/api/v1/ui-session/logout', {
      method: 'POST',
      credentials: 'same-origin'
    }).finally(function () {
      self.clearApiKeyCookie();
    });
  }
};
