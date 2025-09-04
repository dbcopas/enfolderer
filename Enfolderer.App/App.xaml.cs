using System.Configuration;
using System.Data;
using System.Windows;
using Enfolderer.App.Tests;

namespace Enfolderer.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
	public App()
	{
		this.DispatcherUnhandledException += (s, e) =>
		{
			System.Diagnostics.Debug.WriteLine($"[Global] Dispatcher exception: {e.Exception}");
			MessageBox.Show(e.Exception.ToString(), "Unhandled UI Exception");
			e.Handled = true; // prevent silent crash
		};
			AppDomain.CurrentDomain.FirstChanceException += (s, args) =>
			{
				if (args.Exception is ArgumentException && Environment.GetEnvironmentVariable("ENFOLDERER_QTY_DEBUG") == "1")
				{
					System.Diagnostics.Debug.WriteLine("[FirstChance][ArgumentException] " + args.Exception.Message + "\n" + args.Exception.StackTrace);
				}
			};
		AppDomain.CurrentDomain.UnhandledException += (s, e) =>
		{
			var ex = e.ExceptionObject as Exception;
			System.Diagnostics.Debug.WriteLine($"[Global] Domain exception: {ex}");
		};
		TaskScheduler.UnobservedTaskException += (s, e) =>
		{
			System.Diagnostics.Debug.WriteLine($"[Global] Unobserved task exception: {e.Exception}");
			e.SetObserved();
		};
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		// Headless self-test mode: run tests then exit without showing UI.
		var args = Environment.GetCommandLineArgs();
		if (args.Any(a => string.Equals(a, "--selftests", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
		{
			int failures = SelfTests.RunAll();
			Console.WriteLine($"SelfTests completed failures={failures}");
			Environment.Exit(failures == 0 ? 0 : 1);
			return; // unreachable
		}
		base.OnStartup(e);
	}
}

