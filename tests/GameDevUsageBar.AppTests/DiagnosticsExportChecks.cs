using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GameDevUsageBar.App;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;
using GameDevUsageBar.Providers;

// Opens only the diagnostic preview against isolated synthetic homes. The Save
// button is inspected, never clicked: no native file dialog or provider query.
internal static class DiagnosticsExportChecks
{
    public static async Task Run(string root)
    {
        const string accountCanary="nonfunctional-private-account-canary";
        const string secretCanary="nonfunctional-exception-secret-canary";
        foreach(var logMode in new[]{"none","healthy","blocked"})
        {
            var fixture=Path.Combine(root,"diagnostics-preview",Guid.NewGuid().ToString("N"));
            var withLogs=logMode!="none";
            var blocked=logMode=="blocked";
            var loggingRoot=fixture;
            if(blocked){Directory.CreateDirectory(fixture);loggingRoot=Path.Combine(fixture,"blocked-log-root");File.WriteAllText(loggingRoot,"occupied");}
            using var runtime=withLogs?new RuntimeDiagnostics(loggingRoot,"0.9.2"):null;
            if(runtime is not null)
            {
                Check(runtime.BeginSession()!=blocked,"fixture runtime logging state is incorrect");
                runtime.RecordException("handled_exception",new InvalidOperationException(secretCanary));
            }
            await using var host=new ApplicationHost(fixture,ProviderCatalog.Create(),new NativeOAuthStore(Path.Combine(fixture,"native-home"))){RuntimeLog=runtime};
            await host.InitializeAsync();
            for(var index=0;index<host.Accounts.Count;index++)host.Accounts[index]=host.Accounts[index] with {Label=accountCanary};
            using var preferences=new PresentationPreferencesService(new PresentationStore(fixture),new(Widget:new(Visible:false)));
            using var hub=new ProviderStateHub(host.Adapters,host.Coordinator,preferences,Application.Current.Dispatcher);
            var overview=new MainWindow(host,hub,preferences){ShowActivated=false,ShowInTaskbar=false};
            try
            {
                overview.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                foreach(var language in new[]{"en-US","zh-CN"})
                {
                    Localizer.SetLanguage(language);
                    if(blocked)
                    {
                        const string warning="Runtime logs could not be saved. Export diagnostics to see the logging failure; usage queries remain available.";
                        Check(((TextBlock)overview.FindName("LayoutNotice")).Text.Contains(Localizer.T(warning),StringComparison.Ordinal),"logging failure warning is missing or untranslated");
                    }
                    Exception? failure=null;
                    var knownWindows=Application.Current.Windows.OfType<Window>().ToHashSet();
                    var checkedPreview=false;
                    _=overview.Dispatcher.BeginInvoke(new Action(()=>
                    {
                        var preview=Application.Current.Windows.OfType<Window>().FirstOrDefault(window=>!knownWindows.Contains(window));
                        try
                        {
                            Check(preview is not null,"diagnostic preview was not opened");
                            Check(ReferenceEquals(preview!.Owner,overview),"diagnostic preview lost its owner");
                            Check(preview.Title==Localizer.T("GameDevUsageBar · Diagnostic preview"),"preview title is untranslated");
                            var json=Visuals<TextBox>(preview).Single();
                            Check(json.IsReadOnly,"preview JSON is editable");
                            using var doc=JsonDocument.Parse(json.Text);
                            Check(doc.RootElement.GetProperty("app").GetString()=="GameDevUsageBar","preview is not the diagnostic summary");
                            if(blocked)
                            {
                                var logging=doc.RootElement.GetProperty("runtime");
                                Check(!logging.GetProperty("LoggingAvailable").GetBoolean()&&!logging.GetProperty("SessionStarted").GetBoolean(),"preview hides failed logging state");
                                Check(logging.GetProperty("FirstLoggingFailure").GetProperty("Stage").GetString()=="prepare_log_directory","preview omits safe logging failure evidence");
                            }
                            Check(!json.Text.Contains(accountCanary,StringComparison.Ordinal)&&!json.Text.Contains(secretCanary,StringComparison.Ordinal)&&!json.Text.Contains(fixture,StringComparison.Ordinal),"preview exposed fixture private information");
                            var label=withLogs?"Save diagnostic ZIP…":"Save diagnostic JSON…";
                            Check(Visuals<Button>(preview).Any(button=>button.IsVisible&&button.Content as string==Localizer.T(label)),"save label does not match the available diagnostic format");
                            var notice=withLogs
                                ? blocked
                                    ? "Runtime logs are unavailable for this session. The ZIP includes safe in-memory diagnostics and may include earlier session logs. No account files or credentials are included. Nothing is uploaded."
                                    : "Preview the current diagnostic summary below. Saving creates a ZIP with a filtered JSON summary and bounded runtime logs. Logs are kept in %LOCALAPPDATA%\\GameDevBar\\logs. No account files, cached balances, credentials, or native login files are included. Nothing is uploaded."
                                : "Preview the current diagnostic summary below. Runtime logs are unavailable in this session; saving creates a JSON file only. Nothing is uploaded.";
                            Check(Visuals<TextBlock>(preview).Any(text=>text.Text==Localizer.T(notice)),"preview omits format, privacy, or upload notice");
                        }
                        catch(Exception error){failure=error;}
                        finally{checkedPreview=true;preview?.Close();}
                    }),DispatcherPriority.ApplicationIdle);
                    overview.ExportDiagnostics();
                    Check(checkedPreview,"export returned without a modal diagnostic preview");
                    if(failure is not null)throw new InvalidOperationException("Diagnostic preview verification failed",failure);
                    Check(host.Queries.Events.Count==0,"preview sent a provider query");
                    Check(!File.Exists(Path.Combine(fixture,"settings.json")),"preview wrote account settings");
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
            }
            finally
            {
                overview.AllowClose=true;overview.Close();Localizer.SetLanguage("en-US");runtime?.CompleteSession("test");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
        }
        Console.WriteLine("PASS diagnostics preview: healthy/failed logging and JSON fallback, English/Chinese warning and evidence, read-only summary, private canaries excluded, no native save dialog or provider query");
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject node) where T:DependencyObject
    {
        if(node is T match)yield return match;
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(node);index++)
            foreach(var child in Visuals<T>(VisualTreeHelper.GetChild(node,index)))yield return child;
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
