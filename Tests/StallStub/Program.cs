using System;
using System.Threading;

// Stands in for ffmpeg so the watchdog can be checked end to end.
// STUB_MODE=stall keeps rewriting the status line with the same position, which
// is what a frozen encode looks like; STUB_MODE=advance moves on and exits, to
// prove the watchdog leaves a live run alone. See README.md.
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = Environment.GetEnvironmentVariable("STUB_MODE") ?? "stall";
        var err = Console.Error;

        void Status(int seconds)
        {
            int h = seconds / 3600, m = seconds / 60 % 60, s = seconds % 60;
            string at = h.ToString("00") + ":" + m.ToString("00") + ":" + s.ToString("00");
            // ffmpeg rewrites this line with a carriage return rather than ending it.
            err.Write("frame=  120 fps=24 q=28.0 size=1024KiB time=" + at
                      + ".00 bitrate=1000.0kbits/s" + (char)13);
            err.Flush();
        }

        if (mode == "advance")
        {
            for (int i = 1; i <= 20; i++) { Status(i); Thread.Sleep(500); }
            return 0;
        }

        // Get somewhere first, then freeze five seconds in -- forever.
        for (int i = 1; i <= 5; i++) { Status(i); Thread.Sleep(200); }
        while (true) { Status(5); Thread.Sleep(500); }
    }
}
