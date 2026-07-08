using CommunityToolkit.WinUI.Controls;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.ComponentModel;

namespace FolderRewind.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly SettingsPageViewModel _viewModel = new();

        // SettingsExpander lazy loading
        private readonly HashSet<SettingsExpander> _expanderContentCreated = new();
        private readonly Dictionary<SettingsExpander, long> _expanderCallbackTokens = new();
        private bool _expanderLazyLoadInitialized;

        public SettingsPage()
        {
            this.InitializeComponent();

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            Unloaded -= OnSettingsPageUnloaded;
            Unloaded += OnSettingsPageUnloaded;

            Loaded += async (_, _) =>
            {
                // Inject ViewModel into all child controls
                PresetControl.SetViewModel(_viewModel);
                CoreBehaviorControl.SetViewModel(_viewModel);
                AppearanceLayoutControl.SetViewModel(_viewModel);
                RuntimeEnvControl.SetViewModel(_viewModel);
                DiagnosticsControl.SetViewModel(_viewModel);
                DataManagementControl.SetViewModel(_viewModel);
                PluginsKnotLinkControl.SetViewModel(_viewModel);
                AboutControl.SetViewModel(_viewModel);

                await _viewModel.InitializeAsync();
                InitializeExpanderLazyLoading();
            };
        }

        private void InitializeExpanderLazyLoading()
        {
            if (_expanderLazyLoadInitialized) return;
            if (SettingsScrollViewer is null) return;
            _expanderLazyLoadInitialized = true;

            var expanders = FindAllSettingsExpanders(SettingsScrollViewer);
            foreach (var expander in expanders)
            {
                var token = expander.RegisterPropertyChangedCallback(
                    SettingsExpander.IsExpandedProperty,
                    OnExpanderIsExpandedChanged);
                _expanderCallbackTokens[expander] = token;
            }
        }

        private static List<SettingsExpander> FindAllSettingsExpanders(DependencyObject parent)
        {
            var result = new List<SettingsExpander>();
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is SettingsExpander expander)
                {
                    result.Add(expander);
                }
                result.AddRange(FindAllSettingsExpanders(child));
            }
            return result;
        }

        private void OnExpanderIsExpandedChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (sender is not SettingsExpander expander) return;
            if (!expander.IsExpanded) return;
            if (_expanderContentCreated.Contains(expander)) return;

            _expanderContentCreated.Add(expander);
            // Phase 2 will add actual content creation here
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // ViewModel changes are handled by individual UserControls' own x:Bind.
            // Page-level Bindings.Update() is not needed since the page XAML has no x:Bind.
        }

        private void OnSettingsPageUnloaded(object sender, RoutedEventArgs e)
        {
            _viewModel.SaveIfDirty();

            foreach (var (expander, token) in _expanderCallbackTokens)
            {
                try
                {
                    expander.UnregisterPropertyChangedCallback(
                        SettingsExpander.IsExpandedProperty, token);
                }
                catch { }
            }
            _expanderCallbackTokens.Clear();
            _expanderLazyLoadInitialized = false;
            _expanderContentCreated.Clear();

            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
            Unloaded -= OnSettingsPageUnloaded;
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _viewModel.OnNavigatedTo();
        }
    }
}
