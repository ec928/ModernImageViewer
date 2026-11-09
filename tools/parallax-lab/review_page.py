# The watch-and-mark page for the rendered clips. usage: python -I review_page.py <review dir with cards.json>
import json, os, sys, html
d = sys.argv[1]
cards = json.load(open(os.path.join(d, 'cards.json')))
groups = [
    ('Fixed', 'Former Round-1 Failures — Now with Holistic Foreground Mask', 'These failed Round 1 because BiRefNet cut out only one building/animal and left its skyline, ground or perch branch in the background. Now everything connected in front of the far plane stays locked.'),
    ('Slide', 'Plays Slide (Rest of Pictures)', 'Clear subject + connected foreground plane against a distant background. Tabletop/floor/hillside/swamp photos and screenshots have been filtered out to Rock or Out.'),
    ('Rock', 'Plays Rock', 'In a Parallax show now, playing Rock.'),
    ('Out', 'Left out', 'Left out of a Parallax show.')
]
parts = []
for fit, title, note in groups:
    mine = sorted([c for c in cards if c['fit'] == fit], key=lambda c: -c['step'])
    if not mine: continue
    parts.append(f'<h2>{title} <span class="n">{len(mine)}</span></h2><p class="note">{note}</p><div class="grid">')
    for c in mine:
        f = html.escape(c['file'])
        parts.append(f'<figure class="card" data-file="{f}" data-fit="{fit}" data-play="{c["play"]}">'
                     f'<img loading="lazy" src="{c["anim"]}" alt="{f} playing {c["play"]}">'
                     f'<figcaption><span class="name">{f}</span><span class="why">{c["play"]} · {html.escape(c["why"])}</span>'
                     f'<span class="btns"><button class="pass" type="button">Pass</button><button class="fail" type="button">Fail</button></span></figcaption></figure>')
    parts.append('</div>')
page = f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Parallax clips (review4 — holistic foreground + filter)</title><style>
:root {{ --bg:#141414; --card:#1f1f1f; --text:#eee; --muted:#9a9a9a; --pass:#3fb950; --fail:#ff6b6b; --accent:#4cc2ff; }}
@media (prefers-color-scheme: light) {{ :root:not([data-theme="dark"]) {{ --bg:#f4f4f4; --card:#fff; --text:#1b1b1b; --muted:#666; --pass:#1a7f37; --fail:#c42b1c; --accent:#0067c0; }} }}
body {{ margin:0; padding:16px 20px 80px; background:var(--bg); color:var(--text); font:14px "Segoe UI Variable Text","Segoe UI",sans-serif; }}
h1 {{ font-size:22px; font-weight:600; margin:0 0 4px; }} h2 {{ font-size:18px; margin:28px 0 2px; }} .n {{ color:var(--muted); font-weight:400; }}
.note, .intro {{ color:var(--muted); margin:2px 0 10px; }}
.grid {{ display:grid; grid-template-columns:repeat(auto-fill,minmax(340px,1fr)); gap:12px; }}
.card {{ margin:0; background:var(--card); border-radius:8px; overflow:hidden; outline:3px solid transparent; }}
.card[data-mark="pass"] {{ outline-color:var(--pass); }} .card[data-mark="fail"] {{ outline-color:var(--fail); }}
.card img {{ width:100%; display:block; background:#000; }}
figcaption {{ display:grid; grid-template-columns:1fr auto; gap:2px 8px; padding:8px 10px; align-items:center; }}
.name {{ font-size:12px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }}
.why {{ grid-column:1; font-size:12px; color:var(--muted); }}
.btns {{ grid-column:2; grid-row:1 / span 2; display:flex; gap:6px; }}
.btns button {{ font:inherit; padding:5px 12px; border-radius:6px; border:1px solid #8886; background:transparent; color:var(--text); cursor:pointer; }}
.card[data-mark="pass"] .pass {{ background:var(--pass); color:#fff; border-color:var(--pass); }}
.card[data-mark="fail"] .fail {{ background:var(--fail); color:#fff; border-color:var(--fail); }}
.bar {{ position:fixed; left:0; right:0; bottom:0; display:flex; gap:12px; align-items:center; padding:10px 20px; background:var(--card); border-top:1px solid #8884; }}
.bar button {{ font:inherit; padding:6px 14px; border-radius:6px; border:0; background:var(--accent); color:#fff; cursor:pointer; }}
.count {{ color:var(--muted); }}
</style></head><body>
<h1>Parallax clips (review4 — holistic foreground + distant-plane filter)</h1>
<p class="intro">Foreground = BiRefNet subject + connected scene in front of the far background plane (skyline, ground, perch branches). Background slides purely horizontally with no vertical zoom halo. Pass if it belongs in a Parallax show playing that way, Fail if not. Then Copy and paste the result back.</p>
{''.join(parts)}
<div class="bar"><button id="copy" type="button">Copy</button><span class="count" id="count"></span></div>
<script>
const KEY = 'parallax-clips-review4';
let marks = {{}};
try {{ marks = JSON.parse(localStorage.getItem(KEY) || '{{}}'); }} catch (e) {{ marks = {{}}; }}
const cards = [...document.querySelectorAll('.card')];
function refresh() {{
  let p = 0, f = 0;
  for (const c of cards) {{ const m = marks[c.dataset.file] || ''; c.dataset.mark = m; if (m === 'pass') p++; if (m === 'fail') f++; }}
  document.getElementById('count').textContent = p + ' pass, ' + f + ' fail, ' + (cards.length - p - f) + ' not marked';
  try {{ localStorage.setItem(KEY, JSON.stringify(marks)); }} catch (e) {{}}
}}
for (const c of cards) for (const kind of ['pass', 'fail'])
  c.querySelector('.' + kind).addEventListener('click', () => {{
    marks[c.dataset.file] = marks[c.dataset.file] === kind ? '' : kind; refresh();
  }});
document.getElementById('copy').addEventListener('click', async () => {{
  const lines = cards.filter(c => marks[c.dataset.file]).map(c => marks[c.dataset.file].toUpperCase() + ' (' + c.dataset.fit + ', played ' + c.dataset.play + '): ' + c.dataset.file);
  const text = lines.length ? lines.join('\\n') : 'nothing marked';
  try {{ await navigator.clipboard.writeText(text); document.getElementById('count').textContent = lines.length + ' copied'; }}
  catch (e) {{ prompt('Copy this:', text); }}
}});
refresh();
</script></body></html>'''
open(os.path.join(d, 'index.html'), 'w', encoding='utf-8').write(page)
print(len(cards), 'cards')
