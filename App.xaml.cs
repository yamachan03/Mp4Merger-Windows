using System.Windows;
using Mp4Merger.Core;

namespace Mp4Merger
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // Clear out what a crash or a force quit left behind, without
            // touching a folder another running copy is still using.
            WorkDir.RemoveStale(includingOwn: false);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // ffmpeg is a separate process: without this an abandoned re-encode
            // keeps running and keeps writing into the temp folder.
            ChildProcesses.TerminateAll();
            WorkDir.RemoveStale(includingOwn: true);
            base.OnExit(e);
        }
    }
}
