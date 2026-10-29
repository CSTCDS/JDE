// Bandeau commun Utils.Colas : navigation entre utilitaires, utilisateur Windows, version,
// et (option data-connection-selector sur <body>) choix de la connexion Oracle de référence.
(function () {
  'use strict';

  var script = document.currentScript;
  // Racine de l'application déduite de l'emplacement du script (fonctionne sous un sous-chemin IIS).
  var root = new URL('../', script.src).href;
  var cookiePath = new URL(root).pathname;
  var COLORS = { rouge: '#c0392b', vert: '#27ae60', bleuMarine: '#003366', bleu: '#2980b9', gris: '#7f8c8d', orange: '#e67e22' };

  function esc(s) {
    var d = document.createElement('div');
    d.textContent = s == null ? '' : String(s);
    return d.innerHTML;
  }

  function getJson(path) {
    return fetch(root + path, { cache: 'no-store', credentials: 'same-origin' }).then(function (r) {
      var type = r.headers.get('content-type') || '';
      if (!r.ok || type.indexOf('json') < 0) throw new Error('HTTP ' + r.status + ' sur ' + path);
      return r.json();
    });
  }

  function getCookie(name) {
    var m = document.cookie.match(new RegExp('(^|; )' + name + '=([^;]+)'));
    return m ? decodeURIComponent(m[2]) : '';
  }

  function setBannerColor(colorNameOrHex) {
    var c = COLORS[colorNameOrHex] || (/^#[0-9a-f]{3,6}$/i.test(colorNameOrHex || '') ? colorNameOrHex : '#003366');
    document.documentElement.style.setProperty('--banner-color', c);
  }

  window.UtilsColas = {
    root: root,
    api: function (path) { return root + path; },
    getJson: getJson,
    getCookie: getCookie,
    colorOf: function (name) { return COLORS[name] || '#003366'; },
    setBannerColor: setBannerColor,
    escapeHtml: esc
  };

  function render() {
    var placeholder = document.getElementById('bandeau-placeholder');
    if (!placeholder) return;
    var current = document.body.getAttribute('data-utility') || '';
    placeholder.innerHTML =
      '<div id="bandeau"><div class="left">' +
      '<a class="brand" href="' + esc(root) + '">UTILS.COLAS</a><nav id="bandeau-nav"></nav></div>' +
      '<div class="right"><span id="bandeau-conn"></span><span id="bandeau-user"></span></div></div>';

    getJson('config/utilities.json').then(function (list) {
      document.getElementById('bandeau-nav').innerHTML = list.map(function (u) {
        return '<a href="' + esc(root + u.url) + '"' + (u.id === current ? ' class="active"' : '') + '>' + esc(u.title) + '</a>';
      }).join('');
    }).catch(function () {});

    getJson('api/windows-user').then(function (b) {
      document.getElementById('bandeau-user').textContent = b.user || '';
    }).catch(function () {});

    var footer = document.getElementById('version');
    if (footer) {
      getJson('api/version').then(function (b) {
        footer.textContent = 'Utils.Colas — Version ' + b.version + ' - Mise à jour : ' + b.built;
      }).catch(function () {});
    }

    if (document.body.hasAttribute('data-connection-selector')) renderConnectionSelector();
  }

  // Connexion de référence mémorisée dans le cookie JDE-Connection (lu par l'API, cf. JDE Compare).
  function renderConnectionSelector() {
    getJson('api/connections').then(function (b) {
      var list = b.connections || [];
      if (list.length === 0) return;
      var names = list.map(function (c) { return c.name; });
      var current = getCookie('JDE-Connection');
      if (names.indexOf(current) < 0) current = names.indexOf(b.defaultConnection) >= 0 ? b.defaultConnection : names[0];

      var holder = document.getElementById('bandeau-conn');
      holder.innerHTML = '<span class="conn-label">Référence :</span> <select id="bannerConnSelect">' +
        list.map(function (c) {
          return '<option value="' + esc(c.name) + '"' + (c.name === current ? ' selected' : '') + '>' +
            esc(c.name + ' (' + (c.environment || '') + ')' + (c.available ? '' : ' — non configurée')) + '</option>';
        }).join('') + '</select>';

      function apply(name) {
        document.cookie = 'JDE-Connection=' + encodeURIComponent(name) + ';path=' + cookiePath + ';SameSite=Lax;Secure';
        var conn = list.filter(function (c) { return c.name === name; })[0];
        setBannerColor(conn ? conn.bannerColor : '');
        window.dispatchEvent(new CustomEvent('utils:connection', { detail: name }));
      }
      var select = document.getElementById('bannerConnSelect');
      select.addEventListener('change', function () { apply(select.value); });
      apply(current);
    }).catch(function (err) {
      document.getElementById('bandeau-conn').textContent = 'Connexions indisponibles : ' + err.message;
    });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', render);
  else render();
})();
