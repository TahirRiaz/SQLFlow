@echo off
setlocal enabledelayedexpansion

REM ============================================================================
REM  SQLFlow V3 - prod-v2 container deploy (fast path)
REM
REM  Builds images in ACR from a git-archive of tracked files (tiny context),
REM  points the container apps at the new tag, waits for the new control-plane
REM  revision, and prints its startup log. The image tag is the current commit's
REM  short SHA (what the apps run). KEEP THIS FILE AT THE REPO ROOT.
REM
REM  Fast path (why not plain `az acr build .`):
REM    * `az acr build .` uploads the whole working tree and ignores .dockerignore
REM      on the client, shipping gigabytes of bin/obj/target/node_modules every
REM      build. The git-archive context is tracked files only (tens of MB).
REM    * Each build runs FROM INSIDE its context with `--file <name> .`: az resolves
REM      --file against the current directory, not the context path, so building
REM      from the repo root with `--file Dockerfile` would grab the wrong Dockerfile.
REM    * Each build is verified by its ACR run id polled to a terminal state. The az
REM      CLI can crash on a glyph AFTER the server build succeeds, so the local exit
REM      code is not trusted; the run id (printed before any crash) is.
REM
REM  Builds run sequentially here (reliable in cmd); the PowerShell version
REM  (deploy-prod.ps1) builds them in parallel. Do NOT run either while a manual
REM  deploy is in flight.
REM
REM  Usage:
REM    deploy-prod.bat                        control-plane, worker, gui
REM    deploy-prod.bat control-plane worker   only those apps
REM  Known apps: control-plane worker gui mcp slack-bot
REM ============================================================================

cd /d "%~dp0"

set "RG=datawarehouse-west-rg-prod-v2"
set "ACR=sqlflowv3acrprod"
set "SUB=83731164-2cea-4291-b78d-7e2e69eea8a6"
set "PYTHONUTF8=1"
set "PYTHONIOENCODING=utf-8"

set "APPS=%*"
if "%APPS%"=="" set "APPS=control-plane worker gui"
REM 'all' deploys every app, mcp and slack-bot included.
if /i "%APPS%"=="all" set "APPS=control-plane worker gui mcp slack-bot"

set "TAG="
for /f "delims=" %%i in ('git rev-parse --short HEAD') do set "TAG=%%i"
if "%TAG%"=="" ( echo ERROR: could not read git HEAD. Run from the SQLFlow V3 repo. & exit /b 1 )

for /f "delims=" %%c in ('git status --porcelain -- src gui') do (
  echo WARNING: uncommitted changes in src/ or gui/ - image %TAG% is built from the committed tree and will NOT include them.
  goto :dirty_done
)
:dirty_done

echo.
echo === SQLFlow V3 deploy ===
echo    tag:  %TAG%
echo    apps: %APPS%
echo    rg:   %RG%
echo.

call az account set --subscription "%SUB%"
if errorlevel 1 ( echo ERROR: az account set failed. & exit /b 1 )

REM --- Clean, minimal contexts from tracked files only -------------------------
set "CTX=%TEMP%\sqlflow-deploy-ctx"
if exist "%CTX%" rmdir /s /q "%CTX%"
mkdir "%CTX%\repo" >nul 2>&1
mkdir "%CTX%\gui"  >nul 2>&1

echo Preparing clean build context (tracked files only)...
git archive --format=tar.gz -o "%CTX%\repo.tar.gz" HEAD
if errorlevel 1 ( echo ERROR: git archive repo failed. & exit /b 1 )
tar -xzf "%CTX%\repo.tar.gz" -C "%CTX%\repo"
git archive --format=tar.gz -o "%CTX%\gui.tar.gz" HEAD:gui
if errorlevel 1 ( echo ERROR: git archive gui failed. & exit /b 1 )
tar -xzf "%CTX%\gui.tar.gz" -C "%CTX%\gui"

REM --- Build + verify each app (sequential; server-side run id is the truth) ----
set "FAILED="
for %%a in (%APPS%) do call :buildverify %%a
if defined FAILED (
  echo.
  echo ERROR: one or more builds did not succeed - not deploying. See above.
  exit /b 1
)

REM --- Deploy ------------------------------------------------------------------
echo.
echo === Deploying container apps ===
for %%a in (%APPS%) do call :deploy %%a

REM --- Wait for control-plane, then show its startup log -----------------------
echo %APPS% | findstr /i "control-plane" >nul && (
  call :waitrunning sqlflow-v3-control-plane
  call :showlog sqlflow-v3-control-plane
)

echo.
echo === Done. Deployed tag %TAG% to: %APPS% ===
exit /b 0

REM ============================================================================
REM  Subroutines
REM ============================================================================

:buildverify
set "APP=%~1"
call :cfg %APP%
if not defined DF exit /b 0
set "LOG=%CTX%\%APP%.log"
echo.
echo --- Building sqlflow-v3-%APP%:%TAG%   (%DF%, %SUBDIR% context) ---
pushd "%CTX%\%SUBDIR%"
call az acr build --registry %ACR% --image sqlflow-v3-%APP%:%TAG% --file %DF% . > "%LOG%" 2>&1
popd
REM Pull the ACR run id from the log ("Queued a build with ID: <id>"), robust to the CLI crash.
set "RUNID="
for /f "usebackq tokens=*" %%L in (`findstr /c:"Queued a build with ID:" "%LOG%"`) do set "LINE=%%L"
if defined LINE (
  set "RUNID=!LINE:*ID: =!"
  for /f "tokens=1" %%x in ("!RUNID!") do set "RUNID=%%x"
)
if not defined RUNID (
  echo    %APP%: build never queued.
  type "%LOG%"
  set "FAILED=1"
  exit /b 0
)
echo    %APP%: run !RUNID!, waiting for result...
call :waitrun !RUNID! %APP%
exit /b 0

:waitrun
set "RID=%~1"
set "WAPP=%~2"
set /a _n=0
:wr_loop
set "RST="
for /f "delims=" %%s in ('az acr task show-run -r %ACR% --run-id %RID% --query status -o tsv 2^>nul') do set "RST=%%s"
if /i "%RST%"=="Succeeded" ( echo    %WAPP%: Succeeded & exit /b 0 )
if /i "%RST%"=="Failed"    ( echo    %WAPP%: FAILED   & set "FAILED=1" & exit /b 0 )
if /i "%RST%"=="Canceled"  ( echo    %WAPP%: CANCELED & set "FAILED=1" & exit /b 0 )
if /i "%RST%"=="Error"     ( echo    %WAPP%: ERROR    & set "FAILED=1" & exit /b 0 )
if /i "%RST%"=="Timeout"   ( echo    %WAPP%: TIMEOUT  & set "FAILED=1" & exit /b 0 )
set /a _n+=1
if %_n% geq 120 ( echo    %WAPP%: gave up waiting for run %RID% & set "FAILED=1" & exit /b 0 )
timeout /t 10 /nobreak >nul
goto wr_loop

:deploy
set "APP=%~1"
echo --- sqlflow-v3-%APP% -^> :%TAG% ---
call az containerapp update -n sqlflow-v3-%APP% -g %RG% --image %ACR%.azurecr.io/sqlflow-v3-%APP%:%TAG% --query "properties.template.containers[0].image" -o tsv
if errorlevel 1 ( echo ERROR: deploy of sqlflow-v3-%APP% failed. & exit /b 1 )
exit /b 0

:waitrunning
set "APP=%~1"
echo.
echo === Waiting for %APP% to run on %TAG% ===
set /a _m=0
:wrun_loop
set "IMG="
for /f "delims=" %%s in ('az containerapp revision list -n %APP% -g %RG% --query "[?properties.active] ^| [0].properties.template.containers[0].image" -o tsv 2^>nul') do set "IMG=%%s"
echo !IMG! | findstr /c:"%TAG%" >nul && ( echo    %APP%: Running on %TAG% & exit /b 0 )
set /a _m+=1
if %_m% geq 90 ( echo WARNING: %APP% not on %TAG% yet; check the portal. & exit /b 0 )
timeout /t 10 /nobreak >nul
goto wrun_loop

:showlog
set "APP=%~1"
echo.
echo === %APP% startup log (bootstrap / sync / warnings / errors) ===
call az containerapp logs show -n %APP% -g %RG% --tail 300 --type console 2>&1 | findstr /i "Bootstrap Synced warn error exception"
exit /b 0

:cfg
set "DF="
set "SUBDIR="
if /i "%~1"=="control-plane" ( set "DF=Dockerfile"          & set "SUBDIR=repo" )
if /i "%~1"=="worker"        ( set "DF=Dockerfile.worker"   & set "SUBDIR=repo" )
if /i "%~1"=="gui"           ( set "DF=Dockerfile"          & set "SUBDIR=gui"  )
if /i "%~1"=="mcp"           ( set "DF=Dockerfile.mcp"      & set "SUBDIR=repo" )
if /i "%~1"=="slack-bot"     ( set "DF=Dockerfile.slackbot" & set "SUBDIR=repo" )
if not defined DF ( echo ERROR: unknown app '%~1'. Known: control-plane worker gui mcp slack-bot & set "FAILED=1" )
exit /b 0
