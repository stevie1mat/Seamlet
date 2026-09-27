# Seamlet

**A little less between you and your screens.**

Seamlet is the new product name for MultipleMouse. The name draws on the seam between
screens and a small utility that makes crossing it feel natural. The app shares a
Windows-connected mouse and plain text/files with a Mac on the local network.

## Identity

- Ink: `#173536` — headings, text, screen outlines.
- Teal: `#137968` — actions and connected states.
- Mint: `#65D6BB` — the link between devices.
- Paper: `#F4F3EE` — app backgrounds.
- Secondary text: `#607171`.
- Typography: the platform system font (SF on Mac, Segoe UI on Windows).
- Symbol: two linked screens, a flowing connection, and a cursor crossing between them.

## Assets

- `seamlet-icon.png`: original generated icon, with transparency.
- `Seamlet.icns`: Mac app icon, including Retina sizes.
- `Seamlet.ico`: Windows executable/window icon, multiple resolutions.
- `seamlet-logo.svg`: self-contained icon and typographic wordmark, transparent background.

The new icon was created with the built-in image generation tool. The SVG lockup
reuses that icon and adds editable type. `scripts/build-brand.sh` packages the icon
and recreates the logo from the original PNG; no API key is needed to rebuild.

Generation prompt:

> Use case: logo-brand. Create ONE polished production app icon for a new native Mac/Windows mouse and clipboard sharing app called Seamlet. No text or letters in the icon. 1024x1024 square image. Transparent outside a large softly rounded square tile inset about 8% from canvas edges, macOS app icon proportions. Tile is very dark blue-green ink #122C30 with subtle sophisticated satin shading. Central crisp minimalist symbol: two upright rounded rectangular screen outlines side by side, offset slightly vertically, joined by a single fluid S-shaped path suggesting a pointer moving seamlessly across them. Left screen outline warm ivory #F4F2EB, right screen outline mint teal #65D6BB. Strong thick strokes, clean geometric silhouette, readable at 32px. A small ivory cursor arrow at the center of the flowing connection may be included only if simple and uncluttered. Straight-on orthographic, no perspective, no desk, no shadows outside icon, no extra icons, no mockup, no watermark. Restrained premium utility identity, not neon, not generic blue gradient. This image will be the actual shipping app icon; isolated icon only.

The name was selected after a preliminary web search for exact-name software matches;
this is a design choice, not a claim of exclusive ownership or domain availability.

## Implementation

Both apps use native controls with explicit labels, clear focus behavior, and native
keyboard navigation. Warm paper backgrounds and grouped white panels separate pairing
from live status. The two-screen diagram reflects the configured side and connection.
Windows keeps scrolling available on shorter displays and uses DPI scaling.

The macOS bundle identifier, encrypted wire protocol identifiers, discovery service,
and temporary transfer-cache path remain compatible with earlier MultipleMouse builds.
The user-facing bundles are now `build/Seamlet.app` and `build/windows/Seamlet.exe`.
Run only one version on each computer.
