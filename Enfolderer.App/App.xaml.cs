using System.Configuration;
using System.Data;
using System.Windows;

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
}

