# Application icon

`AppIcon.svg` is the approved minimal Mash avatar v2, preserved byte-for-byte
from Library file `libfile_0a41b740bd2c8191909fbb603599c6de` (3236 bytes,
SHA-256 `366bbb0383e06667075ce297c2cb87d5df38d70498b3bcd0e9043b33e56b902b`).
It was drawn for this application; it contains no extracted game artwork.
The character remains associated with Fate/Grand Order; this source does not
grant a separate license to third-party character rights.

`AppIcon.ico` was exported from its matching 1024 × 1024 RGBA PNG preview,
Library file `libfile_3289761cc18c81918a7d52cbec4a466e`
(SHA-256 `15206f0e0e2786c75d6f780283b859066beeedd0ca6b4166527f10cadd6a6d7d`).
It contains 16, 24, 32, 48, 64, 128 and 256 pixel frames, each encoded as PNG
inside the ICO. The original composition and opaque background are preserved.
There are no notification badges or state variants.

Retrieve that preview from Library, or render the SVG with a trusted local SVG
renderer to a 1024 × 1024 PNG, then use the existing Pillow dependency:

```python
from PIL import Image

image = Image.open("mash-avatar-v2.png").convert("RGBA")
image.save("AppIcon.ico", format="ICO",
           sizes=[(size, size) for size in (16, 24, 32, 48, 64, 128, 256)])
```

The initial export used Pillow 12.0.0 with its ICO writer's LANCZOS resampling.
DesktopShell embeds the ICO as a WPF resource at
`/FgoPet.DesktopShell;component/Assets/AppIcon.ico`.
The App project's `ApplicationIcon` points to the same physical file for the
executable. The SVG is source-only; neither it nor the preview is copied to the
runtime output.
