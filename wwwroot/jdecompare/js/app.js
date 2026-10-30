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
      const first = avail.length ? avail[0].environment : null;
      const last = avail.length ? avail[avail.length - 1].environment : null;
      envs.forEach(e => {
        const label = e.environment + (e.available === false ? ' (identifiants manquants)' : '');
        const s = new Option(label, e.environment, e.environment === first, e.environment === first);
        const t = new Option(label, e.environment, e.environment === last, e.environment === last);
        s.disabled = t.disabled = e.available === false;
        srcSelect.add(s);
        tgtSelect.add(t);
      });
    } catch (err) {
      console.error('Erreur chargement environnements:', err);
    }
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

    // Autocomplétion objet
    const objectInput = document.getElementById('objectInput');
    let objDebounce = null;
    objectInput.addEventListener('input', () => {
      clearTimeout(objDebounce);
      objDebounce = setTimeout(() => autocompleteObjects(objectInput.value), 300);
    });
    objectInput.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        const val = objectInput.value.trim();
        if (val.length > 0) {
          addObject(val, '', '');
          objectInput.value = '';
          hideSuggestions('objectSuggestions');
        }
      }
    });

    // Autocomplétion projet
    const projectInput = document.getElementById('projectInput');
    let prjDebounce = null;
    projectInput.addEventListener('input', () => {
      clearTimeout(prjDebounce);
      prjDebounce = setTimeout(() => autocompleteProjects(projectInput.value), 300);
    });

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
      }
    });
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
  async function autocompleteObjects(query) {
    const suggestions = document.getElementById('objectSuggestions');
    if (!query || query.trim().length < 3) {
      hideSuggestions('objectSuggestions');
      return;
    }

    try {
      const conn = getConnection();
      console.log('[JDE Compare] autocomplete objets q=' + query + ' conn=' + conn);
      const url = `${API_BASE}/objects?q=${encodeURIComponent(query.trim())}&limit=15`;
      const resp = await fetch(url, {
        headers: conn ? { 'X-JDE-Connection': conn } : {}
      });
      console.log('[JDE Compare] autocomplete response status=' + resp.status);
      if (!resp.ok) {
        const errText = await resp.text();
        console.error('[JDE Compare] autocomplete erreur:', errText);
        suggestions.innerHTML = '<div class="suggestion-item" style="color:red;cursor:default;">Erreur serveur</div>';
        suggestions.classList.add('visible');
        return;
      }
      const data = await resp.json();

      suggestions.innerHTML = '';
      if (data.length === 0) {
        suggestions.innerHTML = '<div class="suggestion-item" style="color:#999;cursor:default;">Aucun résultat</div>';
      } else {
        data.forEach(item => {
          const div = document.createElement('div');
          div.className = 'suggestion-item';
          div.innerHTML = `<span><span class="obj-name">${escHtml(item.objectName)}</span> <span class="obj-desc">${escHtml(item.description)}</span></span><span class="obj-type">${escHtml(item.objectType)}</span>`;
          div.addEventListener('click', () => {
            addObject(item.objectName, item.objectType, item.description);
            document.getElementById('objectInput').value = '';
            hideSuggestions('objectSuggestions');
          });
          suggestions.appendChild(div);
        });
      }
      suggestions.classList.add('visible');
    } catch (err) {
      console.error('[JDE Compare] Erreur autocomplete objets:', err);
      suggestions.innerHTML = '<div class="suggestion-item" style="color:red;cursor:default;">Erreur: ' + escHtml(err.message) + '</div>';
      suggestions.classList.add('visible');
    }
  }

  // --- Autocomplétion projets ---
  async function autocompleteProjects(query) {
    const suggestions = document.getElementById('projectSuggestions');
    if (!query || query.trim().length < 3) {
      hideSuggestions('projectSuggestions');
      return;
    }

    try {
      const conn = getConnection();
      console.log('[JDE Compare] autocomplete projets q=' + query + ' conn=' + conn);
      const resp = await fetch(`${API_BASE}/projects?q=${encodeURIComponent(query.trim())}&limit=15`, {
        headers: conn ? { 'X-JDE-Connection': conn } : {}
      });
      if (!resp.ok) {
        const errText = await resp.text();
        console.error('[JDE Compare] autocomplete projets erreur:', errText);
        suggestions.innerHTML = '<div class="suggestion-item" style="color:red;cursor:default;">Erreur serveur</div>';
        suggestions.classList.add('visible');
        return;
      }
      const data = await resp.json();

      suggestions.innerHTML = '';
      if (data.length === 0) {
        suggestions.innerHTML = '<div class="suggestion-item" style="color:#999;cursor:default;">Aucun résultat</div>';
      } else {
        data.forEach(item => {
          const div = document.createElement('div');
          div.className = 'suggestion-item';
          div.innerHTML = `<span class="obj-name">${escHtml(item.projectId)}</span> <span class="obj-desc">${escHtml(item.description)}</span><span class="obj-type">St:${escHtml(item.status)}</span>`;
          div.addEventListener('click', () => {
            selectProject(item.projectId);
            document.getElementById('projectInput').value = '';
            hideSuggestions('projectSuggestions');
          });
          suggestions.appendChild(div);
        });
      }
      suggestions.classList.add('visible');
    } catch (err) {
      console.error('Erreur autocomplete projets:', err);
    }
  }

  // --- Sélection d'un projet → charger ses objets ---
  async function selectProject(projectId) {
    const infoBox = document.getElementById('projectInfo');
    infoBox.innerHTML = '<span class="spinner"></span> Chargement des objets du projet...';

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
          await selectProject(p.projectId);
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

    try {
      const conn = getConnection();
      const resp = await fetch(`${API_BASE}/compare`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-JDE-Connection': conn
        },
        body: JSON.stringify({
          source,
          target,
          objects: selectedObjects.map(o => o.name)
        })
      });

      if (!resp.ok) {
        const err = await resp.json();
        throw new Error(err.detail || err.error || resp.statusText);
      }

      const result = await resp.json();
      displayResults(result);
      setStatus(`Comparaison terminée : ${result.totalObjects} objet(s).`);
      collapseFormSection();
    } catch (err) {
      setStatus(`<span style="color:red;">Erreur: ${escHtml(err.message)}</span>`);
    } finally {
      btn.disabled = false;
    }
  }

  // --- Affichage des résultats ---
  let activeBadges = new Set(['=', '≠', '↑', '?', '-', '✓', '!']); // tous actifs par défaut

  function displayResults(result) {
    const section = document.getElementById('resultsSection');
    section.style.display = '';
    activeBadges = new Set(['=', '≠', '↑', '?', '-', '✓', '!']);

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
    const hasProjects = (currentMode === 'project' || currentMode === 'status') && Object.keys(objectToProject).length > 0;

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
        html += `<div class="diff-line diff-removed"><span class="diff-ln">~${d.line}</span> ${escHtml(d.srcContent)}</div>`;
        html += `<div class="diff-line diff-added"><span class="diff-ln">~${d.line}</span> ${escHtml(d.tgtContent)}</div>`;
      } else if (d.type === 'truncated') {
        html += `<div class="diff-line diff-truncated">${escHtml(d.content)}</div>`;
      }
    });
    return html + '</div>';
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
