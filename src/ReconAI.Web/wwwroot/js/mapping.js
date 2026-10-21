// Mapping screen: live "required fields" checklist, AI suggestions, and an AJAX dry run of the mapping against every row.
// Anything that came from the uploaded file or from the AI service is only ever inserted with textContent (never innerHTML).
(function () {
  var form = document.getElementById('mapForm');
  if (!form) return;

  var required = [];
  try { required = JSON.parse(form.getAttribute('data-required') || '[]'); } catch (e) { required = []; }
  var selects = Array.prototype.slice.call(form.querySelectorAll('select[name=Fields]'));
  var cells = Array.prototype.slice.call(form.querySelectorAll('.ai-cell'));
  var checklist = document.getElementById('requiredList');
  var panel = document.getElementById('previewPanel');
  var previewBtn = document.getElementById('previewBtn');
  var aiBtn = document.getElementById('aiBtn');
  var aiStatus = document.getElementById('aiStatus');
  var aiSummary = document.getElementById('aiSummary');
  var inferenceInput = document.getElementById('inferenceId');

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text !== undefined && text !== null) n.textContent = text;
    return n;
  }
  function pct(x) { return Math.round(x * 100) + '%'; }

  // ---------------------------------------------------------------- required-field checklist + duplicate warning
  function refresh() {
    var counts = {};
    selects.forEach(function (s) { if (s.value) counts[s.value] = (counts[s.value] || 0) + 1; });
    selects.forEach(function (s, i) {
      var dup = s.value && counts[s.value] > 1;
      s.classList.toggle('is-dup', !!dup);
      s.title = dup ? 'This field is mapped to more than one column' : '';
      updateEdited(i);
    });
    if (!checklist) return;
    checklist.querySelectorAll('li').forEach(function (li) {
      var done = !!counts[li.getAttribute('data-field')];
      li.classList.toggle('is-done', done);
      li.querySelector('.tick').textContent = done ? '✓' : '';
    });
  }
  selects.forEach(function (s) { s.addEventListener('change', refresh); });

  // ---------------------------------------------------------------- AI suggestions
  function updateEdited(i) {
    var s = selects[i], cell = cells[i];
    if (!cell || s.dataset.aiField === undefined) return;
    var edited = s.value !== s.dataset.aiField;
    cell.classList.toggle('is-edited', edited);
    var note = cell.querySelector('.ai-edited');
    if (edited && !note) {
      note = el('span', 'ai-edited', 'You changed this (AI suggested: ' + (s.dataset.aiLabel || 'ignore') + ')');
      cell.appendChild(note);
    } else if (!edited && note) {
      note.remove();
    }
  }

  function renderCell(sug) {
    var cell = cells[sug.columnIndex];
    if (!cell) return;
    cell.replaceChildren();
    cell.className = 'ai-cell band-' + sug.band;
    var line = el('div', 'ai-line');
    line.appendChild(el('span', 'ai-chip', 'AI ' + pct(sug.confidence) + ' ' + sug.band));
    line.appendChild(el('span', 'ai-what', 'suggests: ' + sug.fieldLabel));
    cell.appendChild(line);
    var bar = el('span', 'conf-bar'); bar.setAttribute('aria-hidden', 'true');
    var fill = el('span', 'conf-fill'); fill.style.width = pct(sug.confidence); bar.appendChild(fill);
    cell.appendChild(bar);
    if (sug.reasons.length || sug.alternatives.length) {
      var d = el('details', 'ai-why');
      d.appendChild(el('summary', '', 'why?'));
      var ul = el('ul', 'reasons');
      sug.reasons.forEach(function (r) { ul.appendChild(el('li', '', r)); });
      d.appendChild(ul);
      if (sug.alternatives.length) {
        d.appendChild(el('p', 'small muted', 'Also considered: ' + sug.alternatives.map(function (a) { return a.fieldLabel + ' ' + pct(a.confidence); }).join(', ')));
      }
      cell.appendChild(d);
    }
  }

  function setStatus(text, tone) {
    aiStatus.textContent = text;
    aiStatus.className = 'small ' + (tone === 'warn' ? 'is-warn' : 'muted');
  }

  function applyAi(result) {
    if (!result.available) {
      setStatus(result.message || 'The AI service is not available. Map the columns manually.', 'warn');
      aiSummary.hidden = true;
      return;
    }
    if (inferenceInput) inferenceInput.value = result.inferenceId;
    var bands = { high: 0, medium: 0, low: 0 };
    result.suggestions.forEach(function (s) {
      var sel = selects[s.columnIndex];
      if (!sel) return;
      sel.value = s.field || '';
      sel.dataset.aiField = s.field || '';
      sel.dataset.aiLabel = s.fieldLabel;
      bands[s.band] = (bands[s.band] || 0) + 1;
      renderCell(s);
    });
    refresh();
    setStatus('Suggested by ' + result.model.name + ' v' + result.model.version + ' in ' + result.latencyMs + ' ms. Review every row: nothing is saved until you press "Confirm mapping and import".');
    aiSummary.replaceChildren();
    aiSummary.appendChild(el('span', 'chip chip-ok', bands.high + ' high confidence'));
    aiSummary.appendChild(el('span', 'chip chip-info', bands.medium + ' medium'));
    aiSummary.appendChild(el('span', 'chip ' + (bands.low ? 'chip-warn' : 'chip-neutral'), bands.low + ' low: check these yourself'));
    if (result.problems && result.problems.length) {
      aiSummary.appendChild(el('span', 'chip chip-warn', result.problems.length + ' suggestion(s) were discarded by validation'));
    }
    aiSummary.hidden = false;
  }

  function suggest() {
    aiBtn.disabled = true;
    var original = aiBtn.textContent;
    aiBtn.textContent = 'Asking the AI...';
    setStatus('Asking the local AI model...');
    fetch(form.getAttribute('data-suggest-url'), { method: 'POST', body: new FormData(form), credentials: 'same-origin' })
      .then(function (res) { if (!res.ok) throw new Error('HTTP ' + res.status); return res.json(); })
      .then(applyAi)
      .catch(function () { setStatus('The suggestion request failed. You can map the columns manually.', 'warn'); })
      .finally(function () { aiBtn.disabled = false; aiBtn.textContent = 'Suggest again'; });
  }
  if (aiBtn) aiBtn.addEventListener('click', suggest);

  refresh();
  if (aiBtn && form.getAttribute('data-auto-suggest') === 'true') suggest();

  // ---------------------------------------------------------------- dry-run preview
  function table(columns, rows) {
    var wrap = el('div', 'table-wrap');
    var t = el('table', 'table table-compact');
    var head = el('tr');
    columns.forEach(function (c) { var th = el('th', c.num ? 'num' : '', c.label); th.scope = 'col'; head.appendChild(th); });
    var thead = el('thead'); thead.appendChild(head); t.appendChild(thead);
    var body = el('tbody');
    rows.forEach(function (r) {
      var tr = el('tr');
      columns.forEach(function (c) { tr.appendChild(el('td', c.num ? 'num' : '', r[c.key] == null ? '' : String(r[c.key]))); });
      body.appendChild(tr);
    });
    t.appendChild(body); wrap.appendChild(t);
    return wrap;
  }

  function stat(label, value, tone) {
    var d = el('div', 'stat' + (tone ? ' stat-' + tone : ''));
    d.appendChild(el('span', 'stat-value', value));
    d.appendChild(el('span', 'stat-label', label));
    return d;
  }

  function render(result) {
    panel.replaceChildren();
    panel.hidden = false;
    panel.appendChild(el('h3', '', 'Mapping check'));

    if (!result.ok) {
      var bad = el('div', 'alert alert-bad');
      bad.setAttribute('role', 'alert');
      result.problems.forEach(function (p) { bad.appendChild(el('div', '', p)); });
      panel.appendChild(bad);
      return;
    }

    var stats = el('div', 'stats');
    stats.appendChild(stat('rows in file', result.totalRows));
    stats.appendChild(stat('valid rows', result.validRows, 'ok'));
    stats.appendChild(stat('rejected rows', result.rejectedRows, result.rejectedRows ? 'bad' : ''));
    stats.appendChild(stat('total of valid amounts (INR)', result.validAmountTotal));
    panel.appendChild(stats);

    var verdict = result.validRows === 0
      ? el('div', 'alert alert-bad', 'No row is valid with this mapping, so it cannot be imported. Check which column is mapped to which field.')
      : result.rejectedRows
        ? el('div', 'banner banner-warn', 'Rejected rows are not matched, but they are kept with the reason so nothing is silently dropped.')
        : el('div', 'banner banner-info', 'Every row is valid with this mapping. Nothing has been imported yet: press "Confirm mapping and import".');
    panel.appendChild(verdict);

    if (result.rejectReasons.length) {
      panel.appendChild(el('h4', '', 'Why rows were rejected'));
      var ul = el('ul', 'reasons');
      result.rejectReasons.forEach(function (r) { ul.appendChild(el('li', '', r.count + ' row(s): ' + r.example)); });
      panel.appendChild(ul);
    }

    var cols = [
      { key: 'rowNumber', label: 'Row', num: true }, { key: 'transactionRef', label: 'Transaction ref' }, { key: 'settlementRef', label: 'Settlement ref' },
      { key: 'amount', label: 'Amount', num: true }, { key: 'fees', label: 'Fees', num: true }, { key: 'txnDate', label: 'Date' }, { key: 'status', label: 'Status' }
    ];
    if (result.sampleValid.length) {
      panel.appendChild(el('h4', '', 'First valid rows as they will be stored'));
      panel.appendChild(table(cols, result.sampleValid));
    }
    if (result.sampleRejected.length) {
      panel.appendChild(el('h4', '', 'First rejected rows'));
      panel.appendChild(table([{ key: 'rowNumber', label: 'Row', num: true }, { key: 'problem', label: 'Reason' }], result.sampleRejected));
    }
  }

  previewBtn.addEventListener('click', function () {
    previewBtn.disabled = true;
    var original = previewBtn.textContent;
    previewBtn.textContent = 'Checking...';
    fetch(form.getAttribute('data-preview-url'), { method: 'POST', body: new FormData(form), credentials: 'same-origin' })
      .then(function (res) { if (!res.ok) throw new Error('HTTP ' + res.status); return res.json(); })
      .then(render)
      .catch(function () {
        render({ ok: false, problems: ['The check could not be completed. Reload the page and try again.'] });
      })
      .finally(function () { previewBtn.disabled = false; previewBtn.textContent = original; });
  });
})();
