namespace DotCov.Tests.Infrastructure;

/// <summary>
/// A stand-in for the dotnet that <c>dotcov test</c> starts, named through
/// <c>DOTNET_HOST_PATH</c> (which the SDK sets for the tools it runs). It copies a prepared
/// Cobertura report into the <c>--results-directory</c> dotcov passes and exits 0, so the gate
/// after a successful test run is exercised in milliseconds instead of a real build.
/// </summary>
internal static class FakeDotnet
{
    public static string Write(TempWorkspace ws, string report)
    {
        if (OperatingSystem.IsWindows())
        {
            // CRLF: cmd.exe's label search misreads LF-only batch files.
            return ws.Write("fake-dotnet.cmd", $"""
                @echo off
                :next
                if "%~1"=="" exit /b 1
                if "%~1"=="--results-directory" (
                  copy /y "{report}" "%~2\coverage.cobertura.xml" >nul
                  exit /b 0
                )
                shift
                goto next
                """.ReplaceLineEndings("\r\n"));
        }

        var path = ws.Write("fake-dotnet", $"""
            #!/bin/sh
            while [ "$#" -gt 0 ]; do
              if [ "$1" = "--results-directory" ]; then
                cp "{report}" "$2/coverage.cobertura.xml"
                exit 0
              fi
              shift
            done
            exit 1
            """.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
