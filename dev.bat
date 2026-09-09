@echo off
REM ============================================================================
REM  SQLFlow local dev. Run this instead of deploying to Azure to try something.
REM
REM      dev.bat          start the GUI and the control plane
REM      dev.bat api      control plane only (no GUI window)
REM
REM  What comes up:
REM      GUI            http://localhost:5173   (Vite, hot-reloads on save)
REM      Control plane  http://localhost:5000   (API + scheduler + worker, ONE process)
REM
REM  Everything RUNS here; every resource is the REAL Azure estate: the same catalog
REM  (dw-sqlflow-prod), the same pre/ods databases, the same lake. Sign in with your
REM  normal SQLFlow login, because it is literally the same user table.
REM
REM  Azure calls go through your 'az login' (SQLFLOW_AZURE_AUTH=cli), so ${keyvault:...}
REM  refs in flow YAML resolve with no extra config.
REM
REM  ---------------------------------------------------------------------------
REM  READ THIS ONCE: you share the run queue with Azure.
REM
REM  The catalog is shared, so this process's worker can CLAIM runs the cloud worker
REM  would have run, and its scheduler competes to fire schedules. The claim is
REM  compare-and-swap, so nothing double-fires or double-runs. But it does mean:
REM    - a run you trigger may execute here (good: breakpoints) or in Azure (race).
REM    - if you leave this running overnight, YOUR MACHINE may run the 04:00 nightly.
REM  Close the window when you are done. There is no scheduler off switch in config.
REM  Starting dev.bat again kills whatever the last run left behind, so you never end up
REM  with two workers on this machine racing each other for the same queue.
REM
REM  Flows read and WRITE the real cloud databases, exactly as the estate does.
REM  ---------------------------------------------------------------------------
REM
REM  Migrations: pending catalog migrations from THIS checkout are applied before startup (the
REM  deployed containers do the same for themselves). F5 alone does NOT migrate: run dev.bat once
REM  after pulling or authoring a migration, then debug.
REM
REM  Config: .sqlflow\env (git-ignored). Rebuild it with scripts\dev-setup.ps1.
REM  Debugger: F5 in VS Code -> "SQLFlow: Dev (F5)" does the same with breakpoints.
REM ============================================================================
setlocal

cd /d "%~dp0"

REM --- Stop whatever the last run left behind, before anything builds ---
REM A control plane from an earlier run keeps SqlFlow.ControlPlane.dll open, so the 'dotnet run'
REM below fails its build with "the file is locked by another process" and you end up debugging
REM the OLD binary. A surviving worker also keeps claiming runs off the shared queue behind your
REM back. So: kill the port owners, kill a control plane that crashed off its port but still holds
REM the bin\ lock (F5 sessions included), and kill the GUI window a previous dev.bat spawned.
call :stopport 5000 "control plane"
call :stopport 5173 "GUI"
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'dotnet.exe','SqlFlow.ControlPlane.exe' -and $_.CommandLine -like '*SqlFlow.ControlPlane*' } | ForEach-Object { Write-Host ('Stopping previous control plane (pid ' + $_.ProcessId + ') ...'); Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
taskkill /f /t /fi "WINDOWTITLE eq SQLFlow GUI*" >nul 2>&1

REM --- Azure login is what routes Key Vault and the lake to the cloud ---
REM 'call' is REQUIRED: az on Windows is az.cmd, and invoking a .cmd from a .bat without 'call'
REM transfers control and never comes back, so the rest of this script silently never runs.
REM Not logged in is not a dead end: az login opens the browser sign-in right here, so one file
REM carries a fresh machine (or an expired token) all the way to a running estate.
call az account show >nul 2>&1
if errorlevel 1 (
    echo Not logged in to Azure; opening the sign-in ...
    call az login
    call az account show >nul 2>&1
    if errorlevel 1 (
        echo [X] Azure sign-in did not complete. Run 'az login' and start dev.bat again.
        exit /b 1
    )
)

REM --- The env file is generated, so a missing one is generated, not reported ---
REM dev-setup.ps1 reads the live container app secrets through the az login above, which is why the
REM login step comes first. Regenerate by hand after a secret rotation (see the file's own header).
if not exist ".sqlflow\env" (
    echo .sqlflow\env is missing; generating it from the live estate ...
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dev-setup.ps1
    if not exist ".sqlflow\env" (
        echo [X] scripts\dev-setup.ps1 did not produce .sqlflow\env. Fix its output above and retry.
        exit /b 1
    )
)

REM --- Load .sqlflow\env into this process ---
REM The control plane does NOT read .sqlflow\env itself: that loader is for flow directories and the
REM engine, not the host. So push the values in as real environment variables.
REM 'delims==' splits on the FIRST '=' only, so connection strings (full of '=' and ';') survive; the
REM findstr drops comment lines and blank lines.
for /f "usebackq tokens=1,* delims==" %%a in (`findstr /v /r /c:"^#" ".sqlflow\env" ^| findstr /r /c:"="`) do set "%%a=%%b"

REM --- Apply pending catalog migrations, up front and visibly ---
REM The deployed containers migrate themselves at startup (Bootstrap:ApplyMigrations=true), but the
REM local HOST runs with ApplyMigrations=false so an F5 or a stray local run can never mutate the
REM shared catalog implicitly. Starting dev.bat is the explicit moment your checkout meets the shared
REM catalog, so its pending migrations are applied here through the CLI's guarded 'db migrate'
REM (upgrades an EXISTING catalog only; it refuses to provision anything, so a wrong connection
REM string can never create a stray database).
echo Applying pending catalog migrations ...
dotnet run --project src\SqlFlow.Cli -- db migrate
if errorlevel 1 (
    echo [!] Catalog migration failed. The control plane still starts, but any surface that needs the
    echo     new schema will error until:  dotnet run --project src\SqlFlow.Cli -- db migrate
)

if /i "%~1"=="api" goto :api

REM --- Node on PATH ---
REM Node is usually installed AFTER VS Code (or this terminal) was started, and a running process
REM never picks up a PATH change. So the terminal, and every window it spawns, has no npm until it
REM is restarted. Rather than demand a restart, find Node and add it for this script and its children.
where npm >nul 2>&1
if errorlevel 1 (
    if exist "%ProgramFiles%\nodejs\npm.cmd" set "PATH=%PATH%;%ProgramFiles%\nodejs"
)
where npm >nul 2>&1
if errorlevel 1 (
    if exist "%ProgramFiles(x86)%\nodejs\npm.cmd" set "PATH=%PATH%;%ProgramFiles(x86)%\nodejs"
)
where npm >nul 2>&1
if errorlevel 1 (
    if exist "%LOCALAPPDATA%\Programs\nodejs\npm.cmd" set "PATH=%PATH%;%LOCALAPPDATA%\Programs\nodejs"
)
where npm >nul 2>&1
if errorlevel 1 (
    echo [X] npm not found. Install Node ^(winget install OpenJS.NodeJS.LTS^), then open a NEW terminal.
    echo     Or run:  dev.bat api    to start the control plane without the GUI.
    exit /b 1
)

REM --- GUI deps: install on first run AND whenever the lock file moved on ---
REM npm writes node_modules\.package-lock.json on every install; when the repo's package-lock.json is
REM newer than that stamp (a pulled or newly added dependency), the tree is stale and Vite would fail
REM at the first missing import. xcopy /L /D lists files newer than the target without copying: one
REM line of output means the lock moved on, so reinstall.
set "_gui_install="
if not exist "gui\node_modules\.package-lock.json" set "_gui_install=1"
if not defined _gui_install (
    for /f %%n in ('xcopy /L /D /Y "gui\package-lock.json" "gui\node_modules\.package-lock.json" ^| findstr /r /c:"^1 "') do set "_gui_install=1"
)
if defined _gui_install (
    echo Installing GUI dependencies ...
    pushd gui
    call npm ci
    popd
)

echo Starting the GUI on http://localhost:5173 ...
start "SQLFlow GUI" cmd /k "cd /d %~dp0gui && npm run dev -- --open"

:api
echo.
echo   GUI:      http://localhost:5173     (your normal SQLFlow login)
echo   API:      http://localhost:5000
echo   Catalog:  the REAL Azure catalog. Your worker shares the estate's run queue.
echo   Ctrl+C    stops the control plane. The GUI has its own window.
echo.
REM The managed git-to-catalog sync is claim-based on the SHARED catalog: a local instance that ran it would
REM steal due syncs from the deployed control plane and execute them with this machine's filesystem,
REM credentials, and code version. Dev instances therefore never participate; use the deployed estate's
REM "sync now" (or the CLI's db sync against a local folder) instead.
set "ControlPlane__ManagedSync__Enabled=false"
dotnet run --project src\SqlFlow.ControlPlane --urls http://localhost:5000

endlocal
exit /b

REM ---------------------------------------------------------------------------
REM  :stopport <port> <what>   free a listening port by killing the process tree that owns it.
REM  netstat prints the owning pid in the last column; the trailing space in the pattern is what
REM  keeps ":5000 " from also matching ":50000".
REM  Do NOT add '-p tcp': that switch makes netstat print IPv4 ONLY, and Vite listens on
REM  [::1]:5173, so the GUI would never be found. Bare '-ano' lists v4 and v6; the LISTENING
REM  match drops the UDP rows on its own.
REM ---------------------------------------------------------------------------
:stopport
setlocal
set "_port=%~1"
set "_what=%~2"
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /r /c:":%_port% .*LISTENING"') do (
    echo Stopping previous %_what% ^(pid %%p^) on port %_port% ...
    taskkill /f /t /pid %%p >nul 2>&1
)
endlocal
goto :eof
