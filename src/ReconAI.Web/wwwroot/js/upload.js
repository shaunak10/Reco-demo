// Instant feedback on the upload forms. The server enforces the same rules; this only saves a round trip.
(function () {
  var forms = document.querySelectorAll('.upload-form');
  forms.forEach(function (form) {
    var input = form.querySelector('input[type=file]');
    var msg = form.querySelector('.upload-msg');
    var button = form.querySelector('button[type=submit]');
    var maxBytes = parseInt(form.getAttribute('data-max-bytes'), 10) || 5242880;

    function problem(file) {
      var name = file.name.toLowerCase();
      if (!/\.(csv|xlsx)$/.test(name)) return 'Only .csv and .xlsx files are accepted.';
      if (file.size === 0) return 'The file is empty.';
      if (file.size > maxBytes) return 'The file is ' + (file.size / 1048576).toFixed(1) + ' MB; the limit is ' + (maxBytes / 1048576) + ' MB.';
      return '';
    }

    input.addEventListener('change', function () {
      var file = input.files && input.files[0];
      msg.textContent = '';
      msg.classList.remove('is-bad');
      button.disabled = false;
      if (!file) return;
      var p = problem(file);
      if (p) {
        msg.textContent = p;
        msg.classList.add('is-bad');
        button.disabled = true;
      } else {
        msg.textContent = file.name + ' (' + (file.size / 1024).toFixed(0) + ' KB) is ready to upload.';
      }
    });

    form.addEventListener('submit', function () {
      button.disabled = true;      // avoid a double submit while the file is uploading
      button.textContent = 'Uploading...';
    });
  });
})();
