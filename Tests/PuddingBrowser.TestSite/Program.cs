var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/frame", () => Results.Content("""
<!doctype html><html><body><button id="frame-button">Frame action</button></body></html>
""", "text/html"));
app.MapGet("/", () => Results.Content("""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>Pudding Browser TestSite</title>
  <style>.hidden{display:none} body{font-family:Segoe UI;padding:24px;min-height:1400px} label{display:block;margin:8px 0}</style>
</head>
<body>
  <main>
    <h1>Browser automation test</h1>
    <form id="profile-form">
      <label>Name <input id="name" placeholder="Your name" data-testid="name-input"></label>
      <label>Role
        <select id="role"><option value="developer">Developer</option><option value="designer">Designer</option></select>
      </label>
      <label><input id="terms" type="checkbox"> Accept terms</label>
      <button id="save" type="submit" aria-label="Save profile">Save</button>
    </form>
    <p id="saved" class="hidden" role="status">Saved</p>
    <button id="replace">Replace action button</button>
    <button id="dynamic-action">Original action</button>
    <button id="popup" onclick="window.open('/frame','pudding-popup')">Open popup</button>
    <div id="shadow-host"></div>
    <iframe id="same-origin-frame" title="Test frame" src="/frame"></iframe>
  </main>
  <script>
    document.querySelector('#profile-form').addEventListener('submit', event => {
      event.preventDefault(); document.querySelector('#saved').classList.remove('hidden');
    });
    const nameInput = document.querySelector('#name');
    nameInput.addEventListener('input', () => nameInput.dataset.observedValue = nameInput.value);
    nameInput.addEventListener('keydown', event => {
      if (event.key === 'Tab') nameInput.dataset.pressed = 'true';
    });
    document.querySelector('#save').addEventListener('mouseover', event => {
      event.currentTarget.dataset.hovered = 'true';
    });
    window.addEventListener('scroll', () => document.body.dataset.scrolled = 'true');
    document.querySelector('#replace').addEventListener('click', () => {
      const old = document.querySelector('#dynamic-action');
      const next = document.createElement('button'); next.id='dynamic-action'; next.textContent='Replacement action';
      old.replaceWith(next);
    });
    const shadow = document.querySelector('#shadow-host').attachShadow({mode:'open'});
    shadow.innerHTML='<button id="shadow-action">Shadow action</button>';
  </script>
</body>
</html>
""", "text/html"));

app.Run();
