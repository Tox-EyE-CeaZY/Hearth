# The icon pipeline

The part that decides whether Hearth looks designed or looks like a bigger
Windows desktop.

## The problem

Android icons look coherent because the platform enforces it. An adaptive icon
is a foreground layer and a background layer, both 108dp, with a 66dp safe zone,
masked by a system-wide shape. Every icon on the screen has the same silhouette,
the same visual weight, and the same amount of breathing room.

Windows has none of that. In a single grid you will find:

- Full-square artwork with its own background (most modern apps)
- Transparent logos with irregular outlines (most installers' shortcuts)
- White-on-transparent logos that vanish on a light wallpaper
- Icons with a drop shadow already baked in
- Wildly different internal padding — some icons fill their canvas, others
  float at 60%
- Legacy apps whose largest resource is still 32×32

Rendering that set at 96px produces visual noise. The icons are not the
problem; the *absence of a system* is.

## What Hearth does

### 1. Extract at 256px

`IShellItemImageFactory::GetImage` with `SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY`.
This is the only extraction API that reaches both classic `.ico` resources and
packaged apps' PNG assets, and the only one that returns genuine 256px artwork
rather than an upscale.

Extraction happens once per item at full size; every display size is derived
from that, which is both faster and sharper than re-extracting per size.

The returned `HBITMAP` carries **premultiplied** alpha. Hearth un-premultiplies
immediately, because every analysis step downstream wants the artwork's true
colours — premultiplied pixels drag their own alpha into the channel values,
which biases dominant-colour picking toward black on anything with soft edges.

A uniformly-zero alpha channel (some old 32bpp resources) is treated as opaque
rather than as fully transparent, which is what it literally means and would
otherwise render as nothing at all.

### 2. Measure

`IconAnalysis` walks the pixels once and produces:

- **Content bounds** — the tight alpha bounding box, which is how we rescue
  icons that ship with wildly different built-in padding
- **Content fill** — how much of that box is actually opaque
- **Aspect ratio** and **canvas fill**
- **Dominant colour** — see below
- **Foreground luminance** — WCAG relative luminance, opacity-weighted
- **Monochrome** flag

### 3. Classify

```
IsFullBleed = ContentFill > 0.90 && AspectRatio > 0.85 && CanvasFill > 0.55
```

A full-bleed icon is already a tile. It gets masked into the chosen shape and
nothing else — generating a background behind already-opaque artwork would just
shrink it for no reason.

Everything else gets a generated background.

### 4. Generate a background

This is the step that handles transparent logos, and the one that has to look
*chosen* rather than computed.

**Dominant colour, not average colour.** Averaging produces mud. Hearth builds a
coarse histogram (5 bits per channel — fine enough to keep a brand colour
distinct, coarse enough that antialiased edges land in the same bucket as the
solid body they came from), weights each vote by saturation and alpha, and
excludes near-white, near-black and unsaturated pixels from voting at all, since
those are almost always padding, outline or shadow rather than the icon's
identity. The winning bucket's true colours are then averaged so the result is
the artwork's actual colour, not the bucket's quantised centre.

**Push to a tonal step.** The dominant hue is kept, but lightness and saturation
are pushed to fixed targets — dark artwork gets a light background, light
artwork gets a dark one — the way Material You builds a tonal palette. This is
what makes a screen full of generated backgrounds look deliberate.

**Verify contrast.** The background lightness walks away from the foreground
until they reach a 2.0 contrast ratio. That is well below text-contrast
requirements on purpose: icon artwork needs to *sit on* its background, not be
read.

**A barely-there gradient.** ±4% lightness, vertical. Enough that sixty flat
tiles do not look like printed stickers; not enough to notice as a gradient.

### 5. Mask

`IconShape` builds the silhouette. The squircle is a true superellipse
(`|x|^n + |y|^n = 1`, n = 4.2) sampled to a path, not the four-arcs
approximation — no flat spot where the arc meets the edge. Geometries are frozen
and cached per (shape, size).

Generated-background icons place their artwork in a **66/108 safe zone**, the
same ratio Android uses: large enough to read, small enough that no shape clips
it.

### 6. Bake the shadow

The drop shadow is rendered **into** the cached bitmap, once, at cache time.

This matters a lot. WPF's `DropShadowEffect` and `BlurEffect` are the classic
performance killers — a live effect on each of 200 tiles would cost a
render-target allocation and a blur pass per tile per frame. Baking costs one
blur per icon ever, and the live scene becomes textured quads with transforms,
which WPF renders at full speed.

The tile bitmap is padded by 10% per side to hold the shadow; `IconTile` and
`ComputeGrid` both account for that padding.

## Caching

Two levels, in `IconService`:

- **Memory** — keyed on item + every render option that affects output
- **Disk** — `%LocalAppData%\Hearth\icons\<hash>.png`, written temp-then-move so
  a crash mid-write cannot leave a truncated PNG that loads next launch

Duplicate in-flight requests are collapsed, because a relayout asks several
tiles for the same icon at once.

`CacheVersion` in `IconService` invalidates everything at once after a pipeline
change. Bump it whenever the rendering changes visibly.

### The render thread

Extraction and rendering never touch the UI thread — and never touch the thread
pool either. `DrawingVisual` and `RenderTargetBitmap` are `DispatcherObject`s
whose constructors create a `Dispatcher` for whatever thread they land on, so
pooling them would quietly leak one `Dispatcher` per worker thread. All
rendering is funnelled through a single long-lived STA thread running its own
dispatcher loop. Results are frozen before crossing back.

## What is not built yet

**Icon pack import** is the big one, and the most interesting idea in the
project. Android icon packs are APKs containing a mapping XML and a folder of
PNGs — parseable, and there are thousands of beautiful free ones representing an
enormous amount of design work. Mapping those onto Windows apps would mean
inheriting the Android look authentically rather than approximating it.

`LauncherItem.IconOverridePath` and `IconService.LoadRawFromFile` are the seam
this plugs into; what is missing is the APK parser and the app-name matching.

**Curated overrides** for the top few hundred Windows apps, shipped in-box, for
the cases where generation cannot win.

**Themed/monochrome mode** — Android 13's tinted icons, where every icon is
reduced to a single-colour glyph on a uniform background. The analysis pass
already flags monochrome artwork.
