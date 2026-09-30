"""Build the approved local Cubism SDK sample into a transparent app-owned runtime.

The generated JavaScript is an ignored application build input, never role-pack code.
This local adapter is for renderer integration and still needs device acceptance.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import tempfile
from pathlib import Path


def _replace_exact(path: Path, before: str, after: str) -> None:
    text = path.read_text(encoding="utf-8")
    if before not in text:
        raise ValueError(f"SDK sample shape changed: {path.name}: {before[:45]}")
    path.write_text(text.replace(before, after, 1), encoding="utf-8")


def _replace_method(path: Path, marker: str, replacement: str) -> None:
    text = path.read_text(encoding="utf-8")
    start = text.find(marker)
    if start < 0:
        raise ValueError(f"SDK sample method changed: {path.name}")
    brace = text.find("{", start)
    depth = 0
    for index in range(brace, len(text)):
        depth += (text[index] == "{") - (text[index] == "}")
        if depth == 0:
            path.write_text(text[:start] + replacement + text[index + 1:], encoding="utf-8")
            return
    raise ValueError(f"Unterminated SDK sample method: {path.name}")


def prepare(sdk: Path, esbuild: Path, output: Path) -> Path:
    sdk, esbuild, output = (path.resolve() for path in (sdk, esbuild, output))
    repository = Path(__file__).resolve().parents[2]
    if output.is_relative_to(repository) and not output.is_relative_to(repository / "artifacts"):
        raise ValueError("Generated SDK runtime must stay under ignored artifacts/")
    if output.exists():
        raise ValueError(f"Output already exists: {output}")
    source = sdk / "Samples" / "TypeScript" / "Demo" / "src"
    framework = sdk / "Framework" / "src"
    core = sdk / "Core" / "live2dcubismcore.min.js"
    shaders = sdk / "Framework" / "Shaders" / "WebGL"
    if not all(path.exists() for path in (source, framework, core, shaders, esbuild)):
        raise ValueError("Cubism SDK, shaders, Core, or local esbuild is missing")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="live2d-build-", dir=output.parent) as temporary:
        staging = Path(temporary)
        work = staging / "sample"
        shutil.copytree(source, work)
        define = work / "lappdefine.ts"
        _replace_exact(define, "export const ResourcesPath = '../../Resources/';", "export const ResourcesPath = './Resources/';")
        _replace_exact(define, "export const ShaderPath = '../../Framework/Shaders/WebGL/';", "export const ShaderPath = './Framework/Shaders/WebGL/';")
        start = "export const ModelDir: string[] = ["
        content = define.read_text(encoding="utf-8")
        left = content.index(start)
        right = content.index("];", left) + 2
        define.write_text(content[:left] + "export const ModelDir: string[] = ['mash_casual'];" + content[right:], encoding="utf-8")
        view = work / "lappview.ts"
        _replace_method(view, "public initializeSprite(): void {", "public initializeSprite(): void { /* no sample background or gear */ }")
        _replace_exact(view, "this._gear.release();", "this._gear?.release();")
        _replace_exact(view, "this._back.release();", "this._back?.release();")
        _replace_exact(view, "if (this._gear.isHit(posX, posY)) {", "if (this._gear?.isHit(posX, posY)) {")
        subdelegate = work / "lappsubdelegate.ts"
        _replace_exact(subdelegate, "gl.clearColor(0.0, 0.0, 0.0, 1.0);", "gl.clearColor(0.0, 0.0, 0.0, 0.0);")
        _replace_exact(subdelegate, "this._view.render();", """this._view.render();
    const now = performance.now();
    if ((window as any).__fgoPetReady && now - ((window as any).__fgoPetMaskAt || 0) > 250) {
      (window as any).__fgoPetMaskAt = now;
      const width = gl.drawingBufferWidth;
      const height = gl.drawingBufferHeight;
      if (width > 0 && height > 0 && width * height <= 4000000) {
        const pixels = new Uint8Array(width * height * 4);
        gl.readPixels(0, 0, width, height, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
        const columns = 64;
        const rows = 96;
        const bits = new Uint8Array(Math.ceil(columns * rows / 8));
        for (let y = 0; y < rows; y++) {
          const sourceY = height - 1 - Math.min(height - 1, Math.floor((y + 0.5) * height / rows));
          for (let x = 0; x < columns; x++) {
            const sourceX = Math.min(width - 1, Math.floor((x + 0.5) * width / columns));
            if (pixels[(sourceY * width + sourceX) * 4 + 3] >= 32) {
              const index = y * columns + x;
              bits[index >> 3] |= 1 << (index & 7);
            }
          }
        }
        let data = '';
        for (const byte of bits) data += String.fromCharCode(byte);
        (window as any).chrome?.webview?.postMessage({ type: 'live2d.hitmask', width: columns, height: rows, data: btoa(data) });
      }
    }""")
        model = work / "lappmodel.ts"
        _replace_exact(model, "if (this._state != LoadStep.CompleteSetup) return;", """if (this._state != LoadStep.CompleteSetup) return;
    if (!(window as any).__fgoPetReady) {
      (window as any).__fgoPetReady = true;
      (window as any).chrome?.webview?.postMessage({ type: 'live2d.ready' });
    }""")
        manager = work / "lapplive2dmanager.ts"
        _replace_exact(manager, "this._sceneIndex = 0;", """this._sceneIndex = 0;
    (window as any).__fgoPetPlayTap = () => this._models[0]?.startRandomMotion(
      LAppDefine.MotionGroupTapBody, LAppDefine.PriorityNormal);
""")
        main = work / "main.ts"
        main.write_text(main.read_text(encoding="utf-8") + """
(window as any).chrome?.webview?.addEventListener('message', (event: any) => {
  if (event.data?.type === 'live2d.tap') (window as any).__fgoPetPlayTap?.();
});
""", encoding="utf-8")
        runtime = staging / "runtime"
        runtime.mkdir()
        subprocess.run([str(esbuild), str(work / "main.ts"), "--bundle", "--platform=browser", "--format=iife",
                        f"--alias:@framework={framework.as_posix()}", f"--outfile={(runtime / 'app.js').as_posix()}"],
                       check=True, capture_output=True, text=True)
        shutil.copy2(core, runtime / "live2dcubismcore.js")
        shutil.copytree(shaders, runtime / "Framework" / "Shaders" / "WebGL")
        shutil.copy2(sdk / "Core" / "LICENSE.md", runtime / "CORE-LICENSE.md")
        shutil.copy2(sdk / "Framework" / "LICENSE.md", runtime / "FRAMEWORK-LICENSE.md")
        (runtime / "index.html").write_text("""<!doctype html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent}
canvas{width:100vw;height:100vh;display:block}</style>
<script src="./live2dcubismcore.js"></script><script src="./app.js" defer></script>
</head><body></body></html>\n""", encoding="utf-8")
        (runtime / "runtime-info.json").write_text(json.dumps({"sdk": "CubismSdkForWeb-5-r.5", "source": "official TypeScript Demo adapter", "model_data_in_role_pack": True}, indent=2) + "\n", encoding="utf-8")
        runtime.rename(output)
    return output


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk", required=True, type=Path)
    parser.add_argument("--esbuild", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    print(prepare(args.sdk, args.esbuild, args.output))


if __name__ == "__main__":
    main()
