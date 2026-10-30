using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Windows.Graphics;

namespace AppUpdateUiProbe;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        string Option(string key, string fallback)
        {
            var index = Array.IndexOf(arguments, key);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : fallback;
        }
        var locale = Option("--locale", "zh-CN");
        var scenario = Option("--scenario", "setup");
        if (locale is not ("zh-CN" or "en-US")) throw new ArgumentException("Unsupported fixture locale.");
        var strings = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", locale + ".resw"))
            .Root!.Elements("data").ToDictionary(element => (string)element.Attribute("name")!,
                element => element.Element("value")!.Value, StringComparer.Ordinal);
        string Get(string key) => strings[key];

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1.9.6.json")));
        const string api = "https://api.github.com/repos/Leafuke/FolderRewind/releases/latest";
        const string mirror = "https://mirror.invalid/" + api;
        var release = GitHubReleaseService.ParseMetadata(json.RootElement, mirror, mirror, api, "Fixture mirror");
        var channel = InstallChannel.Setup;
        var architecture = Architecture.X64;
        switch (scenario)
        {
            case "setup": break;
            case "manual": channel = InstallChannel.Developer; break;
            case "store": channel = InstallChannel.Store; break;
            case "unsafe-link": release.HtmlUrl = "https://untrusted.invalid/setup.exe"; break;
            case "missing-asset": release.Assets = []; break;
            case "unsupported": architecture = Architecture.X86; break;
            default: throw new ArgumentException("Unsupported fixture scenario.");
        }
        var target = AppUpdatePolicy.Resolve(release, new Version(1, 9, 4, 0), channel, architecture, out var reason)
            ?? throw new InvalidOperationException("Fixture did not resolve: " + reason);

        _window = new Window { Title = "FolderRewind P0 update fixture" };
        var root = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = $"Isolated UI fixture: {locale} / {scenario}", TextWrapping = TextWrapping.Wrap });
        var result = new TextBlock { Text = "No update action invoked", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(result, "ActionTarget");
        var show = new Button { Content = locale == "zh-CN" ? "查看更新" : "View update" };
        AutomationProperties.SetAutomationId(show, "ViewUpdate");
        var info = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Title = Get("Update_Dialog_Title"),
            Message = "v1.9.4.0 → v1.9.6.0 | v1.9.6",
            Severity = InfoBarSeverity.Informational,
            ActionButton = show
        };
        root.Children.Add(info);
        root.Children.Add(result);
        var close = new Button { Content = "Close fixture" };
        AutomationProperties.SetAutomationId(close, "CloseProbe");
        close.Click += (_, _) => _window.Close();
        root.Children.Add(close);
        _window.Content = root;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        _window.AppWindow.Resize(new SizeInt32((int)(800 * scale), (int)(720 * scale)));
        _window.Activate();

        show.Click += async (_, _) =>
        {
            var text = new TextBlock
            {
                Text = AppUpdatePresentation.CreateContent(target, string.Empty, release.OfficialMetadata, Get),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            AutomationProperties.SetAutomationId(text, "UpdateContent");
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = Get("Update_Dialog_Title"),
                Content = new ScrollViewer { MaxHeight = 420, Content = text },
                PrimaryButtonText = Get(AppUpdatePresentation.PrimaryButtonResource(target)),
                CloseButtonText = Get("Update_Dialog_Later"),
                DefaultButton = ContentDialogButton.Primary
            };
            // Deliberately capture rather than launch: this probe cannot install or access app data.
            var answer = await dialog.ShowAsync();
            result.Text = answer == ContentDialogResult.Primary ? target.Uri.AbsoluteUri : "Deferred";
        };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
