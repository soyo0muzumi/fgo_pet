"""Exercise the real app's MSBuild resource contract without compiling the app."""

import json
import os
import shutil
import subprocess
from pathlib import Path

import pytest


REPOSITORY = Path(__file__).resolve().parents[2]
APP = REPOSITORY / "src/FgoPet.App"
RUNTIME_FILES = (
    "index.html", "app.js", "live2dcubismcore.js",
    "CORE-LICENSE.md", "FRAMEWORK-LICENSE.md", "runtime-info.json",
    *("Framework/Shaders/WebGL/" + name for name in (
        "vertshadersrc.vert", "vertshadersrcblend.vert", "vertshadersrccopy.vert",
        "vertshadersrcmasked.vert", "vertshadersrcsetupmask.vert",
        "fragshadersrcalphablend.frag", "fragshadersrccolorblend.frag",
        "fragshadersrccopy.frag", "fragshadersrcmaskinvertedpremultipliedalpha.frag",
        "fragshadersrcmaskpremultipliedalpha.frag", "fragshadersrcpremultipliedalpha.frag",
        "fragshadersrcpremultipliedalphablend.frag", "fragshadersrcsetupmask.frag",
    )),
)


@pytest.fixture
def project(tmp_path):
    app = tmp_path / "src/FgoPet.App"
    app.mkdir(parents=True)
    shutil.copy2(APP / "FgoPet.App.csproj", app)
    if (APP / "Live2D.targets").exists():
        shutil.copy2(APP / "Live2D.targets", app)
    return app / "FgoPet.App.csproj"


def runtime(root):
    for relative in RUNTIME_FILES:
        file = root / relative
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text("fixture", encoding="utf-8")
    return root


def evaluate(project, *properties, environment=None, target="PrepareForBuild"):
    env = dict(os.environ)
    for name in ("FGOPET_LIVE2D_RUNTIME", "FgoPetLive2DRuntime", "FgoPetRequireLive2D"):
        env.pop(name, None)
    env.update(environment or {})
    env["DOTNET_CLI_UI_LANGUAGE"] = "en"
    return subprocess.run(
        ["dotnet", "msbuild", str(project), "-nologo", f"-t:{target}",
         "-getProperty:FgoPetLive2DRuntime", "-getItem:Content", *properties],
        capture_output=True, text=True, encoding="utf-8", env=env, timeout=45,
    )


def test_plain_build_includes_conventional_runtime_for_build_and_publish(project):
    runtime(project.parents[2] / "artifacts/live2d-runtime")
    result = evaluate(project)
    assert result.returncode == 0, result.stdout + result.stderr
    content = json.loads(result.stdout)["Items"]["Content"]
    links = {item["Link"].replace("\\", "/") for item in content}
    assert links == {"Live2D/" + name for name in RUNTIME_FILES}
    assert all(item["CopyToOutputDirectory"] == "PreserveNewest" for item in content)
    assert all(item["CopyToPublishDirectory"] == "PreserveNewest" for item in content)


def test_missing_default_runtime_stops_before_build(project):
    result = evaluate(project)
    assert result.returncode != 0
    assert "FGOPET_LIVE2D_MISSING" in result.stdout + result.stderr


@pytest.mark.parametrize("missing", ["app.js", "CORE-LICENSE.md", "Framework/Shaders/WebGL/vertshadersrc.vert"])
def test_incomplete_explicit_runtime_never_falls_back(project, missing):
    runtime(project.parents[2] / "artifacts/live2d-runtime")
    selected = runtime(project.parents[2] / "explicit runtime")
    (selected / missing).unlink()
    result = evaluate(project, f"-p:FgoPetLive2DRuntime={selected}")
    assert result.returncode != 0
    assert "FGOPET_LIVE2D_MISSING" in result.stdout + result.stderr


def test_environment_runtime_and_explicit_override(project):
    selected = runtime(project.parents[2] / "from environment")
    result = evaluate(project, environment={"FGOPET_LIVE2D_RUNTIME": str(selected)})
    assert result.returncode == 0, result.stdout + result.stderr
    assert Path(json.loads(result.stdout)["Properties"]["FgoPetLive2DRuntime"]) == selected
    explicit = runtime(project.parents[2] / "explicit")
    result = evaluate(project, f"-p:FgoPetLive2DRuntime={explicit}",
                      environment={"FGOPET_LIVE2D_RUNTIME": str(selected / "missing")})
    assert result.returncode == 0, result.stdout + result.stderr
    assert Path(json.loads(result.stdout)["Properties"]["FgoPetLive2DRuntime"]) == explicit


def test_static_test_build_is_explicit_but_publish_still_requires_runtime(project):
    result = evaluate(project, "-p:FgoPetRequireLive2D=false")
    assert result.returncode == 0, result.stdout + result.stderr
    result = evaluate(project, "-p:FgoPetRequireLive2D=false", target="PrepareForPublish")
    assert result.returncode != 0
    assert "FGOPET_LIVE2D_MISSING" in result.stdout + result.stderr


def test_static_test_opt_out_does_not_hide_an_invalid_explicit_path(project):
    result = evaluate(project, "-p:FgoPetRequireLive2D=false",
                      f"-p:FgoPetLive2DRuntime={project.parent / 'missing'}")
    assert result.returncode != 0
    assert "FGOPET_LIVE2D_MISSING" in result.stdout + result.stderr


def test_incremental_build_and_publish_keep_all_runtime_files(project):
    # Use the production targets in a tiny package-free project so the actual
    # SDK copy/IncrementalClean/publish pipeline runs without compiling WPF.
    project.write_text('''<Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
      <Import Project="Live2D.targets" />
    </Project>''', encoding="utf-8")
    source = runtime(project.parents[2] / "artifacts/live2d-runtime")
    env = dict(os.environ)
    for name in ("FGOPET_LIVE2D_RUNTIME", "FgoPetLive2DRuntime", "FgoPetRequireLive2D"):
        env.pop(name, None)
    env["DOTNET_CLI_UI_LANGUAGE"] = "en"

    def dotnet(*arguments):
        result = subprocess.run(
            ["dotnet", *arguments, str(project), "-c", "Release", "-nologo", "-v:q"],
            capture_output=True, text=True, encoding="utf-8", env=env, timeout=90,
        )
        assert result.returncode == 0, result.stdout + result.stderr

    def verify(directory):
        assert {p.relative_to(directory).as_posix() for p in directory.rglob("*") if p.is_file()} == set(RUNTIME_FILES)
        for relative in RUNTIME_FILES:
            assert (directory / relative).read_bytes() == (source / relative).read_bytes()

    output = project.parent / "bin/Release/net8.0/Live2D"
    dotnet("build")
    verify(output)
    (output / "app.js").unlink()
    dotnet("build", "--no-restore")
    verify(output)
    publish = project.parent / "publish"
    dotnet("publish", "--no-build", "--no-restore", "-o", str(publish))
    verify(publish / "Live2D")
