// Starts a date or time field on the visitor's own device clock. The server cannot know their local day or
// time, so the markup only says what to do (data-wayfinder-device-default="today" on a date input,
// "time" on a text input) and this fills it in. A field that already has a value is left alone, so a saved
// answer, or one the visitor has typed, is never overwritten. Without this script the field is just empty.
(function () {
  var pad = function (number) { return String(number).padStart(2, '0'); };

  function announce(input) {
    input.dispatchEvent(new Event('input', { bubbles: true }));
  }

  function fillDate(group, now) {
    var parts = ['day', 'month', 'year'].map(function (part) { return group.querySelector('input[id$="-' + part + '"]'); });
    if (parts.some(function (input) { return !input || input.value.trim() !== ''; })) return;
    parts[0].value = String(now.getDate());
    parts[1].value = String(now.getMonth() + 1);
    parts[2].value = String(now.getFullYear());
    parts.forEach(announce);
  }

  function fillTime(input, now) {
    if (input.value.trim() !== '') return;
    input.value = pad(now.getHours()) + ':' + pad(now.getMinutes());
    announce(input);
  }

  var now = new Date();
  document.querySelectorAll('[data-wayfinder-device-default]').forEach(function (element) {
    var kind = element.getAttribute('data-wayfinder-device-default');
    if (kind === 'today') fillDate(element, now);
    else if (kind === 'time' && element.tagName === 'INPUT') fillTime(element, now);
  });
})();
