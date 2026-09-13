namespace Meows.Kitten;

/// <summary>
/// The files, with __Name__, __key__, __Id__, __Icon__, __Category__, __Plain__ and
/// __Description__ filled in. Kept as the plugins are actually written today: the header bar,
/// the error strip, the empty state, the language watch, the search hook, the dispose. When the
/// house style moves, this moves with it, and the build Kitten runs afterwards is what notices
/// if it did not.
/// </summary>
public static class Templates
{
    public const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">

            <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
                <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>

                <OutputPath Condition="'$(Configuration)' == 'Debug'">$(MSBuildThisFileDirectory)..\plugins\Meows.Plugins.__Name__\</OutputPath>
                <OutputPath Condition="'$(Configuration)' != 'Debug'">$(MSBuildThisFileDirectory)bin\$(Configuration)\</OutputPath>
            </PropertyGroup>

            <!-- One file per language. WithCulture and LogicalName are both load bearing: MSBuild reads
                 the middle of Strings.de.json as a culture and would otherwise file it under a de
                 satellite assembly, where the shell never looks. -->
            <ItemGroup>
              <EmbeddedResource Include="Strings\Strings.*.json">
                <WithCulture>false</WithCulture>
                <LogicalName>$(AssemblyName).Strings.%(Filename)%(Extension)</LogicalName>
              </EmbeddedResource>
            </ItemGroup>

            <!-- Avalonia and the contract come from the shell, so neither is copied: the plugin
                 folder holds only what is its own. Add Meows.Disk, Meows.Media or Meows.Bot.Core
                 as plain ProjectReferences if it needs them; those do get copied. -->
            <ItemGroup>
              <PackageReference Include="Avalonia" Version="12.1.1" ExcludeAssets="runtime" />
              <ProjectReference Include="..\Meows.Plugins.Abstractions\Meows.Plugins.Abstractions.csproj"
                                Private="false"
                                ExcludeAssets="runtime" />
            </ItemGroup>

        </Project>

        """;

    public const string Plugin = """
        using Avalonia.Controls;
        using Meows.Plugins.Abstractions;
        using Meows.Plugins.__Name__.ViewModels;
        using Meows.Plugins.__Name__.Views;

        namespace Meows.Plugins.__Name__;

        public sealed class __Name__Plugin : IMeowsPlugin
        {
            /// <summary>Stable forever. It is the settings key and the activation record.</summary>
            public string Id => "__Id__";

            public string DisplayName => "__Name__";

            public string PlainName => "__key__.name.plain";

            public string Description => "__key__.description";

            public string Icon => "__Icon__";

            public string Category => "__Category__";

            public Control CreateView(IMeowsHost host) => new __Name__View
            {
                DataContext = new __Name__ViewModel(host),
            };
        }

        """;

    public const string ViewModel = """
        using Meows.Plugins.Abstractions;

        namespace Meows.Plugins.__Name__.ViewModels;

        public sealed class __Name__Settings
        {
            // Whatever the tab should remember between openings. Plain JSON, camelCase, no schema.
        }

        public sealed class __Name__ViewModel : ObservableObject, IDisposable, ISearchable
        {
            private readonly IMeowsHost _host;
            private __Name__Settings _settings;

            private string? _status;
            private string? _errorMessage;

            /// <summary>
            /// Text worked out in code rather than bound with {m:Tr} has to be read again when the
            /// language changes. Nothing moves, but everything reads differently.
            /// </summary>
            private readonly LanguageWatch _language;

            public __Name__ViewModel(IMeowsHost host)
            {
                _host = host;
                _settings = host.LoadSettings<__Name__Settings>() ?? new __Name__Settings();

                RefreshCommand = new RelayCommand(Refresh);

                _language = new LanguageWatch(OnEverythingChanged);
            }

            public RelayCommand RefreshCommand { get; }

            public string Status
            {
                get => _status ?? _host.Text["__key__.status.ready"];
                private set => SetField(ref _status, value);
            }

            public string? ErrorMessage
            {
                get => _errorMessage;
                private set
                {
                    if (SetField(ref _errorMessage, value))
                        OnPropertyChanged(nameof(HasError));
                }
            }

            public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

            /// <summary>Where the real work starts. Long work goes through _host.Background so the Tasks panel sees it.</summary>
            private void Refresh()
            {
                ErrorMessage = null;
                Status = _host.Text.Format("__key__.status.refreshed", DateTime.Now.ToString("HH:mm"));
            }

            /// <summary>Ctrl+K reaching into this tab. Answer from what is already loaded, never from the disk.</summary>
            public IReadOnlyList<SearchHit> Search(string query, int limit) => [];

            private void SaveSettings()
            {
                try
                {
                    _host.SaveSettings(_settings);
                }
                catch (Exception ex)
                {
                    _host.Log($"Could not save __Name__ settings: {ex.Message}");
                }
            }

            public void Dispose() => _language.Dispose();
        }

        """;

    public const string View = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:Meows.Plugins.__Name__.ViewModels"
                     xmlns:m="using:Meows.Plugins.Abstractions"
                     x:Class="Meows.Plugins.__Name__.Views.__Name__View"
                     x:DataType="vm:__Name__ViewModel">

            <UserControl.Styles>
                <Style Selector="TextBlock.label">
                    <Setter Property="Opacity" Value="0.5" />
                    <Setter Property="FontSize" Value="11" />
                </Style>
            </UserControl.Styles>

            <DockPanel>

                <Border DockPanel.Dock="Top"
                        Background="{DynamicResource MeowsCard}"
                        BorderBrush="{DynamicResource MeowsLineSoft}"
                        BorderThickness="0,0,0,1"
                        Padding="14,10">
                    <Grid ColumnDefinitions="*,Auto" ColumnSpacing="14">
                        <StackPanel Grid.Column="0" Spacing="2">
                            <TextBlock Text="{m:Tr __key__.title}" FontWeight="SemiBold" />
                            <TextBlock Classes="label" Text="{Binding Status}" />
                        </StackPanel>
                        <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="8">
                            <Button Content="{m:Tr __key__.refresh}" Command="{Binding RefreshCommand}" />
                        </StackPanel>
                    </Grid>
                </Border>

                <Border DockPanel.Dock="Top"
                        Background="{DynamicResource MeowsDanger}"
                        BorderBrush="{DynamicResource MeowsDangerLine}"
                        BorderThickness="0,0,0,1"
                        Padding="14,8"
                        IsVisible="{Binding HasError}">
                    <TextBlock Text="{Binding ErrorMessage}" Foreground="{DynamicResource MeowsDangerText}" TextWrapping="Wrap" />
                </Border>

                <TextBlock Margin="14,12"
                           Opacity="0.45"
                           TextWrapping="Wrap"
                           Text="{m:Tr __key__.empty}" />

            </DockPanel>

        </UserControl>

        """;

    public const string ViewCode = """
        using Avalonia.Controls;
        using Avalonia.Markup.Xaml;

        namespace Meows.Plugins.__Name__.Views;

        public partial class __Name__View : UserControl, IDisposable
        {
            public __Name__View()
            {
                InitializeComponent();
            }

            private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

            public void Dispose() => (DataContext as IDisposable)?.Dispose();
        }

        """;

    public const string Readme = """
        # __Name__

        __Description__

        Plugin id `__Id__`.

        ## What it does

        Written by Kitten; the plugin does not do anything yet. Say here what it reads, what it
        shows, and what it changes, in that order, and why it earns a tab rather than a mode on one
        that already exists.

        ## What it refuses to do

        The line it will not cross, so the scope that made it worth building stays honest.

        """;

    public const string Tests = """
        using Meows.Plugins.__Name__.ViewModels;

        namespace Meows.Tests;

        public sealed class __Name__Tests : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "__key__-" + Guid.NewGuid().ToString("N")[..10]);

            public __Name__Tests() => Directory.CreateDirectory(_root);

            [Fact]
            public void Opens_with_something_to_say()
            {
                using var model = new __Name__ViewModel(new FakeHost(Path.Combine(_root, "hostdata")));

                Assert.False(string.IsNullOrWhiteSpace(model.Status));
                Assert.False(model.HasError);
            }

            [Fact]
            public void Refresh_reports_when_it_ran()
            {
                using var model = new __Name__ViewModel(new FakeHost(Path.Combine(_root, "hostdata")));

                model.RefreshCommand.Execute(null);

                Assert.Contains(":", model.Status);
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                }
                catch (Exception)
                {
                }
            }
        }

        """;
}
