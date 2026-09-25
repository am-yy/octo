# Cover kit notice

The playlist cover templates in this folder come from ncfer's cover kit,
vendored so Octo can draw mix and radio station covers itself (#54).

- Source: <https://github.com/ncfer/octo-cover-kit>
- Commit: `050c70ad2611ee978c093d55f8ae400eae9c17ba` (2026-09-22)
- Vendored: `templates/*.svg` (39 files), `palette.json` and `LICENSE`,
  byte for byte.
- Not vendored: `render.py`, the kit's reference renderer (Octo draws the
  templates with its own `SvgTemplateRenderer`), and `README.md`.
- Font: the decade templates name Bricolage Grotesque. Octo does not ship it
  and draws that lettering in DejaVu Sans Bold, which its image carries.

## Where each icon comes from

Taken from the kit's README and the `icon_source` field in `palette.json`.

- **Lucide** (ISC), 30 covers: Pop, Hip-Hop, Reggaeton, Electronic,
  Dance & House, Rock, Metal, Punk, R&B & Soul, Classical, Soundtrack, Latin,
  Afro, Indie, Lo-fi & Chill, Kids, Games & Anime, Gospel, World, Other,
  Your Mix, Discovery, Top, \* Liked Songs, ◆ Keep, ◆ Replace, ◆ Trash,
  ▸ Covers, ▸ Lyrics and ▸ Review. Some Lucide icons derive from Feather,
  whose MIT notice is part of Lucide's licence below.
- **Tabler** (MIT), 1 cover: Reggae, Tabler's `cannabis` icon.
- **Phosphor** (MIT), 1 cover: Folk & Country, Phosphor's `cowboy-hat` icon.
- **ncfer, original** (MIT, the kit's own), 3 covers: Jazz & Blues, K-Pop and
  Flamenco.
- **Lettering, no icon** (MIT, the kit's own), 4 covers: 1990s, 2000s, 2010s
  and 2020s.

## Licences

### The cover kit (MIT)

```text
MIT License

Copyright (c) 2026 ncfer

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Lucide (ISC, with Feather's MIT notice)

From <https://github.com/lucide-icons/lucide>. The list of Feather-derived
icon names is re-wrapped to fit this file; its content is unchanged.

```text
ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

---

The following Lucide icons are derived from the Feather project:

airplay, alert-circle, alert-octagon, alert-triangle, aperture,
arrow-down-circle, arrow-down-left, arrow-down-right, arrow-down,
arrow-left-circle, arrow-left, arrow-right-circle, arrow-right,
arrow-up-circle, arrow-up-left, arrow-up-right, arrow-up, at-sign, calendar,
cast, check, chevron-down, chevron-left, chevron-right, chevron-up,
chevrons-down, chevrons-left, chevrons-right, chevrons-up, circle, clipboard,
clock, code, columns, command, compass, corner-down-left, corner-down-right,
corner-left-down, corner-left-up, corner-right-down, corner-right-up,
corner-up-left, corner-up-right, crosshair, database, divide-circle,
divide-square, dollar-sign, download, external-link, feather, frown, hash,
headphones, help-circle, info, italic, key, layout, life-buoy, link-2, link,
loader, lock, log-in, log-out, maximize, meh, minimize, minimize-2,
minus-circle, minus-square, minus, monitor, moon, more-horizontal,
more-vertical, move, music, navigation-2, navigation, octagon, pause-circle,
percent, plus-circle, plus-square, plus, power, radio, rss, search, server,
share, shopping-bag, sidebar, smartphone, smile, square, table-2, tablet,
target, terminal, trash-2, trash, triangle, tv, type, upload, x-circle,
x-octagon, x-square, x, zoom-in, zoom-out

The MIT License (MIT) (for the icons listed above)

Copyright (c) 2013-present Cole Bemis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Tabler Icons (MIT)

From <https://github.com/tabler/tabler-icons>.

```text
MIT License

Copyright (c) 2020-2026 Paweł Kuna

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Phosphor Icons (MIT)

From <https://github.com/phosphor-icons/core>.

```text
MIT License

Copyright (c) 2023 Phosphor Icons

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
