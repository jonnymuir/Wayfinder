// Shows what the visitor chose in an image file input (data-wayfinder-file-preview). The control the browser
// draws for a chosen file is not a reliable preview: a phone's webview can draw its thumbnail black, which
// leaves the visitor unable to tell whether they picked the right photo. The input is untouched and still
// carries the file; this adds a small picture and a caption beneath it.
//
// The picture is made on a canvas, scaled down, and shown as a data: URL. That keeps a 10 MB photo from being
// held on a phone just to draw a thumbnail, and needs no blob: source, so a strict Content-Security-Policy
// (img-src 'self' data:) allows it. Without this script, or for a file the browser cannot decode, the page
// still works and the caption alone says what was chosen.
(function () {
  var MAX_EDGE = 640;

  function describe(file) {
    var size = file.size >= 1024 * 1024
      ? (file.size / (1024 * 1024)).toFixed(1) + ' MB'
      : Math.max(1, Math.round(file.size / 1024)) + ' KB';
    return file.name + ', ' + size;
  }

  function thumbnail(bitmap) {
    var scale = Math.min(1, MAX_EDGE / Math.max(bitmap.width, bitmap.height));
    var canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(bitmap.width * scale));
    canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    if (bitmap.close) bitmap.close();
    return canvas.toDataURL('image/jpeg', 0.8);
  }

  function enhance(input) {
    var box = document.createElement('div');
    box.className = 'wayfinder-file-preview';
    box.hidden = true;
    var picture = document.createElement('img');
    picture.className = 'wayfinder-file-preview__image';
    picture.alt = 'Preview of the file you chose';
    picture.hidden = true;
    var caption = document.createElement('p');
    caption.className = 'govuk-hint wayfinder-file-preview__caption';
    caption.setAttribute('role', 'status');
    box.append(picture, caption);
    input.insertAdjacentElement('afterend', box);

    var latest = 0;
    input.addEventListener('change', function () {
      var file = input.files && input.files[0];
      var ticket = ++latest;
      picture.hidden = true;
      picture.removeAttribute('src');
      if (!file) {
        box.hidden = true;
        caption.textContent = '';
        return;
      }
      box.hidden = false;
      caption.textContent = 'Chosen: ' + describe(file);
      if (!window.createImageBitmap) return;
      window.createImageBitmap(file).then(function (bitmap) {
        if (ticket !== latest) return;
        picture.src = thumbnail(bitmap);
        picture.hidden = false;
      }, function () {
        if (ticket !== latest) return;
        caption.textContent = 'Chosen: ' + describe(file) + '. A preview is not available for this file, but it is chosen.';
      });
    });
  }

  document.querySelectorAll('input[type="file"][data-wayfinder-file-preview]').forEach(enhance);
})();
