#!/usr/bin/env python3
"""Build the static website into _site/.

- Copies site/ (landing page, og image) verbatim.
- Renders docs/*.md into _site/docs/*.html at build time, so the published
  docs NEVER drift from docs/*.md — the markdown files stay the single
  source of truth and no generated HTML is committed.

Used by .github/workflows/pages.yml on every push to master.
Requires: python3 + the `markdown` package (pip install markdown).
"""
import html as html_mod
import json
import re
import shutil
import sys
from pathlib import Path

import markdown

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"
SITE = ROOT / "site"
OUT = ROOT / "_site"
REPO = "https://github.com/Neftedollar/WTF"
# The EFFECTIVE serving domain (neftedollar.github.io/WTF 301s here).
# Canonicals, og:url, sitemap and JSON-LD must all use this base.
BASE_URL = "https://neftedollar.com/WTF"

# Sidebar order + human titles. Every docs/*.md must be listed here so the
# build fails loudly when a new page is added but not wired into the nav.
NAV = [
    ("index", "Overview"),
    ("installation", "Installation"),
    ("quickstart", "Quickstart"),
    ("configuration", "Configuration"),
    ("keybindings", "Keybindings"),
    ("appearance", "Appearance & ricing"),
    ("wtfctl", "wtfctl & the socket"),
    ("troubleshooting", "Troubleshooting"),
    ("faq", "FAQ"),
    ("architecture", "Architecture"),
    ("CONFIG-EDITING", "Config editing"),
    ("AOT", "NativeAOT build"),
]

# Per-page SEO head data. `title` is the full <title> (keep ≤ ~62 chars,
# front-load the distinguishing keywords); `desc` is the meta description
# (~140-165 chars, honest, hand-written from the page content).
# NOTE: the landing page owns the head terms ("wayland tiling window
# manager", "tiling compositor F#") — docs titles deliberately target
# long-tail modifiers (install, quickstart, keybindings, …) to avoid
# cannibalizing it. Every NAV slug must have an entry (checked at build).
META = {
    "index": (
        "WTF Documentation — guides for the F# Wayland compositor",
        "WTF documentation: a tiling Wayland compositor configured in real "
        "F#. Design (F# brain, C body), feature overview, and where to "
        "start reading.",
    ),
    "installation": (
        "Installing WTF on Debian, Ubuntu, Fedora & Arch · WTF docs",
        "Install WTF with one command on Debian, Ubuntu 24.04, Fedora, Arch, "
        "or openSUSE — prebuilt x86_64/aarch64 packages, no .NET SDK, meson, "
        "or compiler needed.",
    ),
    "quickstart": (
        "Quickstart — first session & day-one keys · WTF docs",
        "Your first WTF session: the ten keybindings you need on day one, "
        "what the seeded config gives you, and how to try WTF nested in a "
        "window with zero risk.",
    ),
    "configuration": (
        "Configuring WTF in F# — config.fsx & hot-reload · WTF docs",
        "Configure WTF in ~/.config/wtf/config.fsx — real F# with "
        "autocomplete and type-checking, hot-reloaded on every save with a "
        "last-good fallback.",
    ),
    "keybindings": (
        "Keybindings — chord syntax & default keymap · WTF docs",
        "WTF keybinding reference: chord syntax like M-S-j, modifier order, "
        "the full default keymap, and how to define your own binds in F#.",
    ),
    "appearance": (
        "Appearance & ricing — blur, shadows, wallpapers · WTF docs",
        "Ricing WTF: borders, gaps, rounded corners, blur, macOS-style "
        "shadows, animations, and dynamic .heic wallpapers — hot-reloadable "
        "and live-tunable via wtfctl.",
    ),
    "wtfctl": (
        "wtfctl — JSON socket for scripts & LLM agents · WTF docs",
        "wtfctl and the WTF control socket: the whole WM state as one JSON "
        "document, semantic NDJSON commands, and a tool manifest for LLM "
        "agents.",
    ),
    "troubleshooting": (
        "Troubleshooting — session logs, crashes, safe mode · WTF docs",
        "Troubleshooting WTF: where session logs live "
        "(~/.local/state/wtf/), reading crash backtraces, safe mode, and how "
        "the session wrapper recovers.",
    ),
    "faq": (
        "FAQ — stability, NVIDIA, multi-monitor, LLM agents · WTF docs",
        "WTF FAQ: how stable the 0.1 beta is, why F#, NVIDIA status, "
        "multi-monitor plans, LLM agent control, and how WTF compares to "
        "xMonad, sway, and Hyprland.",
    ),
    "architecture": (
        "Architecture — F# brain, C body (wlroots + scenefx) · WTF docs",
        "How WTF works: an 'F# brain, C body' split — pure, fully-tested F# "
        "window-management logic driving a thin C shim over wlroots 0.18 + "
        "scenefx.",
    ),
    "CONFIG-EDITING": (
        "Editing config.fsx with autocomplete · WTF docs",
        "Edit your WTF config with machine-aware autocomplete: Type "
        "Providers turn your installed apps, layouts, and keyboard layouts "
        "into types the F# LSP completes.",
    ),
    "AOT": (
        "NativeAOT build — feature matrix & trade-offs · WTF docs",
        "WTF's NativeAOT build: a small, fast-starting native binary — the "
        "feature matrix of what the AOT flavor keeps and what it trades "
        "away vs the default JIT build.",
    ),
}

FAVICON = ("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' "
           "viewBox='0 0 32 32'%3E%3Crect width='32' height='32' rx='7' "
           "fill='%231e1e2e'/%3E%3Ctext x='16' y='22' font-family='monospace' "
           "font-size='14' font-weight='bold' fill='%2389b4fa' "
           "text-anchor='middle'%3E%7C%26gt%3B%3C/text%3E%3C/svg%3E")

TEMPLATE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{page_title}</title>
<meta name="description" content="{description}">
<link rel="canonical" href="{canonical}">
<link rel="sitemap" type="application/xml" title="Sitemap" href="../sitemap.xml">
<meta property="og:type" content="article">
<meta property="og:site_name" content="WTF — Wayland Tiling, F#">
<meta property="og:title" content="{page_title}">
<meta property="og:description" content="{description}">
<meta property="og:url" content="{canonical}">
<meta property="og:image" content="{base}/og.png">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:title" content="{page_title}">
<meta name="twitter:description" content="{description}">
<meta name="twitter:image" content="{base}/og.png">
<meta name="theme-color" content="#1e1e2e">
<link rel="icon" href="{favicon}">
{jsonld}
<style>
:root{{
  --crust:#11111b; --mantle:#181825; --base:#1e1e2e;
  --surface0:#313244; --surface1:#45475a; --overlay:#6c7086;
  --text:#cdd6f4; --subtext:#a6adc8;
  --blue:#89b4fa; --mauve:#cba6f7; --green:#a6e3a1; --peach:#fab387;
  --yellow:#f9e2af; --teal:#94e2d5;
  --mono:ui-monospace,'Cascadia Code','JetBrains Mono','Fira Code',Menlo,Consolas,monospace;
  --sans:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;
}}
*{{box-sizing:border-box;margin:0;padding:0}}
body{{background:var(--base);color:var(--text);font-family:var(--sans);line-height:1.65;font-size:15.5px;-webkit-font-smoothing:antialiased}}
a{{color:var(--blue);text-decoration:none}}
a:hover{{text-decoration:underline}}
a:focus-visible{{outline:2px solid var(--blue);outline-offset:2px;border-radius:4px}}
.top{{border-bottom:1px solid var(--surface0);background:var(--mantle)}}
.top .in{{max-width:1100px;margin:0 auto;padding:13px 20px;display:flex;gap:16px;align-items:center;flex-wrap:wrap}}
.brand{{font-family:var(--mono);font-weight:700;font-size:15px;color:var(--text)!important}}
.brand b{{color:var(--blue)}}
.top .right{{margin-left:auto;display:flex;gap:16px;font-size:13.5px}}
.top .right a{{color:var(--subtext)}}
.layout{{max-width:1100px;margin:0 auto;padding:28px 20px 60px;display:grid;grid-template-columns:210px minmax(0,1fr);gap:40px}}
nav.side{{position:sticky;top:24px;align-self:start;font-size:14px}}
nav.side .h{{font-family:var(--mono);font-size:11px;letter-spacing:.09em;text-transform:uppercase;color:var(--overlay);margin:0 0 8px 10px}}
nav.side a{{display:block;padding:5px 10px;border-radius:7px;color:var(--subtext)}}
nav.side a:hover{{color:var(--text);background:var(--mantle);text-decoration:none}}
nav.side a[aria-current]{{color:var(--blue);background:var(--mantle);font-weight:600}}
nav.side .back{{margin:14px 0 0;border-top:1px solid var(--surface0);padding-top:12px}}
nav.side .back a{{color:var(--overlay);font-size:13px}}
main{{min-width:0}}
main h1{{font-size:clamp(24px,4vw,32px);line-height:1.2;letter-spacing:-.01em;margin:0 0 14px}}
main h2{{font-size:21px;margin:34px 0 10px;padding-top:10px;border-top:1px solid var(--surface0)}}
main h3{{font-size:17px;margin:26px 0 8px}}
main h4{{font-size:15px;margin:20px 0 6px;color:var(--subtext)}}
main p,main ul,main ol{{margin:0 0 14px;color:var(--subtext)}}
main li{{margin:4px 0}}
main ul,main ol{{padding-left:24px}}
main strong,main b{{color:var(--text)}}
main code{{font-family:var(--mono);font-size:.88em;color:var(--teal);background:var(--crust);border:1px solid var(--surface0);border-radius:4px;padding:1px 5px}}
main pre{{background:var(--crust);border:1px solid var(--surface0);border-radius:10px;padding:14px 16px;overflow-x:auto;margin:0 0 16px;scrollbar-width:thin;scrollbar-color:var(--surface1) transparent}}
main pre::-webkit-scrollbar{{height:8px}}
main pre::-webkit-scrollbar-thumb{{background:var(--surface1);border-radius:4px}}
main pre code{{border:none;background:none;padding:0;color:var(--text);font-size:13px;line-height:1.6}}
main blockquote{{border-left:3px solid var(--peach);background:var(--mantle);border-radius:0 8px 8px 0;padding:10px 16px;margin:0 0 16px}}
main blockquote p{{margin:0;color:var(--subtext)}}
main hr{{border:none;border-top:1px solid var(--surface0);margin:26px 0}}
.tbl{{overflow-x:auto;margin:0 0 16px;border:1px solid var(--surface0);border-radius:10px}}
main table{{border-collapse:collapse;width:100%;font-size:13.5px;background:var(--mantle)}}
main th,main td{{padding:9px 13px;text-align:left;border-bottom:1px solid var(--surface0);vertical-align:top;color:var(--subtext)}}
main th{{font-family:var(--mono);font-size:12px;color:var(--subtext);background:var(--crust)}}
main tr:last-child td{{border-bottom:none}}
.edit{{margin-top:40px;border-top:1px solid var(--surface0);padding-top:14px;font-size:13px;color:var(--overlay)}}
.edit a{{color:var(--subtext)}}
@media(max-width:840px){{
  .layout{{grid-template-columns:1fr;gap:20px}}
  nav.side{{position:static}}
  nav.side .h{{margin-left:0}}
  nav.side ul{{display:flex;flex-wrap:wrap;gap:4px}}
  nav.side a{{background:var(--mantle);border:1px solid var(--surface0)}}
}}
nav.side ul{{list-style:none;padding:0}}
</style>
</head>
<body>
<div class="top"><div class="in">
  <a class="brand" href="../"><b>WTF</b> — Wayland Tiling, F#</a>
  <span class="right">
    <a href="../">Home</a>
    <a href="{repo}/releases">Releases</a>
    <a href="{repo}">GitHub</a>
  </span>
</div></div>
<div class="layout">
<nav class="side" aria-label="Documentation">
  <p class="h">Documentation</p>
  <ul>
{nav}
  </ul>
  <p class="back"><a href="../">&larr; back to the landing page</a></p>
</nav>
<main>
{body}
<p class="edit">Found a problem on this page? <a href="{repo}/blob/master/docs/{md_name}">Edit it on GitHub</a> — the site rebuilds from <code>docs/</code> automatically.</p>
</main>
</div>
</body>
</html>
"""


def gh_slugify(value, separator):
    """Match GitHub's heading-anchor slugs so cross-page #fragments keep working."""
    value = value.lower()
    value = re.sub(r"[^\w\- ]", "", value)
    return value.replace(" ", separator)


def rewrite_href(href, doc_stems):
    if href.startswith(("http://", "https://", "#", "mailto:")):
        return href
    target, _, frag = href.partition("#")
    frag = f"#{frag}" if frag else ""
    if target.startswith("../"):  # out of docs/ -> the GitHub repo
        return f"{REPO}/blob/master/{target[3:]}{frag}"
    if target.endswith(".md"):
        stem = target[:-3]
        if stem in doc_stems:  # sibling doc page
            return f"{stem}.html{frag}"
        return f"{REPO}/blob/master/docs/{target}{frag}"
    return f"{REPO}/blob/master/docs/{target}{frag}"  # any other repo-relative file


def convert(md_path, doc_stems):
    text = md_path.read_text(encoding="utf-8")
    md = markdown.Markdown(
        extensions=["extra", "toc", "sane_lists"],
        extension_configs={"toc": {"slugify": gh_slugify, "toc_depth": "2-4"}},
    )
    body = md.convert(text)
    body = re.sub(
        r'href="([^"]+)"',
        lambda m: f'href="{rewrite_href(m.group(1), doc_stems)}"',
        body,
    )
    # tables need their own horizontal-scroll container on small screens
    body = body.replace("<table>", '<div class="tbl"><table>').replace(
        "</table>", "</table></div>"
    )
    return body


def strip_tags(html):
    """Rendered HTML -> plain text (for JSON-LD answer bodies)."""
    text = re.sub(r"<[^>]+>", " ", html)
    return re.sub(r"\s+", " ", html_mod.unescape(text)).strip()


def jsonld_script(data):
    return ('<script type="application/ld+json">\n'
            + json.dumps(data, ensure_ascii=False, indent=1)
            + "\n</script>")


def breadcrumb_jsonld(stem, title, canonical):
    """BreadcrumbList: Home -> Docs (-> page)."""
    items = [
        {"@type": "ListItem", "position": 1, "name": "WTF",
         "item": f"{BASE_URL}/"},
        {"@type": "ListItem", "position": 2, "name": "Documentation",
         "item": f"{BASE_URL}/docs/"},
    ]
    if stem != "index":
        items.append({"@type": "ListItem", "position": 3, "name": title,
                      "item": canonical})
    return {"@context": "https://schema.org", "@type": "BreadcrumbList",
            "itemListElement": items}


def faq_jsonld(body, canonical):
    """FAQPage JSON-LD from the rendered faq page: each <h2> is a question,
    everything until the next <h2> is its answer (as plain text)."""
    entities = []
    for m in re.finditer(r"<h2[^>]*>(.*?)</h2>(.*?)(?=<h2|\Z)", body, re.S):
        question = strip_tags(m.group(1))
        answer = strip_tags(m.group(2))
        if question and answer:
            entities.append({
                "@type": "Question",
                "name": question,
                "acceptedAnswer": {"@type": "Answer", "text": answer},
            })
    if not entities:
        sys.exit("build-site.py: faq.md produced no Q/A pairs for FAQPage "
                 "JSON-LD — check its <h2> structure.")
    return {"@context": "https://schema.org", "@type": "FAQPage",
            "url": canonical, "mainEntity": entities}


def write_sitemap(canonicals):
    """sitemap.xml at the site root, listing landing + every docs page.
    NOTE: /WTF/robots.txt is never read by crawlers (robots.txt is
    host-root-only, and the host root isn't ours), so this sitemap must be
    submitted directly in Google Search Console / Bing Webmaster Tools."""
    urls = "\n".join(
        f"  <url><loc>{html_mod.escape(u)}</loc></url>"
        for u in [f"{BASE_URL}/"] + canonicals
    )
    (OUT / "sitemap.xml").write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n'
        f"{urls}\n</urlset>\n",
        encoding="utf-8",
    )
    print(f"  sitemap.xml  ({1 + len(canonicals)} URLs)")


def main():
    md_files = sorted(DOCS.glob("*.md"))
    doc_stems = {p.stem for p in md_files}
    nav_stems = {s for s, _ in NAV}
    if doc_stems != nav_stems:
        sys.exit(
            f"build-site.py: docs/ and NAV are out of sync.\n"
            f"  missing from NAV: {sorted(doc_stems - nav_stems)}\n"
            f"  missing from docs/: {sorted(nav_stems - doc_stems)}"
        )
    if set(META) != nav_stems:
        sys.exit(
            f"build-site.py: META and NAV are out of sync.\n"
            f"  missing from META: {sorted(nav_stems - set(META))}\n"
            f"  stale in META: {sorted(set(META) - nav_stems)}"
        )

    if OUT.exists():
        shutil.rmtree(OUT)
    shutil.copytree(SITE, OUT)
    (OUT / "docs").mkdir()

    canonicals = []
    for stem, title in NAV:
        nav_html = "\n".join(
            '    <li><a href="{s}.html"{cur}>{t}</a></li>'.format(
                s=s, t=t, cur=' aria-current="page"' if s == stem else ""
            )
            for s, t in NAV
        )
        body = convert(DOCS / f"{stem}.md", doc_stems)
        # GitHub Pages serves docs/index.html at /docs/ — canonicalize there.
        canonical = (f"{BASE_URL}/docs/" if stem == "index"
                     else f"{BASE_URL}/docs/{stem}.html")
        canonicals.append(canonical)
        page_title, description = META[stem]
        jsonld = jsonld_script(breadcrumb_jsonld(stem, title, canonical))
        if stem == "faq":
            jsonld += "\n" + jsonld_script(faq_jsonld(body, canonical))
        page = TEMPLATE.format(
            page_title=html_mod.escape(page_title, quote=True),
            description=html_mod.escape(description, quote=True),
            canonical=canonical, jsonld=jsonld,
            md_name=f"{stem}.md", nav=nav_html,
            body=body, repo=REPO, base=BASE_URL, favicon=FAVICON,
        )
        (OUT / "docs" / f"{stem}.html").write_text(page, encoding="utf-8")
        print(f"  docs/{stem}.html  <- docs/{stem}.md")

    write_sitemap(canonicals)
    print(f"built {len(NAV)} doc pages + landing into {OUT.relative_to(ROOT)}/")


if __name__ == "__main__":
    main()
