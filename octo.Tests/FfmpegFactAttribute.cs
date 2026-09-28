using System.Diagnostics;

namespace Octo.Tests;

/// <summary>A test that needs ffmpeg on the PATH, and skips itself cleanly on a machine without it.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -version")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (process is null) return false;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch { return false; }
    });

    public FfmpegFactAttribute()
    {
        if (!Available.Value) Skip = "ffmpeg is not on the PATH";
    }

    internal static bool IsAvailable => Available.Value;
}
