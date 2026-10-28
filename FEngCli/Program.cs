using CommandLine;

namespace FEngCli;

internal static class Program
{
	private static int Main(string[] args)
	{
        if (FEngLib.Assets.BinaryRuntime.TryRunWorker(args, out var exitCode)) return exitCode;
        FEngLib.Assets.BinaryRuntime.WorkerHostPath = typeof(Program).Assembly.Location;
		//NFSCO Specific changes
		// FRONTEND\\FECUSTOMIZEPARTS
		// GLOBAL\\WORLDMAPMAIN
		// FRONTEND\\FECHALLENGESERIES
		// GLOBAL\\SCREENPRINTF

		//var path = "D:\\Development\\nfsco\\files\\data";
		//var folder = "FRONTEND";
		//var file = "FECUSTOMIZEPARTS";

		//var @string = $"compile -i {path}\\{folder}\\_FNG\\{file}.json -o {path}\\{folder}\\_FNG\\{file}.BIN";

		//args = @string.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);

		return Parser.Default
			.ParseArguments(args, typeof(DecompileCommand), typeof(CompileCommand),
				typeof(ScanContainerCommand), typeof(ExtractContainerCommand),
				typeof(WriteBackRegisterCommand), typeof(WriteBackRecordCommand),
				typeof(WriteBackPlanCommand), typeof(WriteBackExecuteCommand))
			.MapResult((BaseCommand bc) => bc.Execute(), errs => 1);
	}
}
