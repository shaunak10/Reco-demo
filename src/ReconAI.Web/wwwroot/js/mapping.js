// Mapping screen: live "required fields" checklist and an AJAX dry run of the mapping against every row.
// Values that came from the uploaded file are only ever inserted with textContent (never innerHTML).
(function () {
  var form = document.getElementById('mapForm');
  if (!form) return;

  var required = [];
  try { required = JSON.parse(form.getAttribute('data-required') || '[]'); } catch (e) { required = []; }
  var selects = Array.prototype.slice.call(form.querySelectorAll('select[name=Fields]'));
  var checklist = document.getElementById('requiredList');
  var panel = document.getElementById('previewPanel');
  var previewBtn = document.getElementById('previewBtn');

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text !== undefined && text !== null) n.textContent = text;
    return n;
  }

  function refresh() {
    var counts = {};
    selects.forEach(function (s) { if (s.value) counts[s.value] = (counts[s.value] || 0) + 1; });
    selects.forEach(function (s) {
      var dup = s.value && counts[s.value] > 1;
      s.classList.toggle('is-dup', !!dup);
      s.title = dup ? 'This field is mapped to more than one column' : '';
    });
    if (!checklist) return;
    checklist.querySelectorAll('li').forEach(function (li) {
      var done = !!counts[li.getAttribute('data-field')];
      li.classList.toggle('is-done', done);
      li.querySelector('.tick').textContent = done ? '✓' : '';
    });
  }
  selects.forEach(function (s) { s.addEventListener('change', refresh); });
  refresh();

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
