"""Build an offline optional Python wheel bundle for Windows x64 / Python 3.12."""
import argparse,hashlib,json,shutil,subprocess,sys,zipfile
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--output',type=Path,required=True);a=p.parse_args()
root=Path(__file__).resolve().parents[1];output=a.output.resolve();output.mkdir(parents=True,exist_ok=False)
wheelhouse=output/'wheels';wheelhouse.mkdir()
subprocess.run([sys.executable,'-m','pip','download','--only-binary=:all:','--require-hashes','-r',str(root/'python/requirements-stage3-win-py312.lock'),'--dest',str(wheelhouse)],check=True)
subprocess.run([sys.executable,'-m','pip','wheel','--no-deps','--no-build-isolation','--wheel-dir',str(wheelhouse),str(root/'python')],check=True)
lines=[];files=[]
for path in sorted(wheelhouse.glob('*.whl')):
    name,version=path.name.split('-')[:2];digest=hashlib.sha256(path.read_bytes()).hexdigest()
    lines.append(f'{name}=={version} --hash=sha256:{digest}');files.append(dict(file=path.name,sha256=digest))
(output/'requirements.lock').write_text('\n'.join(lines)+'\n',encoding='utf-8')
(output/'install.ps1').write_text(r"""param([string]$Python="python")
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    & $Python -c "import sys,platform; assert sys.version_info[:2]==(3,12) and platform.system()=='Windows' and platform.machine().lower() in ('amd64','x86_64')"
    if($LASTEXITCODE -ne 0){throw 'Requires Windows x64 Python 3.12'}
    & $Python -m venv .venv
    if($LASTEXITCODE -ne 0){throw 'venv failed'}
    & .\.venv\Scripts\python.exe -m pip install --no-index --find-links wheels --require-hashes -r requirements.lock
    if($LASTEXITCODE -ne 0){throw 'Offline install failed'}
} finally {Pop-Location}
""",encoding='utf-8')
shutil.copyfile(root.parent/'LICENSE',output/'LICENSE')
manifest=dict(python='3.12',platform='win-x64',commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=root).decode().strip(),dirty=bool(subprocess.check_output(['git','status','--porcelain'],cwd=root)),wheels=files)
(output/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
(output/'README.txt').write_text('Requires an existing Windows x64 Python 3.12 installation. Run install.ps1 offline. Start .venv/Scripts/multinodedaq-analysis.exe with --units and --port; supply MULTINODEDAQ_IPC_TOKEN through the environment. This optional bundle does not contain or start the C# recorder. Dependency licenses are included in wheel metadata.\n',encoding='utf-8')
with zipfile.ZipFile(str(output)+'.zip','x',compression=zipfile.ZIP_DEFLATED) as z:
    for path in output.rglob('*'):
        if path.is_file():z.write(path,path.relative_to(output.parent))
print(str(output)+'.zip')
