using System;
using System.IO;

namespace NeteaseToolbox
{
    internal static class NeteasePlaybackSyncValidation
    {
        private static int Main()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NeteaseToolbox\Playback\play-history.csv");
            bool failed = false;
            var recorder = new PlaybackRecorder(path, message => {
                if (message.StartsWith("失败：", StringComparison.Ordinal)) failed = true;
                Console.WriteLine(message);
            });
            try
            {
                recorder.StartAsync().Wait();
                if (failed) { Console.WriteLine("SYNC_FAILED"); return 1; }
                Console.WriteLine("SYNC_OK");
                Console.WriteLine("RECORD_FILE=" + path);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("SYNC_FAILED=" + ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message);
                Console.WriteLine(ex.GetBaseException().StackTrace);
                return 1;
            }
            finally { recorder.Dispose(); }
        }
    }
}
