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
        behavior = repository / "plugins" / "providers" / "FgoPet.Portrait.Live2D" / "Runtime" / "desktop-behavior.mjs"
        shutil.copy2(behavior, work / "desktop-behavior.mjs")
        shutil.copy2(behavior.with_name("desktop-state.mjs"), work / "desktop-state.mjs")
        shutil.copy2(behavior.with_name("desktop-speech.mjs"), work / "desktop-speech.mjs")
        model.write_text("""import { desktopBehavior, eyeOriginFromModel, projectEyeOrigin, applyGaze, restoreMotionBase, HeadHover, HeadHitRegion, applyHoverBlink, textureAlphaFromImage } from './desktop-behavior.mjs';
import { ICubismUpdater, CubismUpdateOrder } from '@framework/motion/icubismupdater';
import { desktopState } from './desktop-state.mjs';
import { desktopSpeech } from './desktop-speech.mjs';
""" + model.read_text(encoding="utf-8"), encoding="utf-8")
        _replace_exact(model, "export class LAppModel extends CubismUserModel {", """export class LAppModel extends CubismUserModel {
  private _desktopEyeOrigin: any = null;
  private _desktopEyeViewport = { x: 0.5, y: 0.25 };
  private _desktopGazeWeight = 1;
  private _desktopHover = new HeadHover();
  private _desktopHeadRegion: any = null;
  private _desktopViewMatrix: any = null;
  private _desktopHoverFrame = { state: 'outside', triggered: false, tilt: 0, blink: 1 };
""")
        _replace_exact(model, "this.getRenderer().bindTexture(modelTextureNumber, textureInfo.id);", """this.getRenderer().bindTexture(modelTextureNumber, textureInfo.id);
          this._desktopHeadRegion.setTexture(modelTextureNumber, textureAlphaFromImage(textureInfo.img));""")
        _replace_exact(model, """const eyeBlinkUpdater = new CubismEyeBlinkUpdater(
          () => this._motionUpdated,
          this._eyeBlink
        );""", """const owner = this;
        const eyeBlinkUpdater = new class extends CubismEyeBlinkUpdater {
          onLateUpdate(model: any, dt: number): void {
            if (desktopState.blocksBlink) return;
            super.onLateUpdate(model, dt);
            if (!desktopState.blocksGaze && !owner._motionUpdated && owner._motionManager.isFinished()) {
              applyHoverBlink(model, owner._eyeBlink.getParameterIds(), owner._desktopHoverFrame.blink);
            }
          }
        }(() => this._motionUpdated, this._eyeBlink);""")
        _replace_exact(model, "this._updateScheduler.addUpdatableList(lookUpdater);", """// Replace sample drag tracking; only the app-owned desktop behavior writes gaze.
      this._desktopEyeOrigin = eyeOriginFromModel(this._model.getModel());
      this._desktopHeadRegion = new HeadHitRegion(this._model.getModel());
      const owner = this;
      // Decide the reaction before the single blink updater; expressions still run after blink.
      this._updateScheduler.addUpdatableList(new class extends ICubismUpdater {
        constructor() { super(CubismUpdateOrder.CubismUpdateOrder_EyeBlink - 1); }
        onLateUpdate(model: any, dt: number): void {
          const now = performance.now() / 1000;
          const inside = now - desktopBehavior.receivedAt <= 0.5
            && owner._desktopHeadRegion.isHit(desktopBehavior.pointer, owner._desktopViewMatrix);
          owner._desktopHoverFrame = owner._desktopHover.update(inside,
            (desktopBehavior.pointer?.x ?? 0.5) - owner._desktopEyeViewport.x,
            desktopState.blocksGaze || !owner._motionManager.isFinished(), dt, now);
          if (owner._desktopHoverFrame.triggered) desktopBehavior.interact(now);
        }
      }());
      this._updateScheduler.addUpdatableList(new class extends ICubismUpdater {
        constructor() { super(CubismUpdateOrder.CubismUpdateOrder_Drag); }
        onLateUpdate(model: any, dt: number): void {
          if (!owner._desktopEyeOrigin) return;
          const gaze = desktopBehavior.update(dt, performance.now() / 1000, owner._desktopEyeViewport);
          owner._desktopGazeWeight = !desktopState.blocksGaze && owner._motionManager.isFinished()
            ? Math.min(1, owner._desktopGazeWeight + dt / 0.6) : 0;
          applyGaze(model, id => CubismFramework.getIdManager().getId(id), gaze, owner._desktopGazeWeight);
          if (model.getModel().parameters.ids.includes('ParamAngleZ')) {
            model.addParameterValueById(CubismFramework.getIdManager().getId('ParamAngleZ'),
              owner._desktopHoverFrame.tilt * owner._desktopGazeWeight);
          }
        }
      }());
      this._updateScheduler.addUpdatableList(new class extends ICubismUpdater {
        constructor() { super(CubismUpdateOrder.CubismUpdateOrder_Expression + 1); }
        onLateUpdate(model: any, dt: number): void {
          desktopState.applyEffects(model, id => CubismFramework.getIdManager().getId(id), owner._motionManager.isFinished());
        }
      }());
      this._updateScheduler.addUpdatableList(new class extends ICubismUpdater {
        constructor() { super(CubismUpdateOrder.CubismUpdateOrder_LipSync + 1); }
        onLateUpdate(model: any, dt: number): void {
          desktopSpeech.apply(model, id => CubismFramework.getIdManager().getId(id),
            desktopSpeech.update(dt, performance.now() / 1000));
        }
      }());""")
        # Sample breath also swings head/body strongly. Retain breathing with quiet offsets.
        _replace_exact(model, "this._idParamAngleX, 0.0, 15.0, 6.5345, 0.5", "this._idParamAngleX, 0.0, 2.0, 6.5345, 0.5")
        _replace_exact(model, "this._idParamAngleY, 0.0, 8.0, 3.5345, 0.5", "this._idParamAngleY, 0.0, 1.5, 3.5345, 0.5")
        _replace_exact(model, "this._idParamAngleZ, 0.0, 10.0, 5.5345, 0.5", "this._idParamAngleZ, 0.0, 2.0, 5.5345, 0.5")
        _replace_exact(model, "          4.0,\n          15.5345,", "          1.0,\n          15.5345,")
        _replace_exact(model, "matrix.multiplyByMatrix(this._modelMatrix);", """matrix.multiplyByMatrix(this._modelMatrix);
      if (this._desktopEyeOrigin) this._desktopEyeViewport = projectEyeOrigin(this._desktopEyeOrigin, matrix);""")
        _replace_exact(model, "this.getRenderer().setMvpMatrix(matrix);", """this._desktopViewMatrix = matrix;
      this.getRenderer().setMvpMatrix(matrix);""")
        _replace_exact(model, """      this.startRandomMotion(
        LAppDefine.MotionGroupIdle,
        LAppDefine.PriorityIdle
      );""", """      // Procedural idle owns attention; release the final motion pose smoothly.
      if (!desktopState.applyBase(this._model)) restoreMotionBase(this._model, deltaTimeSeconds);""")
        _replace_exact(model, "this._model.loadParameters();", """desktopState.speech(desktopSpeech.active(performance.now() / 1000), performance.now() / 1000);
    desktopState.sync(this, performance.now() / 1000);
    this._model.loadParameters();""")
        _replace_exact(model, "this._model.saveParameters(); // 状態を保存", """if (this._motionUpdated) desktopState.capture(this._model);
    this._model.saveParameters(); // 状態を保存""")
        _replace_exact(model, "if (this._state != LoadStep.CompleteSetup) return;", """if (this._state != LoadStep.CompleteSetup) return;
    if (!(window as any).__fgoPetReady) {
      (window as any).__fgoPetReady = true;
      (window as any).chrome?.webview?.postMessage({ type: 'live2d.ready' });
    }""")
        manager = work / "lapplive2dmanager.ts"
        _replace_exact(manager, "    model.update();", """    // Match the padded 531x675 host while retaining the original 483x603
    // character scale. All extra vertical room belongs above the character.
    projection.scaleRelative(483 / 531, 483 / 531);
    projection.translateRelative(0, -72 / 675);
    model.update();""")
        _replace_exact(manager, "this._sceneIndex = 0;", """this._sceneIndex = 0;
    (window as any).__fgoPetPlayTap = () => {
      const model = this._models[0];
      if (!model) return;
      const play = desktopState.tap(performance.now() / 1000);
      desktopState.sync(model, performance.now() / 1000);
      if (play) model.startRandomMotion(LAppDefine.MotionGroupTapBody, LAppDefine.PriorityNormal);
    };
""")
        manager.write_text("import { desktopState } from './desktop-state.mjs';\n" + manager.read_text(encoding="utf-8"), encoding="utf-8")
        main = work / "main.ts"
        main.write_text(main.read_text(encoding="utf-8") + """
import { desktopBehavior } from './desktop-behavior.mjs';
import { desktopState } from './desktop-state.mjs';
import { desktopSpeech } from './desktop-speech.mjs';
(window as any).chrome?.webview?.addEventListener('message', (event: any) => {
  const now = performance.now() / 1000;
  if (event.data?.type === 'live2d.tap') {
    desktopBehavior.interact(now);
    (window as any).__fgoPetPlayTap?.();
  } else if (!desktopSpeech.receive(event.data, now) && !desktopState.receive(event.data, now)) {
    if (desktopBehavior.receive(event.data, now) && event.data.active) desktopState.activity(now);
  }
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
        (runtime / "runtime-info.json").write_text(json.dumps({"sdk": "CubismSdkForWeb-5-r.5", "source": "official TypeScript Demo adapter", "model_data_in_role_pack": True, "desktop_behavior": 3, "head_hover": 1, "desktop_states": 1, "speech_lip_sync": 1}, indent=2) + "\n", encoding="utf-8")
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
