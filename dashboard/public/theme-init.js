// Runs before the stylesheet and the app, so the first paint is already in the
// right theme instead of flashing dark then light (or the reverse).
//
// Mirrors resolveTheme() in src/theme/theme.ts; the key and values must match.
// A plain classic script on purpose: it is loaded as-is, outside the bundler.
(function () {
  var preference = 'system';
  try {
    var stored = window.localStorage.getItem('epp.theme');
    if (stored === 'light' || stored === 'dark' || stored === 'system') preference = stored;
  } catch {
    /* No storage: follow the system until the account preference arrives. */
  }

  var dark = preference === 'dark';
  if (preference === 'system') {
    try {
      dark = window.matchMedia('(prefers-color-scheme: dark)').matches;
    } catch {
      dark = true;
    }
  }

  var theme = dark ? 'dark' : 'light';
  var root = document.documentElement;
  root.setAttribute('data-theme', theme);
  root.style.colorScheme = theme;

  var meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', dark ? '#0b1220' : '#f3f5f9');
})();
