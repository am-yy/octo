"""Builds octo/wwwroot/admin/icons.svg, the dashboard's one icon sprite.

Phosphor glyphs come from the @phosphor-icons/core package on npm, the same version the Octo
apps use, checked against its published sha512. Brand marks come from brands/, one cleaned file
per service; SOURCES.md says where each came from.

Run from anywhere, with Python 3 and nothing else installed:

    python scripts/admin-icons/make_sprite.py

The page loads the sprite once and puts it in the document, so every icon is a
<svg><use href="#i-name"/></svg> (Phosphor) or <use href="#b-name"/> (a brand).
"""

import base64
import hashlib
import io
import json
import re
import tarfile
import urllib.request
from pathlib import Path

PHOSPHOR_VERSION = '2.1.1'
PHOSPHOR_URL = f'https://registry.npmjs.org/@phosphor-icons/core/-/core-{PHOSPHOR_VERSION}.tgz'
PHOSPHOR_SHA512 = 'v4ARvrip4qBCImOE5rmPUylOEK4iiED9ZyKjcvzuezqMaiRASCHKcRIuvvxL/twvLpkfnEODCOJp5dM4eZilxQ=='

HERE = Path(__file__).resolve().parent
OUT = HERE.parent.parent / 'octo' / 'wwwroot' / 'admin' / 'icons.svg'


def phosphor_tarball() -> tarfile.TarFile:
    data = urllib.request.urlopen(PHOSPHOR_URL).read()
    digest = base64.b64encode(hashlib.sha512(data).digest()).decode()
    if digest != PHOSPHOR_SHA512:
        raise SystemExit(f'Phosphor {PHOSPHOR_VERSION} did not match its published sha512; refusing to use it.')
    return tarfile.open(fileobj=io.BytesIO(data), mode='r:gz')


def inner(svg: str) -> tuple[str, str]:
    """The viewBox and the children of an <svg> element."""
    svg = re.sub(r'<\?xml[^>]*>', '', svg).strip()
    view_box = re.search(r'viewBox="([^"]+)"', svg).group(1)
    body = re.sub(r'^<svg[^>]*>|</svg>\s*$', '', svg, flags=re.S).strip()
    return view_box, body


def main() -> None:
    wanted = json.loads((HERE / 'icons.json').read_text(encoding='utf8'))
    symbols = []

    with phosphor_tarball() as tar:
        for weight, names in wanted['phosphor'].items():
            for name in names:
                file = name if weight == 'regular' else f'{name}-{weight}'
                member = tar.extractfile(f'package/assets/{weight}/{file}.svg')
                if member is None:
                    raise SystemExit(f'Phosphor has no {weight} "{name}".')
                view_box, body = inner(member.read().decode('utf8'))
                icon_id = f'i-{name}' if weight == 'regular' else f'i-{name}-{weight}'
                symbols.append(f'<symbol id="{icon_id}" viewBox="{view_box}" fill="currentColor">{body}</symbol>')

    for name in wanted['brands']:
        view_box, body = inner((HERE / 'brands' / f'{name}.svg').read_text(encoding='utf8'))
        body = re.sub(r'\s*\n\s*', ' ', body)
        # Ids inside a brand (gradients, clip paths) share the page with every other symbol.
        for local in re.findall(r'\bid="([^"]+)"', body):
            body = body.replace(f'id="{local}"', f'id="b-{name}-{local}"')
            body = body.replace(f'url(#{local})', f'url(#b-{name}-{local})')
            body = body.replace(f'href="#{local}"', f'href="#b-{name}-{local}"')
        symbols.append(f'<symbol id="b-{name}" viewBox="{view_box}">{body}</symbol>')

    # Hidden by size, not display:none: a gradient inside an undisplayed <svg> never paints.
    OUT.write_text(
        '<svg xmlns="http://www.w3.org/2000/svg" aria-hidden="true" width="0" height="0" style="position:absolute;width:0;height:0;overflow:hidden">\n'
        f'<!-- Built by scripts/admin-icons/make_sprite.py. Do not edit by hand.\n'
        f'     Phosphor Icons {PHOSPHOR_VERSION}, MIT, (c) 2023 Phosphor Icons: https://phosphoricons.com\n'
        '     Brand marks belong to their owners and identify their services; see scripts/admin-icons/SOURCES.md. -->\n'
        + '\n'.join(symbols) + '\n</svg>\n',
        encoding='utf8', newline='\n')
    print(f'{OUT}: {len(symbols)} icons, {OUT.stat().st_size:,} bytes')


if __name__ == '__main__':
    main()
