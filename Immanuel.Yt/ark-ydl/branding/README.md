# ARK-RAPID branding

This folder holds the logo sources and builds every icon the web app ships. It is not shipped
itself: `.dockerignore` keeps it out of the image.

| File | What it is |
|---|---|
| `source/*.png` | The original logo artwork, as supplied: `lockup` (emblem and wordmark), `tile-dark`, `tile-light`, `tile-mono`. Each comes in an opaque and a `-transparent` version. Keep these for reference only; nothing reads them. |
| `ark-rapid.svg` | **The vector master.** The arch, hull and waves were traced from `source/lockup.png` with potrace, after mirroring the left half so they are exactly symmetric about x = 730. The play button, arrow and 37 sun rays were redrawn as geometry. The wordmark and tagline were traced as well. Every path has an `id`. |
| `build.py` | Builds all web assets from the master. |

## Rebuild

```sh
python3 branding/build.py        # needs Pillow, and Inkscape (or $INKSCAPE) for SVG -> PNG
```

The script writes into `src/Ark.Ydl/wwwroot`:

| Output | Used for |
|---|---|
| `favicon.ico` (16/32/48), `img/favicon.svg` | Browser tabs. Small sizes use a simplified mark: no rays, a bolder hull, a larger play button and arrow. |
| `img/apple-touch-icon.png` (180) | iOS home screen. Full bleed, because iOS rounds the corners itself. |
| `img/icon.svg`, `img/icon-192.png`, `img/icon-512.png` | PWA `any` icons, plus the notification icon and the job-thumbnail fallback. |
| `img/icon-maskable-192.png`, `img/icon-maskable-512.png` | PWA `maskable` icons. The emblem stays inside the 80 % safe circle. |
| `img/icon-monochrome.png` | PWA `monochrome` icon: a white silhouette defined by alpha only. |
| `img/mark.svg` | The `#mark`, `#wordmark` and `#tagline` symbols the page uses, coloured by CSS. |
| `img/logo.svg`, `img/logo.png` | The full lockup. `logo.svg` follows the viewer's light/dark setting (used in the README). |

The palette lives at the top of `build.py`: navy `#0d2841`, gold `#d0a548` and teal `#1d7b8c`,
sampled from the artwork, plus the brighter cream/gold/teal used on navy tiles. If you change it,
change the matching tokens at the top of `src/Ark.Ydl/wwwroot/css/app.css` too. Browsers cache
icons hard (`max-age=86400` here, and longer for favicons), so a new icon can take a day to show.
