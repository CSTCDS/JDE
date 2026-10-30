// JDE Compare (ex NERcompare) - Utils.Colas
(function () {
  'use strict';

  // Racine calculée par common/banner.js (fonctionne sous un sous-chemin IIS).
  const API_BASE = window.UtilsColas.api('api/jdecompare');
  let selectedObjects = [];
  let objectToProject = {}; // mapping objet → projet pour regroupement
  let currentMode = 'object';

  // --- Initialisation ---
  document.addEventListener('DOMContentLoaded', async () => {
    await loadEnvironments();
    await loadStatuses();
    setupEventListeners();
    // Changement de connexion de référence dans le bandeau commun
    window.addEventListener('utils:connection', loadStatuses);
  });

  // --- Chargement des environnements ---
  async function loadEnvironments() {
    try {
      const resp = await fetch(`${API_BASE}/environments`);
      const envs = await resp.json();
      const srcSelect = document.getElementById('envSource');
      const tgtSelect = document.getElementById('envTarget');

      srcSelect.innerHTML = '';
      tgtSelect.innerHTML = '';

      const avail = envs.filter(e => e.available !== false);
      // Dernier choix mémorisé (localStorage, sans expiration), sinon premier / dernier environnement disponible.
      const saved = readStore(ENVS_KEY, {}) || {};
      const usable = (name) => avail.some(e => e.environment === name);
      const first = usable(saved.source) ? saved.source : (avail.length ? avail[0].environment : null);
      const last = usable(saved.target) ? saved.target : (avail.length ? avail[avail.length - 1].environment : null);
      envs.forEach(e => {
        const label = e.environment + (e.available === false ? ' (identifiants manquants)' : '');
        const s = new Option(label, e.environment, e.environment === first, e.environment === first);
        const t = new Option(label, e.environment, e.environment === last, e.environment === last);
        s.disabled = t.disabled = e.available === false;
        srcSelect.add(s);
        tgtSelect.add(t);
      });
      syncQuickEnvs();
    } catch (err) {
      console.error('Erreur chargement environnements:', err);
    }
  }

  function saveEnvironments() {
    const source = document.getElementById('envSource').value;
    const target = document.getElementById('envTarget').value;
    if (source || target) writeStore(ENVS_KEY, { source, target, at: Date.now() });
    syncQuickEnvs();
  }

  // Copie la liste et le choix du formulaire dans la barre de recherche des résultats.
  function syncQuickEnvs() {
    [['envSource', 'quickSource'], ['envTarget', 'quickTarget']].forEach(([from, to]) => {
      const a = document.getElementById(from);
      const b = document.getElementById(to);
      if (!a || !b) return;
      if (b.options.length !== a.options.length) b.innerHTML = a.innerHTML;
      b.value = a.value;
    });
  }

  // --- Chargement des statuts ---
  async function loadStatuses() {
    try {
      const conn = getConnection();
      const resp = await fetch(`${API_BASE}/statuses`, {
        headers: conn ? { 'X-JDE-Connection': conn } : {}
      });
      if (!resp.ok) {
        console.warn('[JDE Compare] Statuts: erreur ' + resp.status);
        return;
      }
      const statuses = await resp.json();
      if (!Array.isArray(statuses)) {
        console.warn('[JDE Compare] Statuts: réponse inattendue', statuses);
        return;
      }
      const select = document.getElementById('statusSelect');
      select.innerHTML = '<option value="">-- Choisir un statut --</option>';
      statuses.forEach(s => {
        select.add(new Option(s, s));
      });
    } catch (err) {
      console.error('Erreur chargement statuts:', err);
    }
  }

  // --- Event listeners ---
  function setupEventListeners() {
    // Mode de recherche
    document.querySelectorAll('input[name="searchMode"]').forEach(radio => {
      radio.addEventListener('change', (e) => {
        currentMode = e.target.value;
        document.getElementById('searchObject').style.display = currentMode === 'object' ? '' : 'none';
        document.getElementById('searchProject').style.display = currentMode === 'project' ? '' : 'none';
        document.getElementById('searchStatus').style.display = currentMode === 'status' ? '' : 'none';
        updateCompareButton();
      });
    });

    // Autocomplétion objet (historique + F9860)
    attachAutocomplete(document.getElementById('objectInput'), document.getElementById('objectSuggestions'),
      () => 'object', (it) => addObject(it.value, it.tag, it.desc));

    // Autocomplétion projet (historique + F98220)
    attachAutocomplete(document.getElementById('projectInput'), document.getElementById('projectSuggestions'),
      () => 'project', (it) => selectProject(it.value, it.desc));

    // Environnements mémorisés (sans expiration) à chaque changement
    ['envSource', 'envTarget'].forEach(id => document.getElementById(id).addEventListener('change', saveEnvironments));

    // Statut
    document.getElementById('statusSelect').addEventListener('change', (e) => {
      if (e.target.value) loadProjectsByStatus(e.target.value);
    });

    // Boutons
    document.getElementById('btnCompare').addEventListener('click', runComparison);
    document.getElementById('btnClear').addEventListener('click', clearAll);

    // Filtre résultats
    document.getElementById('filterResults').addEventListener('input', filterResults);

    // Fermer suggestions au clic extérieur
    document.addEventListener('click', (e) => {
      if (!e.target.closest('.autocomplete-wrapper')) {
        hideSuggestions('objectSuggestions');
        hideSuggestions('projectSuggestions');
        hideSuggestions('quickSuggestions');
      }
    });

    // Recherche complémentaire depuis la page résultats
    setupQuickSearch();
  }

  // --- Connexion de référence ---
  function getConnection() {
    // Lire depuis le cookie ou le bandeau
    const cookie = document.cookie.split(';').find(c => c.trim().startsWith('JDE-Connection='));
    if (cookie) return cookie.split('=')[1].trim();
    const bannerSelect = document.querySelector('#bandeau select, #bandeau-placeholder select');
    if (bannerSelect) return bannerSelect.value;
    return '';
  }

  // --- Autocomplétion objets ---
  // Message d'erreur lisible d'une réponse API (ProblemDetails.detail, {error}, sinon statut HTTP).
  function apiErrorMessage(resp, text) {
    try {
      const j = JSON.parse(text);
      if (j.detail || j.error || j.title) return j.detail || j.error || j.title;
    } catch { /* réponse non JSON */ }
    return `Erreur serveur (HTTP ${resp.status}${resp.statusText ? ' ' + resp.statusText : ''})`;
  }

  // --- Historique des recherches (localStorage, propre au navigateur) ---
  const HISTORY_KEY = 'utilsColas.jdeCompare.history';
  const ENVS_KEY = 'utilsColas.jdeCompare.envs';
  const HISTORY_MAX = 50;

  function readStore(key, fallback) {
    try { const v = JSON.parse(localStorage.getItem(key)); return v ?? fallback; } catch { return fallback; }
  }
  function writeStore(key, value) {
    try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* stockage indisponible ou plein */ }
  }

  function getHistory(kind) {
    const list = readStore(HISTORY_KEY, []);
    return Array.isArray(list) ? list.filter(h => h && h.kind === kind && h.value) : [];
  }

  // items : [{ value, desc, tag }] ; les plus récents en tête, sans doublon.
  function addHistory(kind, items) {
    let list = readStore(HISTORY_KEY, []);
    if (!Array.isArray(list)) list = [];
    const at = Date.now();
    [...items].reverse().forEach(it => {
      const value = (it.value || '').trim().toUpperCase();
      if (!value) return;
      list = list.filter(h => !(h.kind === kind && h.value === value));
      list.unshift({ kind, value, desc: it.desc || '', tag: it.tag || '', at });
    });
    writeStore(HISTORY_KEY, list.slice(0, HISTORY_MAX));
  }

  function removeHistory(kind, value) {
    writeStore(HISTORY_KEY, readStore(HISTORY_KEY, []).filter(h => !(h.kind === kind && h.value === value)));
  }

  function clearHistory(kind) {
    writeStore(HISTORY_KEY, readStore(HISTORY_KEY, []).filter(h => h.kind !== kind));
  }

  // --- Autocomplétion (objet ou projet) : historique local + recherche serveur (min. 3 caractères) ---
  // getKind() : 'object' | 'project' ; onPick({ value, desc, tag }).
  function attachAutocomplete(input, box, getKind, onPick) {
    let debounce = null;
    let seq = 0;
    const refresh = () => {
      clearTimeout(debounce);
      debounce = setTimeout(() => showSuggestions(input, box, getKind(), onPick, ++seq, () => seq), 250);
    };
    input.addEventListener('input', refresh);
    input.addEventListener('focus', refresh);
    input.addEventListener('keydown', (e) => {
      const items = [...box.querySelectorAll('.suggestion-item[data-value]')];
      const cur = box.querySelector('.suggestion-item.active');
      let i = items.indexOf(cur);
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        if (!items.length || !box.classList.contains('visible')) return;
        e.preventDefault();
        i = e.key === 'ArrowDown' ? (i + 1) % items.length : (i <= 0 ? items.length - 1 : i - 1);
        items.forEach(x => x.classList.toggle('active', x === items[i]));
        items[i].scrollIntoView({ block: 'nearest' });
      } else if (e.key === 'Escape') {
        box.classList.remove('visible');
      } else if (e.key === 'Enter') {
        e.preventDefault();
        if (cur && box.classList.contains('visible')) { cur.click(); return; }
        const val = input.value.trim();
        if (val) { onPick({ value: val.toUpperCase(), desc: '', tag: '' }); input.value = ''; box.classList.remove('visible'); }
      }
    });
  }

  async function showSuggestions(input, box, kind, onPick, mySeq, currentSeq) {
    const q = input.value.trim().toUpperCase();
    const history = getHistory(kind).filter(h => !q || h.value.includes(q) || (h.desc || '').toUpperCase().includes(q)).slice(0, 10);
    let server = [];
    let error = '';
    if (q.length >= 3) {
      try {
        const conn = getConnection();
        const url = kind === 'project'
          ? `${API_BASE}/projects?q=${encodeURIComponent(q)}&limit=15`
          : `${API_BASE}/objects?q=${encodeURIComponent(q)}&limit=15`;
        const resp = await fetch(url, { headers: conn ? { 'X-JDE-Connection': conn } : {} });
        if (!resp.ok) {
          const errText = await resp.text();
          console.error('[JDE Compare] autocomplete erreur:', errText);
          error = apiErrorMessage(resp, errText);
        } else {
          const data = await resp.json();
          server = (Array.isArray(data) ? data : []).map(d => kind === 'project'
            ? { value: d.projectId, desc: d.description, tag: 'St:' + (d.status || '') }
            : { value: d.objectName, desc: d.description, tag: d.objectType });
        }
      } catch (err) {
        error = 'Erreur: ' + err.message;
      }
    }
    if (mySeq !== currentSeq()) return; // une saisie plus récente a pris le relais

    const known = new Set(history.map(h => h.value));
    server = server.filter(s => !known.has((s.value || '').trim().toUpperCase()));
    box.innerHTML = '';
    const addItem = (it, isHistory) => {
      const div = document.createElement('div');
      div.className = 'suggestion-item' + (isHistory ? ' suggestion-history' : '');
      div.dataset.value = it.value;
      div.innerHTML = `<span>${isHistory ? '<span class="hist-icon" title="Historique">🕘</span> ' : ''}<span class="obj-name">${escHtml(it.value)}</span> <span class="obj-desc">${escHtml(it.desc || '')}</span></span>`
        + `<span class="obj-type">${escHtml(it.tag || '')}${isHistory ? ' <span class="hist-remove" title="Retirer de l\'historique">×</span>' : ''}</span>`;
      div.addEventListener('mousedown', (e) => e.preventDefault()); // garde le focus dans le champ
      div.addEventListener('click', (e) => {
        if (e.target.classList.contains('hist-remove')) {
          e.stopPropagation();
          removeHistory(kind, it.value);
          showSuggestions(input, box, kind, onPick, mySeq, currentSeq);
          return;
        }
        onPick({ value: it.value, desc: it.desc || '', tag: it.tag || '' });
        input.value = '';
        box.classList.remove('visible');
      });
      box.appendChild(div);
    };
    const header = (text, action) => {
      const h = document.createElement('div');
      h.className = 'suggestion-header';
      h.innerHTML = `<span>${text}</span>` + (action ? `<a href="#" class="hist-clear">${action}</a>` : '');
      const link = h.querySelector('.hist-clear');
      if (link) link.addEventListener('mousedown', (e) => {
        e.preventDefault();
        clearHistory(kind);
        showSuggestions(input, box, kind, onPick, mySeq, currentSeq);
      });
      box.appendChild(h);
    };

    if (history.length) {
      header('Recherches récentes', q ? '' : 'Effacer l\'historique');
      history.forEach(h => addItem(h, true));
    }
    if (q.length >= 3) {
      if (history.length) header(kind === 'project' ? 'Projets' : 'Objets');
      if (error) box.insertAdjacentHTML('beforeend', `<div class="suggestion-item" style="color:red;cursor:default;">${escHtml(error)}</div>`);
      else if (!server.length && !history.length) box.insertAdjacentHTML('beforeend', '<div class="suggestion-item" style="color:#999;cursor:default;">Aucun résultat</div>');
      server.forEach(s => addItem(s, false));
    }
    box.classList.toggle('visible', box.children.length > 0);
  }
  // --- Sélection d'un projet → charger ses objets ---
  async function selectProject(projectId, desc) {
    const infoBox = document.getElementById('projectInfo');
    infoBox.innerHTML = '<span class="spinner"></span> Chargement des objets du projet...';
    addHistory('project', [{ value: projectId, desc: desc || '' }]);

    try {
      const conn = getConnection();
      const resp = await fetch(`${API_BASE}/project-objects?prj=${encodeURIComponent(projectId)}`, {
        headers: { 'X-JDE-Connection': conn }
      });
      const data = await resp.json();

      let added = 0;
      if (data.objects && data.objects.length > 0) {
        data.objects.forEach(o => {
          if (!selectedObjects.find(x => x.name === o.objectName)) {
            selectedObjects.push({ name: o.objectName, type: o.objectType, desc: o.description });
            added++;
          }
          // Mapping objet → projet pour regroupement
          objectToProject[o.objectName] = projectId;
        });
        infoBox.innerHTML = `<strong>${added} objet(s) ajouté(s)</strong> depuis le projet ${escHtml(projectId)} (total: ${selectedObjects.length})`;
      } else {
        infoBox.innerHTML = 'Aucun objet trouvé pour ce projet.';
      }
      updateCompareButton();
    } catch (err) {
      infoBox.innerHTML = `<span style="color:red;">Erreur: ${escHtml(err.message)}</span>`;
    }
  }

  // --- Projets par statut ---
  async function loadProjectsByStatus(status) {
    const box = document.getElementById('statusProjects');
    box.innerHTML = '<span class="spinner"></span> Chargement des projets...';

    try {
      const conn = getConnection();
      const resp = await fetch(`${API_BASE}/projects-by-status?status=${encodeURIComponent(status)}`, {
        headers: { 'X-JDE-Connection': conn }
      });
      const data = await resp.json();

      if (!data.data || data.data.length === 0) {
        box.innerHTML = 'Aucun projet avec ce statut.';
        selectedObjects = [];
        updateCompareButton();
        return;
      }

      // Afficher la liste des projets cliquables
      box.innerHTML = `<strong>${data.count} projet(s)</strong> avec statut "${escHtml(status)}" :<br>`;
      const list = document.createElement('div');
      list.style.marginTop = '6px';
      data.data.forEach(p => {
        const link = document.createElement('a');
        link.href = '#';
        link.textContent = `${p.projectId} — ${p.description}`;
        link.style.display = 'block';
        link.style.padding = '3px 0';
        link.addEventListener('click', async (e) => {
          e.preventDefault();
          await selectProject(p.projectId, p.description);
          box.querySelectorAll('a').forEach(a => a.style.fontWeight = 'normal');
          link.style.fontWeight = 'bold';
        });
        list.appendChild(link);
      });
      box.appendChild(list);

      // Charger tous les objets de tous les projets
      selectedObjects = [];
      setStatus('Chargement de tous les objets...');
      for (const p of data.data) {
        const conn2 = getConnection();
        const resp2 = await fetch(`${API_BASE}/project-objects?prj=${encodeURIComponent(p.projectId)}`, {
          headers: { 'X-JDE-Connection': conn2 }
        });
        const d2 = await resp2.json();
        if (d2.objects) {
          d2.objects.forEach(o => {
            if (!selectedObjects.find(x => x.name === o.objectName)) {
              selectedObjects.push({ name: o.objectName, type: o.objectType, desc: o.description });
            }
          });
        }
      }
      setStatus(`${selectedObjects.length} objet(s) chargé(s) depuis ${data.count} projet(s).`);
      updateCompareButton();
    } catch (err) {
      box.innerHTML = `<span style="color:red;">Erreur: ${escHtml(err.message)}</span>`;
    }
  }

  // --- Ajout d'objet à la sélection ---
  function addObject(name, type, desc) {
    name = name.trim().toUpperCase();
    if (!name) return;
    if (selectedObjects.find(o => o.name === name)) return;
    selectedObjects.push({ name, type, desc });
    renderSelectedObjects();
    updateCompareButton();
  }

  function removeObject(name) {
    selectedObjects = selectedObjects.filter(o => o.name !== name);
    renderSelectedObjects();
    updateCompareButton();
  }

  function renderSelectedObjects() {
    const container = document.getElementById('selectedObjects');
    container.innerHTML = '';
    selectedObjects.forEach(obj => {
      const tag = document.createElement('span');
      tag.className = 'tag';
      tag.innerHTML = `${escHtml(obj.name)} <span class="tag-remove" data-name="${escHtml(obj.name)}">×</span>`;
      tag.querySelector('.tag-remove').addEventListener('click', () => removeObject(obj.name));
      container.appendChild(tag);
    });
  }

  // --- Comparaison ---
  async function runComparison() {
    const source = document.getElementById('envSource').value;
    const target = document.getElementById('envTarget').value;

    if (!source || !target) {
      setStatus('Veuillez sélectionner source et cible.');
      return;
    }
    if (source === target) {
      setStatus('Source et cible doivent être différents.');
      return;
    }
    if (selectedObjects.length === 0) {
      setStatus('Aucun objet sélectionné.');
      return;
    }

    const btn = document.getElementById('btnCompare');
    btn.disabled = true;
    setStatus('<span class="spinner"></span> Comparaison en cours...');
    saveEnvironments();
    if (currentMode === 'object') addHistory('object', selectedObjects.map(o => ({ value: o.name, desc: o.desc, tag: o.type })));

    try {
      const result = await fetchComparison(source, target, selectedObjects.map(o => o.name));
      displayResults(result);
      setStatus(`Comparaison terminée : ${result.totalObjects} objet(s).`);
      collapseFormSection();
    } catch (err) {
      setStatus(`<span style="color:red;">Erreur: ${escHtml(err.message)}</span>`);
    } finally {
      btn.disabled = false;
    }
  }

  async function fetchComparison(source, target, objects) {
    const resp = await fetch(`${API_BASE}/compare`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-JDE-Connection': getConnection()
      },
      body: JSON.stringify({ source, target, objects })
    });
    if (!resp.ok) throw new Error(apiErrorMessage(resp, await resp.text()));
    return resp.json();
  }

  // --- Recherche complémentaire depuis la page résultats ---
  let lastResult = null;
  const RESULT_COUNTERS = { '=': 'identical', '≠': 'different', '↑': 'moreRecent', '?': 'missing', '-': 'notApplicable', '✓': 'conforme', '!': 'errors' };

  // Les nouveaux objets passent en tête ; un objet déjà affiché est remplacé par son nouveau résultat.
  function mergeResults(previous, added) {
    const names = new Set(added.data.map(d => d.objectName));
    const data = [...added.data, ...previous.data.filter(d => !names.has(d.objectName))];
    const merged = { ...previous, ...added, data, totalObjects: data.length };
    Object.values(RESULT_COUNTERS).forEach(k => { merged[k] = 0; });
    data.forEach(d => { const k = RESULT_COUNTERS[d.result]; if (k) merged[k]++; });
    return merged;
  }

  function setupQuickSearch() {
    const input = document.getElementById('quickInput');
    const kind = document.getElementById('quickKind');
    const setQuickStatus = (html) => { document.getElementById('quickStatus').innerHTML = html; };

    [['quickSource', 'envSource'], ['quickTarget', 'envTarget']].forEach(([from, to]) => {
      document.getElementById(from).addEventListener('change', (e) => {
        document.getElementById(to).value = e.target.value;
        saveEnvironments();
      });
    });
    kind.addEventListener('change', () => {
      input.placeholder = kind.value === 'project' ? 'ID du projet (ou historique)' : 'Nom de l\'objet (ou historique)';
      input.value = '';
      input.focus();
    });

    async function quickCompare(item) {
      const source = document.getElementById('quickSource').value;
      const target = document.getElementById('quickTarget').value;
      if (!source || !target || source === target) {
        setQuickStatus('<span style="color:red;">Source et cible doivent être renseignés et différents.</span>');
        return;
      }
      const btn = document.getElementById('btnQuick');
      btn.disabled = true;
      try {
        let objects;
        if (kind.value === 'project') {
          setQuickStatus('<span class="spinner"></span> Objets du projet ' + escHtml(item.value) + '...');
          const resp = await fetch(`${API_BASE}/project-objects?prj=${encodeURIComponent(item.value)}`, {
            headers: { 'X-JDE-Connection': getConnection() }
          });
          if (!resp.ok) throw new Error(apiErrorMessage(resp, await resp.text()));
          const data = await resp.json();
          objects = (data.objects || []).map(o => ({ name: o.objectName, type: o.objectType, desc: o.description }));
          if (!objects.length) { setQuickStatus('Aucun objet trouvé pour le projet ' + escHtml(item.value) + '.'); return; }
          addHistory('project', [item]);
          objects.forEach(o => { objectToProject[o.name] = item.value; });
        } else {
          objects = [{ name: item.value, type: item.tag, desc: item.desc }];
          addHistory('object', [item]);
        }

        const replace = document.getElementById('quickReplace').checked || !lastResult
          || lastResult.source !== source || lastResult.target !== target;
        setQuickStatus('<span class="spinner"></span> Comparaison en cours...');
        const result = await fetchComparison(source, target, objects.map(o => o.name));
        if (replace) selectedObjects = [];
        objects.forEach(o => { if (!selectedObjects.find(x => x.name === o.name)) selectedObjects.push(o); });
        renderSelectedObjects();
        updateCompareButton();
        displayResults(replace ? result : mergeResults(lastResult, result));
        setQuickStatus(`${result.totalObjects} objet(s) ${replace ? 'comparé(s)' : 'ajouté(s) en tête des résultats'}.`);
      } catch (err) {
        setQuickStatus(`<span style="color:red;">Erreur: ${escHtml(err.message)}</span>`);
      } finally {
        btn.disabled = false;
      }
    }

    attachAutocomplete(input, document.getElementById('quickSuggestions'), () => kind.value, quickCompare);
    document.getElementById('btnQuick').addEventListener('click', () => {
      const val = input.value.trim().toUpperCase();
      if (!val) { input.focus(); return; }
      input.value = '';
      hideSuggestions('quickSuggestions');
      quickCompare({ value: val, desc: '', tag: '' });
    });
  }

  // --- Affichage des résultats ---
  let activeBadges = new Set(['=', '≠', '↑', '?', '-', '✓', '!']); // tous actifs par défaut

  function displayResults(result) {
    const section = document.getElementById('resultsSection');
    section.style.display = '';
    activeBadges = new Set(['=', '≠', '↑', '?', '-', '✓', '!']);
    lastResult = result;
    syncQuickEnvs();

    document.getElementById('summaryTotal').textContent = `Total: ${result.totalObjects}`;
    document.getElementById('summaryIdentical').textContent = `= Identiques: ${result.identical}`;
    document.getElementById('summaryDifferent').textContent = `≠ Différents: ${result.different}`;
    document.getElementById('summaryMissing').textContent = `? Absents: ${result.missing}`;

    const moreRecentBadge = document.getElementById('summaryMoreRecent');
    if (moreRecentBadge) moreRecentBadge.textContent = `↑ Plus récent: ${result.moreRecent || 0}`;

    const naBadge = document.getElementById('summaryNA');
    if (naBadge) naBadge.textContent = `- N/A: ${result.notApplicable || 0}`;

    const conformeBadge = document.getElementById('summaryConforme');
    if (conformeBadge) conformeBadge.textContent = `✓ Conforme: ${result.conforme || 0}`;

    const errBadge = document.getElementById('summaryError');
    if (errBadge) errBadge.textContent = `! Erreurs: ${result.errors || 0}`;

    // Activer tous les badges visuellement
    document.querySelectorAll('.results-summary .badge').forEach(badge => {
      badge.classList.add('badge-active');
      badge.style.cursor = 'pointer';
      badge.onclick = () => toggleBadgeFilter(badge.dataset.code);
    });

    const tbody = document.getElementById('resultsBody');
    tbody.innerHTML = '';

    // Regrouper par projet si le mode est projet ou statut
    const hasProjects = result.data.some(d => objectToProject[d.objectName]);

    if (hasProjects) {
      renderGroupedByProject(tbody, result);
    } else {
      renderFlat(tbody, result);
    }

    // Events expand/collapse détail
    tbody.querySelectorAll('.expand-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        const idx = btn.dataset.idx;
        const detail = document.getElementById(`detail-${idx}`);
        const isCollapsed = detail.classList.contains('collapsed');
        detail.classList.toggle('collapsed');
        btn.textContent = isCollapsed ? '▼' : '▶';
      });
    });

    // Events expand/collapse groupe projet
    tbody.querySelectorAll('.project-group-header').forEach(header => {
      header.addEventListener('click', () => {
        const grp = header.dataset.group;
        const toggle = header.querySelector('.group-toggle');
        const rows = tbody.querySelectorAll(`[data-project-group="${grp}"]`);
        const isCollapsed = toggle.textContent === '▶';
        toggle.textContent = isCollapsed ? '▼' : '▶';
        rows.forEach(r => r.classList.toggle('group-collapsed', !isCollapsed));
      });
    });

    // Events diff d'un fichier du package F98780R
    tbody.querySelectorAll('.btn-pkg').forEach(btn => {
      btn.addEventListener('click', (e) => { e.stopPropagation(); runPackageDiff(btn); });
    });

    applyFilters();
  }

  function renderFlat(tbody, result) {
    result.data.forEach((item, idx) => {
      appendResultRow(tbody, item, idx, result);
    });
  }

  function renderGroupedByProject(tbody, result) {
    // Construire les groupes
    const groups = {};
    const noProject = [];
    result.data.forEach((item, idx) => {
      const prj = objectToProject[item.objectName] || '';
      if (prj) {
        if (!groups[prj]) groups[prj] = [];
        groups[prj].push({ item, idx });
      } else {
        noProject.push({ item, idx });
      }
    });

    // Afficher par groupe
    let groupIdx = 0;
    for (const [prjId, items] of Object.entries(groups)) {
      const grpKey = `grp-${groupIdx++}`;
      // Ligne header de groupe
      const headerTr = document.createElement('tr');
      headerTr.className = 'project-group-header';
      headerTr.dataset.group = grpKey;
      const prjDiffCount = items.filter(i => i.item.result === '≠' || i.item.result === '↑').length;
      headerTr.innerHTML = `<td colspan="7"><span class="group-toggle">▼</span>Projet ${escHtml(prjId)} (${items.length} objet(s), ${prjDiffCount} diff/récent)</td>`;
      tbody.appendChild(headerTr);

      items.forEach(({ item, idx }) => {
        appendResultRow(tbody, item, idx, result, grpKey);
      });
    }

    // Objets sans projet
    if (noProject.length > 0) {
      noProject.forEach(({ item, idx }) => {
        appendResultRow(tbody, item, idx, result, '');
      });
    }
  }

  function appendResultRow(tbody, item, idx, result, projectGroup) {
    const tr = document.createElement('tr');
    const pkgPaths = item.package && item.package.files ? item.package.files.map(f => f.path).join(' ') : '';
    tr.dataset.filter = `${item.objectName} ${item.objectType} ${item.resultLabel} ${item.differences} ${pkgPaths}`.toLowerCase();
    tr.dataset.resultCode = item.result;
    if (projectGroup) tr.dataset.projectGroup = projectGroup;

    const resultClass = item.result === '=' ? 'result-identical' :
                        item.result === '≠' ? 'result-different' :
                        item.result === '↑' ? 'result-recent' :
                        item.result === '✓' ? 'result-conforme' :
                        item.result === '-' ? 'result-na' :
                        item.result === '?' ? 'result-missing' : 'result-error';

    tr.innerHTML = `
      <td><span class="expand-btn" data-idx="${idx}">▶</span></td>
      <td><strong>${escHtml(item.objectName)}</strong><br><small>${escHtml(item.objectDesc || '')}</small></td>
      <td>${escHtml(item.objectType)}</td>
      <td class="${resultClass}">${escHtml(item.result)} ${escHtml(item.resultLabel)}</td>
      <td><small>${escHtml(item.source.user || '-')} ${escHtml(item.source.date || '')} ${escHtml(item.source.time || '')}</small><br>
          <small>${item.source.recordCount} enreg, ${formatSize(item.source.blobSize)}</small></td>
      <td><small>${escHtml(item.target.user || '-')} ${escHtml(item.target.date || '')} ${escHtml(item.target.time || '')}</small><br>
          <small>${item.target.recordCount} enreg, ${formatSize(item.target.blobSize)}</small></td>
      <td><small>${escHtml(item.differences || '-')}</small></td>
    `;
    tbody.appendChild(tr);

    // Ligne de détail
    const detailTr = document.createElement('tr');
    detailTr.className = 'detail-row collapsed';
    detailTr.id = `detail-${idx}`;
    if (projectGroup) detailTr.dataset.projectGroup = projectGroup;
    detailTr.innerHTML = `<td colspan="7">
      <strong>Détail comparaison :</strong><br>
      Source (${escHtml(result.source)}) : ${item.source.recordCount} enregistrement(s), ${formatSize(item.source.blobSize)}<br>
      Cible (${escHtml(result.target)}) : ${item.target.recordCount} enregistrement(s), ${formatSize(item.target.blobSize)}<br>
      ${item.differences ? 'Résumé repository : ' + escHtml(item.differences) : 'Aucune différence détectée.'}
      ${renderPackage(item, idx, result)}
    </td>`;
    tbody.appendChild(detailTr);
  }

  // --- Package F98780R (même extraction que Lire .h) ---
  const PKG_STATUS = { 'modifie': 'Modifié', 'source-seule': 'Source seule', 'cible-seule': 'Cible seule' };

  function renderPackage(item, idx, result) {
    const pkg = item.package;
    if (!pkg) return '';
    let html = `<div class="pkg-block"><strong>Package F98780R :</strong> ${escHtml(pkg.label)}`;
    if (pkg.files && pkg.files.length) {
      html += `<table class="pkg-table"><thead><tr><th>Fichier</th><th>Écart</th><th>${escHtml(result.source)}</th><th>${escHtml(result.target)}</th><th></th></tr></thead><tbody>`;
      pkg.files.forEach((f, i) => {
        const cls = f.status === 'modifie' ? 'result-different' : 'result-missing';
        html += `<tr><td><code>${escHtml(f.path)}</code></td><td class="${cls}">${escHtml(PKG_STATUS[f.status] || f.status)}</td>
          <td>${f.sourceSize ? formatSize(f.sourceSize) : '-'}</td><td>${f.targetSize ? formatSize(f.targetSize) : '-'}</td>
          <td><button class="btn btn-xs btn-pkg" data-obj="${escHtml(item.objectName)}" data-src="${escHtml(result.source)}" data-tgt="${escHtml(result.target)}" data-path="${escHtml(f.path)}" data-target="pkg-diff-${idx}-${i}">Voir</button></td></tr>
          <tr class="pkg-diff-row"><td colspan="5"><div id="pkg-diff-${idx}-${i}"></div></td></tr>`;
      });
      html += '</tbody></table>';
    }
    return html + '</div>';
  }

  async function runPackageDiff(btn) {
    const box = document.getElementById(btn.dataset.target);
    if (box.dataset.loaded) { box.innerHTML = ''; delete box.dataset.loaded; btn.textContent = 'Voir'; return; }
    btn.disabled = true;
    box.innerHTML = '<span class="spinner"></span> Lecture des packages...';
    try {
      const resp = await fetch(`${API_BASE}/package-diff`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-JDE-Connection': getConnection() },
        body: JSON.stringify({ object: btn.dataset.obj, source: btn.dataset.src, target: btn.dataset.tgt, path: btn.dataset.path })
      });
      const data = await resp.json().catch(() => ({}));
      if (!resp.ok) throw new Error(data.detail || data.error || resp.statusText);
      let html;
      if (data.binary) {
        html = data.identical ? '<span class="result-identical">✓ Contenu identique</span>'
          : `Fichier binaire : ${formatSize(data.srcSize)} (source) / ${formatSize(data.tgtSize)} (cible), contenu différent.`;
      } else {
        html = `${escHtml(btn.dataset.src)} : ${data.srcLineCount} lignes — ${escHtml(btn.dataset.tgt)} : ${data.tgtLineCount} lignes<br>` + renderDiff(data.diff, 'Contenu identique');
        if (data.aiAvailable || data.copilotPrompt) {
          html += `<div class="ai-block">`
            + (data.aiAvailable ? `<button class="btn btn-xs btn-ai">✨ Analyse IA</button> ` : '')
            + (data.copilotPrompt ? `<button class="btn btn-xs btn-copilot" title="Copie les consignes et le diff : coller ensuite dans Copilot (Ctrl+V)">📋 Copier pour Copilot</button>` : '')
            + `<span class="ai-hint">Écart de ${data.screenLines} lignes : demander une explication de l'écart.</span>
            <div class="ai-result"></div></div>`;
        }
      }
      box.innerHTML = html;
      const aiBtn = box.querySelector('.btn-ai');
      if (aiBtn) aiBtn.addEventListener('click', (e) => { e.stopPropagation(); runAiAnalysis(btn, aiBtn, box.querySelector('.ai-result')); });
      const cpBtn = box.querySelector('.btn-copilot');
      if (cpBtn) cpBtn.addEventListener('click', (e) => { e.stopPropagation(); copyForCopilot(cpBtn, data.copilotPrompt); });
      box.dataset.loaded = '1';
      btn.textContent = 'Masquer';
    } catch (err) {
      box.innerHTML = `<span style="color:red;">Erreur: ${escHtml(err.message)}</span>`;
    } finally {
      btn.disabled = false;
    }
  }

  // Analyse IA (Azure OpenAI) d'un écart de fichier : le serveur recalcule le diff à partir de l'objet et du chemin.
  async function runAiAnalysis(pkgBtn, aiBtn, out) {
    aiBtn.disabled = true;
    out.innerHTML = '<span class="spinner"></span> Analyse IA en cours...';
    try {
      const resp = await fetch(`${API_BASE}/ai-analysis`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-JDE-Connection': getConnection() },
        body: JSON.stringify({ object: pkgBtn.dataset.obj, source: pkgBtn.dataset.src, target: pkgBtn.dataset.tgt, path: pkgBtn.dataset.path })
      });
      const data = await resp.json().catch(() => ({}));
      if (!resp.ok) throw new Error(data.detail || data.error || resp.statusText);
      out.innerHTML = `<div class="ai-analysis">${renderAiText(data.analysis)}</div>
        <div class="ai-disclaimer">Analyse générée par IA : à vérifier.</div>`;
      aiBtn.style.display = 'none';
    } catch (err) {
      out.innerHTML = `<span style="color:red;">Erreur IA : ${escHtml(err.message)}</span>`;
      aiBtn.disabled = false;
    }
  }

  // Copie le prompt (consignes + diff) pour le coller dans Copilot. navigator.clipboard n'existe qu'en https/localhost.
  async function copyForCopilot(cpBtn, text) {
    let ok = false;
    try {
      if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); ok = true; }
    } catch { /* repli ci-dessous */ }
    if (!ok) {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', '');
      ta.style.cssText = 'position:fixed;left:-9999px;top:0;';
      document.body.appendChild(ta);
      ta.select();
      try { ok = document.execCommand('copy'); } catch { ok = false; }
      ta.remove();
    }
    const label = cpBtn.textContent;
    cpBtn.textContent = ok ? '✓ Copié : coller dans Copilot (Ctrl+V)' : '✗ Copie impossible';
    setTimeout(() => { cpBtn.textContent = label; }, 4000);
  }

  // Mise en forme minimale (texte échappé) : **gras**, `code`, titres #, puces.
  function renderAiText(text) {
    return escHtml(text || '').split('\n').map(line => {
      let l = line.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>').replace(/`([^`]+)`/g, '<code>$1</code>');
      const h = l.match(/^#{1,4}\s+(.*)$/);
      if (h) return `<div class="ai-title">${h[1]}</div>`;
      const b = l.match(/^(\s*)[-*•]\s+(.*)$/);
      if (b) return `<div class="ai-bullet" style="margin-left:${b[1].length * 6 + 8}px">• ${b[2]}</div>`;
      return l.trim() ? `<div>${l}</div>` : '<div class="ai-gap"></div>';
    }).join('');
  }

  function renderDiff(diff, identicalLabel) {
    if (!diff || diff.length === 0) return `<span class="result-identical">✓ ${escHtml(identicalLabel)}</span>`;
    let html = `<div class="diff-summary">${diff.length} différence(s) détectée(s)</div><div class="diff-lines">`;
    diff.forEach(d => {
      if (d.type === 'added') {
        html += `<div class="diff-line diff-added"><span class="diff-ln">+${d.line}</span> ${escHtml(d.content)}</div>`;
      } else if (d.type === 'removed') {
        html += `<div class="diff-line diff-removed"><span class="diff-ln">-${d.line}</span> ${escHtml(d.content)}</div>`;
      } else if (d.type === 'modified') {
        const [src, tgt] = charDiff(d.srcContent || '', d.tgtContent || '');
        html += `<div class="diff-line diff-removed"><span class="diff-ln">~${d.line}</span> ${src}</div>`;
        html += `<div class="diff-line diff-added"><span class="diff-ln">~${d.line}</span> ${tgt}</div>`;
      } else if (d.type === 'truncated') {
        html += `<div class="diff-line diff-truncated">${escHtml(d.content)}</div>`;
      }
    });
    return html + '</div>';
  }

  // Diff caractère par caractère d'une ligne modifiée : renvoie [html source, html cible] avec les écarts dans <mark>.
  // LCS sur les caractères restant après préfixe/suffixe communs ; au-delà de CHAR_DIFF_MAX cellules, tout le milieu est surligné.
  const CHAR_DIFF_MAX = 250000;
  function charDiff(a, b) {
    let pre = 0;
    while (pre < a.length && pre < b.length && a[pre] === b[pre]) pre++;
    let suf = 0;
    while (suf < a.length - pre && suf < b.length - pre && a[a.length - 1 - suf] === b[b.length - 1 - suf]) suf++;
    const ma = a.slice(pre, a.length - suf), mb = b.slice(pre, b.length - suf);
    const keepA = new Array(ma.length).fill(false), keepB = new Array(mb.length).fill(false);
    if (ma.length && mb.length && ma.length * mb.length <= CHAR_DIFF_MAX) {
      const n = ma.length, m = mb.length;
      const t = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
      for (let i = n - 1; i >= 0; i--)
        for (let j = m - 1; j >= 0; j--)
          t[i][j] = ma[i] === mb[j] ? t[i + 1][j + 1] + 1 : Math.max(t[i + 1][j], t[i][j + 1]);
      let i = 0, j = 0;
      while (i < n && j < m) {
        if (ma[i] === mb[j]) { keepA[i++] = true; keepB[j++] = true; }
        else if (t[i + 1][j] >= t[i][j + 1]) i++;
        else j++;
      }
      // Les fragments communs très courts entre deux écarts (ex. une lettre isolée) sont surlignés aussi : plus lisible.
      absorbShortRuns(keepA); absorbShortRuns(keepB);
    }
    const render = (s, mid, keep) => escHtml(s.slice(0, pre)) + markRuns(mid, keep) + escHtml(s.slice(s.length - suf));
    return [render(a, ma, keepA), render(b, mb, keepB)];
  }

  function absorbShortRuns(keep) {
    let i = 0;
    while (i < keep.length) {
      if (!keep[i]) { i++; continue; }
      let j = i;
      while (j < keep.length && keep[j]) j++;
      if (i > 0 && j < keep.length && j - i < 3) keep.fill(false, i, j);
      i = j;
    }
  }

  function markRuns(s, keep) {
    let html = '', i = 0;
    while (i < s.length) {
      let j = i;
      while (j < s.length && keep[j] === keep[i]) j++;
      const part = escHtml(s.slice(i, j));
      html += keep[i] ? part : `<mark class="diff-char">${part}</mark>`;
      i = j;
    }
    return html;
  }

  // --- Filtre résultats ---
  function applyFilters() {
    const query = document.getElementById('filterResults').value.toLowerCase();
    const rows = document.querySelectorAll('#resultsBody > tr:not(.detail-row):not(.project-group-header)');
    rows.forEach(row => {
      const textMatch = !query || (row.dataset.filter && row.dataset.filter.includes(query));
      const badgeMatch = activeBadges.has(row.dataset.resultCode);
      const visible = textMatch && badgeMatch;
      row.classList.toggle('hidden', !visible);
      // Cacher aussi le détail associé
      const next = row.nextElementSibling;
      if (next && next.classList.contains('detail-row')) {
        if (!visible) next.classList.add('collapsed');
      }
    });

    // Masquer les headers de groupe si tous leurs enfants sont cachés
    document.querySelectorAll('.project-group-header').forEach(header => {
      const grp = header.dataset.group;
      const children = document.querySelectorAll(`[data-project-group="${grp}"]:not(.detail-row)`);
      const allHidden = Array.from(children).every(r => r.classList.contains('hidden'));
      header.classList.toggle('hidden', allHidden);
    });
  }

  function filterResults() {
    applyFilters();
  }

  // --- Filtrage par clic sur badge (toggle on/off) ---
  function toggleBadgeFilter(code) {
    if (activeBadges.has(code)) {
      activeBadges.delete(code);
    } else {
      activeBadges.add(code);
    }
    // Mettre à jour le visuel des badges
    document.querySelectorAll('.results-summary .badge').forEach(b => {
      b.classList.toggle('badge-active', activeBadges.has(b.dataset.code));
    });
    applyFilters();
  }

  // --- Utilitaires ---
  function hideSuggestions(id) {
    document.getElementById(id).classList.remove('visible');
  }

  function updateCompareButton() {
    const btn = document.getElementById('btnCompare');
    btn.disabled = selectedObjects.length === 0;
  }

  function setStatus(msg) {
    document.getElementById('statusMessage').innerHTML = msg;
  }

  function clearAll() {
    selectedObjects = [];
    objectToProject = {};
    lastResult = null;
    renderSelectedObjects();
    document.getElementById('objectInput').value = '';
    document.getElementById('projectInput').value = '';
    document.getElementById('statusSelect').value = '';
    document.getElementById('projectInfo').innerHTML = '';
    document.getElementById('statusProjects').innerHTML = '';
    document.getElementById('resultsSection').style.display = 'none';
    document.getElementById('filterResults').value = '';
    setStatus('');
    updateCompareButton();
  }

  function escHtml(str) {
    if (!str) return '';
    const div = document.createElement('div');
    div.textContent = str;
    return div.innerHTML;
  }

  function formatSize(bytes) {
    if (!bytes || bytes === 0) return '0';
    if (bytes < 1024) return bytes + ' o';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' Ko';
    return (bytes / (1024 * 1024)).toFixed(1) + ' Mo';
  }

  // --- Collapse/expand du formulaire ---
  function collapseFormSection() {
    const form = document.getElementById('formSection');
    const icon = document.getElementById('formCollapseIcon');
    const btn = document.getElementById('btnRecommencer');
    form.style.display = 'none';
    icon.textContent = '▸';
    btn.style.display = '';
  }

  function expandFormSection() {
    const form = document.getElementById('formSection');
    const icon = document.getElementById('formCollapseIcon');
    form.style.display = '';
    icon.textContent = '▾';
  }

  // Expose pour onclick dans le HTML
  window.toggleFormSection = function() {
    const form = document.getElementById('formSection');
    if (form.style.display === 'none') {
      expandFormSection();
    } else {
      collapseFormSection();
    }
  };

  window.recommencer = function() {
    expandFormSection();
    clearAll();
    document.getElementById('btnRecommencer').style.display = 'none';
  };
})();
