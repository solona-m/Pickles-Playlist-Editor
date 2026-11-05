using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;

// Writes its version and architecture to %TEMP%\PicklesArchProbe.txt; "update <folder>" applies the newest release there.
VelopackApp.Build().Run();
var mgr = new UpdateManager(new SimpleFileSource(new DirectoryInfo(args.Length > 1 ? args[1] : ".")));
string report = Path.Combine(Path.GetTempPath(), "PicklesArchProbe.txt");
File.WriteAllText(report, $"{mgr.CurrentVersion} {RuntimeInformation.ProcessArchitecture}");
if (args.Length > 1 && args[0] == "update")
{
    var update = mgr.CheckForUpdates();
    if (update == null) { File.AppendAllText(report, " no-update"); return 2; }
    mgr.DownloadUpdates(update);
    mgr.ApplyUpdatesAndExit(update);
}
return 0;
