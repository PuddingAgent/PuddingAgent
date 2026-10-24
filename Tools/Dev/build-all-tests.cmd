@echo off
rem Compile gate for every test project (build only, does not run tests).
rem Each Source\*Tests and Tests\*Tests is built in turn; only "error CS" counts as a failure.
rem MSB3021 / MSB1009 mean a file lock or a wrong path, not broken code.
rem Why: adding an interface member breaks every implementer, including test
rem doubles, which no single project's test run will reveal.
rem Usage: cmd /c Tools\Dev\build-all-tests.cmd   ->  temp\alltests.txt
rem NOTE: keep this file ASCII-only. cmd reads .cmd as the OEM codepage and
rem Chinese comments corrupt the parse (verified 2026-10-21).
setlocal enabledelayedexpansion
rem Truncate instead of "del": if a previous run left the file open, del silently
rem fails and this run appends to the old content (observed 2026-10-21).
echo RUN > temp\alltests.txt

for /d %%d in (Source\*Tests Tests\*Tests) do (
  set "proj="
  for %%f in ("%%d\*.csproj") do set "proj=%%f"
  if "!proj!"=="" (
    echo NOCSPROJ %%~nxd>> temp\alltests.txt
  ) else (
    dotnet build "!proj!" -v q --nologo > temp\one-build.log 2>&1
    findstr /C:"error CS" temp\one-build.log >nul
    if errorlevel 1 (
      echo OK %%~nxd>> temp\alltests.txt
    ) else (
      echo CSERROR %%~nxd>> temp\alltests.txt
    )
  )
)

echo DONE>> temp\alltests.txt
findstr /C:"CSERROR" temp\alltests.txt >nul
if errorlevel 1 (
  echo RESULT: all ok>> temp\alltests.txt
) else (
  echo RESULT: has CS errors>> temp\alltests.txt
)
