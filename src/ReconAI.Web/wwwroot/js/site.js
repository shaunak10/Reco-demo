// Mobile navigation toggle (progressive enhancement: the page works without it on wide screens).
(function () {
  var btn = document.getElementById('menuBtn');
  var sidebar = document.getElementById('sidebar');
  var scrim = document.getElementById('scrim');
  if (!btn || !sidebar || !scrim) return;

  function setOpen(open) {
    sidebar.classList.toggle('open', open);
    scrim.hidden = !open;
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
  }

  btn.addEventListener('click', function () { setOpen(!sidebar.classList.contains('open')); });
  scrim.addEventListener('click', function () { setOpen(false); });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') setOpen(false); });
})();
