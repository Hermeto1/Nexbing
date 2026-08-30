using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Utilities;
using Ryujinx.Input;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.Misc
{
    public partial class ApplicationCarouselView : RyujinxControl<MainWindowViewModel>
    {
        public static readonly RoutedEvent<ApplicationOpenedEventArgs> ApplicationOpenedEvent =
            RoutedEvent.Register<ApplicationCarouselView, ApplicationOpenedEventArgs>(nameof(ApplicationOpened), RoutingStrategies.Bubble);

        public event EventHandler<ApplicationOpenedEventArgs> ApplicationOpened
        {
            add => AddHandler(ApplicationOpenedEvent, value);
            remove => RemoveHandler(ApplicationOpenedEvent, value);
        }

        private const double SelectedScale = 1.07;
        private const double UnselectedScale = 0.97;
        private const double SelectedLift = -26;
        private const double UnselectedSink = 6;

        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _connectionTimer;
        private readonly DispatcherTimer _gamepadTimer;

        private bool _gamepadLeftPressed;
        private bool _gamepadRightPressed;
        private bool _gamepadUpPressed;
        private bool _gamepadDownPressed;
        private bool _gamepadConfirmDown;

        private const int NavProfile = -1;
        private const int NavCarousel = 0;
        private const int NavBottom = 1;

        private int _navLevel;
        private int _bottomIndex;

        public ApplicationCarouselView()
        {
            InitializeComponent();

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, _) => UpdateClock();
            _clockTimer.Start();

            _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _connectionTimer.Tick += async (_, _) => await RefreshConnectionStatusAsync(false);
            _connectionTimer.Start();

            _gamepadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _gamepadTimer.Tick += (_, _) => PollGamepad();
            _gamepadTimer.Start();

            CarouselList.SelectionChanged += CarouselList_SelectionChanged;
            Loaded += (_, _) => { _ = LoadAvatarAsync(); LoadDiscordSvg(); };
            Loaded += (_, _) => LoadWallpaper();
            ConfigurationState.Instance.UI.WallpaperPath.Event += (_, _) => LoadWallpaper();
        }

        // [Nextendo] Applies the user's chosen wallpaper image as the launcher background.
        private void LoadWallpaper()
        {
            try
            {
                string path = ConfigurationState.Instance.UI.WallpaperPath.Value;
                if (WallpaperBackground != null)
                {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        WallpaperBackground.Source = new Bitmap(path);
                    else
                        WallpaperBackground.Source = null;
                }
            }
            catch
            {
                // Ignore bad/invalid wallpaper files; the carousel just keeps its default look.
            }
        }

        public void GameLaunched() { }

        public void CarouselList_DoubleTapped(object sender, TappedEventArgs args)
        {
            if (sender is ListBox { SelectedItem: ApplicationData selected })
                RaiseEvent(new ApplicationOpenedEventArgs(selected, ApplicationOpenedEvent));
        }

        private async void ProfileButton_OnClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                ClearBottomFocus();
                // [Nextendo] Full app-style profile: identity header + Friends/Activity/History
                // tabs in a modal dialog, shown from the Switch launcher's circular button.
                var profile = new NextendoProfileView();
                var dialog = new ContentDialog
                {
                    Title = "Perfil",
                    Content = profile,
                    CloseButtonText = "Cerrar",
                };
                await ContentDialogHelper.ShowAsync(dialog);
            }
            catch (Exception)
            {
                // Never let a UI issue in the profile block the launcher.
            }
        }

        private void ClearBottomFocus()
        {
            _navLevel = NavCarousel;
            UpdateSectionHighlights();
        }

        private void WebsiteButton_OnClick(object? sender, RoutedEventArgs e)
        {
            ClearBottomFocus();
            try { Ryujinx.Common.Helper.OpenHelper.OpenUrl(Ryujinx.Ava.Common.NextendoApi.SiteUrl()); }
            catch (Exception) { /* ignore */ }
        }

        private void StatusButton_OnClick(object? sender, RoutedEventArgs e)
        {
            ClearBottomFocus();
            try { Ryujinx.Common.Helper.OpenHelper.OpenUrl("https://nextendo.network/status"); }
            catch (Exception) { /* ignore */ }
        }

        private void DiscordButton_OnClick(object? sender, RoutedEventArgs e)
        {
            ClearBottomFocus();
            try { Ryujinx.Common.Helper.OpenHelper.OpenUrl("https://discord.com/invite/nextendonetwork"); }
            catch (Exception) { /* ignore */ }
        }

        private async void NewsButton_OnClick(object? sender, RoutedEventArgs e)
        {
            // [Nextendo] "What's new" news panel.
            ClearBottomFocus();
            try { await Ryujinx.Ava.Common.NextendoPatchNotes.ShowAsync(); }
            catch (Exception) { /* ignore */ }
        }

        private void CarouselList_KeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Avalonia.Input.Key.Down:
                    if (_navLevel == NavProfile)
                        ExitProfile();
                    else if (_navLevel == NavCarousel)
                        EnterBottom();
                    e.Handled = true;
                    break;
                case Avalonia.Input.Key.Up:
                    if (_navLevel == NavBottom)
                        ExitBottom();
                    else if (_navLevel == NavCarousel)
                        EnterProfile();
                    e.Handled = true;
                    break;
                case Avalonia.Input.Key.Right:
                    if (_navLevel == NavBottom)
                        MoveBottom(1);
                    else if (_navLevel == NavCarousel)
                        MoveBy(1);
                    e.Handled = true;
                    break;
                case Avalonia.Input.Key.Left:
                    if (_navLevel == NavBottom)
                        MoveBottom(-1);
                    else if (_navLevel == NavCarousel)
                        MoveBy(-1);
                    e.Handled = true;
                    break;
                case Avalonia.Input.Key.Enter:
                case Avalonia.Input.Key.Space:
                    if (_navLevel == NavProfile)
                        ProfileButton_OnClick(this, null);
                    else if (_navLevel == NavBottom)
                        ActivateBottom();
                    else
                        Confirm();
                    e.Handled = true;
                    break;
            }
        }

        internal void MoveLeft() => MoveBy(-1);
        internal void MoveRight() => MoveBy(1);
        internal void Confirm()
        {
            if (CarouselList.SelectedItem is ApplicationData selected)
                RaiseEvent(new ApplicationOpenedEventArgs(selected, ApplicationOpenedEvent));
        }

        private Avalonia.Controls.Button GetBottomButton(int index) => index switch
        {
            0 => WebsiteButton,
            1 => DiscordButton,
            _ => NewsButton,
        };

        private void EnterBottom()
        {
            _navLevel = NavBottom;
            _bottomIndex = 0;
            UpdateSectionHighlights();
        }

        private void ExitBottom()
        {
            _navLevel = NavCarousel;
            UpdateSectionHighlights();
        }

        private void EnterProfile()
        {
            _navLevel = NavProfile;
            UpdateSectionHighlights();
        }

        private void ExitProfile()
        {
            _navLevel = NavCarousel;
            UpdateSectionHighlights();
        }

        private void MoveBottom(int delta)
        {
            _bottomIndex = Math.Clamp(_bottomIndex + delta, 0, 2);
            UpdateSectionHighlights();
        }

        private void UpdateSectionHighlights()
        {
            bool bottomFocused = _navLevel == NavBottom;
            for (int i = 0; i < 3; i++)
                GetBottomButton(i).Classes.Set("carouselBottomSelected", bottomFocused && i == _bottomIndex);

            if (ProfileSelectionRing != null)
                ProfileSelectionRing.IsVisible = _navLevel == NavProfile;
        }

        private void ActivateBottom()
        {
            switch (_bottomIndex)
            {
                case 0: WebsiteButton_OnClick(this, null); break;
                case 1: DiscordButton_OnClick(this, null); break;
                default: NewsButton_OnClick(this, null); break;
            }
        }

private void CarouselList_RightTapped(object? sender, RoutedEventArgs e)
        {
            if (CarouselList.SelectedItem is ApplicationData selected)
            {
                var flyout = new Flyout
                {
                    Placement = FlyoutPlacementMode.Bottom,
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 8,
                        Children = new UIElement[]
                        {
                            new TextBlock { Text = "Start", FontWeight = FontWeight.SemiBold, PointerPressed = OnMenuItemClick },
                            new TextBlock { Text = "Favorite", FontWeight = FontWeight.SemiBold, PointerPressed = OnMenuItemClick },
                            new TextBlock { Text = "Manage Updates", FontWeight = FontWeight.SemiBold, PointerPressed = OnMenuItemClick },
                            new TextBlock { Text = "Manage Dlc", FontWeight = FontWeight.SemiBold, PointerPressed = OnMenuItemClick },
                            new TextBlock { Text = "Manage Mods", FontWeight = FontWeight.SemiBold, PointerPressed = OnMenuItemClick },
                        }
                    }
                };
                flyout.ShowAt(CarouselList, e.GetPosition(CarouselList));
            }
        }

        private void OnMenuItemClick(object? sender, PointerPressedEventArgs e)
        {
            // Placeholder: aquí se implementaría la lógica para cada opción
            // Start, Favorite, Manage Updates, Manage Dlc, Manage Mods
            // Para Mario Kart 8 Deluxe: mostrar menú de banderas de país
            Flyout flyout = sender as Flyout;
            if (flyout != null)
                flyout.Hide();
        }

        private void MoveBy(int delta)
        {
            if (ViewModel.AppsObservableList == null || ViewModel.AppsObservableList.Count == 0)
                return;

            ApplicationData current = CarouselList.SelectedItem as ApplicationData;
            int index = current != null ? ViewModel.AppsObservableList.IndexOf(current) : -1;
            if (index < 0)
                index = 0;

            int target = Math.Clamp(index + delta, 0, ViewModel.AppsObservableList.Count - 1);
            CarouselList.SelectedIndex = target;
            CarouselList.ScrollIntoView(ViewModel.AppsObservableList[target]);
        }

        private void CarouselList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            ApplySelectionScale();
        }

        private void ApplySelectionScale()
        {
            var panel = CarouselList.ItemsPanelRoot as Panel;
            if (panel == null)
                return;

            var selectedContainer = CarouselList.SelectedItem != null
                ? CarouselList.ContainerFromItem(CarouselList.SelectedItem)
                : null;

            foreach (var child in panel.Children)
            {
                if (child is ListBoxItem item)
                {
                    bool isSelected = ReferenceEquals(item, selectedContainer);

                    // The selected tile is enlarged and lifted upward while the neighbours sink
                    // and shrink, so it separates from the row instead of overlapping them
                    // (Switch home-menu style). ZIndex keeps the selected tile on top.
                    double scale = isSelected ? SelectedScale : UnselectedScale;
                    double dy = isSelected ? SelectedLift : UnselectedSink;

                    var transform = new TransformGroup();
                    transform.Children.Add(new ScaleTransform(scale, scale));
                    transform.Children.Add(new TranslateTransform(0, dy));

                    item.RenderTransform = transform;
                    item.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                    item.ZIndex = isSelected ? 10 : 0;
                }
            }
        }

        private IGamepad _menuGamepad;
        private string _menuGamepadId;

        private void PollGamepad()
        {
            IGamepad gamepad = GetConfiguredGamepad();
            if (gamepad == null)
            {
                _gamepadLeftPressed = false;
                _gamepadRightPressed = false;
                _gamepadUpPressed = false;
                _gamepadDownPressed = false;
                _gamepadConfirmDown = false;
                return;
            }

            // The configured gamepad maps physical inputs to logical Switch buttons via the
            // user's settings, so navigation honours whatever the player assigned in options
            // (remapped A/D-pad, the left stick, swapped buttons, etc.).
            GamepadStateSnapshot snapshot = gamepad.GetMappedStateSnapshot();
            (float stickX, float stickY) = snapshot.GetStick(StickInputId.Left);

            bool left = snapshot.IsPressed(GamepadButtonInputId.DpadLeft) || stickX < -0.5f;
            bool right = snapshot.IsPressed(GamepadButtonInputId.DpadRight) || stickX > 0.5f;
            bool up = snapshot.IsPressed(GamepadButtonInputId.DpadUp) || stickY > 0.5f;
            bool down = snapshot.IsPressed(GamepadButtonInputId.DpadDown) || stickY < -0.5f;
            bool confirm = snapshot.IsPressed(GamepadButtonInputId.A) ||
                           snapshot.IsPressed(GamepadButtonInputId.B);

            if (down && !_gamepadDownPressed)
            {
                if (_navLevel == NavProfile)
                    ExitProfile();
                else if (_navLevel == NavCarousel)
                    EnterBottom();
            }
            if (up && !_gamepadUpPressed)
            {
                if (_navLevel == NavBottom)
                    ExitBottom();
                else if (_navLevel == NavCarousel)
                    EnterProfile();
            }
            if (left && !_gamepadLeftPressed)
            {
                if (_navLevel == NavBottom)
                    MoveBottom(-1);
                else if (_navLevel == NavCarousel)
                    MoveBy(-1);
            }
            if (right && !_gamepadRightPressed)
            {
                if (_navLevel == NavBottom)
                    MoveBottom(1);
                else if (_navLevel == NavCarousel)
                    MoveBy(1);
            }
            if (confirm && !_gamepadConfirmDown)
            {
                if (_navLevel == NavProfile)
                    ProfileButton_OnClick(this, null);
                else if (_navLevel == NavBottom)
                    ActivateBottom();
                else
                    Confirm();
            }

            _gamepadLeftPressed = left;
            _gamepadRightPressed = right;
            _gamepadUpPressed = up;
            _gamepadDownPressed = down;
            _gamepadConfirmDown = confirm;
        }

        private IGamepad GetConfiguredGamepad()
        {
            Ryujinx.Input.HLE.InputManager inputManager = ViewModel.InputManager;
            if (inputManager?.GamepadDriver == null)
                return null;

            // Use the controller the user selected in the emulator's input settings (player 1).
            Ryujinx.Common.Configuration.Hid.InputConfig config = null;
            if (ViewModel.AppHost?.NpadManager != null)
                config = ViewModel.AppHost.NpadManager.GetPlayerInputConfigByIndex(0);

            string targetId = config is Ryujinx.Common.Configuration.Hid.Controller.StandardControllerInputConfig ? config.Id : null;

            // Fall back to the first connected gamepad when no controller mapping is active.
            if (string.IsNullOrEmpty(targetId))
            {
                foreach (var g in inputManager.GamepadDriver.GetGamepads())
                {
                    if (g.IsConnected)
                    {
                        targetId = g.Id;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(targetId))
                return null;

            if (_menuGamepad != null && _menuGamepad.Id == targetId)
            {
                if (_menuGamepad.IsConnected)
                    return _menuGamepad;

                _menuGamepad?.Dispose();
                _menuGamepad = null;
            }

            if (_menuGamepadId != targetId)
            {
                _menuGamepad?.Dispose();
                _menuGamepad = null;
                _menuGamepadId = targetId;
            }

            if (_menuGamepad == null)
            {
                try
                {
                    _menuGamepad = inputManager.GamepadDriver.GetGamepad(targetId);
                    if (_menuGamepad != null && config != null && !string.IsNullOrEmpty(config.Id))
                        _menuGamepad.SetConfiguration(config);
                }
                catch (Exception)
                {
                    _menuGamepad = null;
                }
            }

            return _menuGamepad;
        }

        private async Task LoadAvatarAsync()
        {
            // [Nextendo] Profile photo for the circular profile button. Loaded async so the
            // UI never blocks on the network. Falls back to a blank avatar on failure.
            if (!NextendoAccount.IsLinked)
                return;

            try
            {
                var profile = await Ryujinx.Ava.Common.NextendoApi.GetProfileSyncAsync();
                if (profile.image != null && profile.image.Length > 0 && ProfileAvatarImage != null)
                {
                    using var mem = new MemoryStream(profile.image);
                    var bitmap = new Avalonia.Media.Imaging.Bitmap(mem);
                    ProfileAvatarImage.Source = bitmap;
                }
            }
            catch (Exception)
            {
                // ignore network/avatar errors
            }
        }

        private void UpdateClock()
        {
            if (ClockTextBlock != null)
                ClockTextBlock.Text = DateTime.Now.ToString("HH:mm");
        }

        private async Task RefreshConnectionStatusAsync(bool force)
        {
            // Throttle: rely on the periodic timer.
            if (!force && _connectionLastCheck != null &&
                (DateTime.UtcNow - _connectionLastCheck.Value).TotalSeconds < 8)
                return;

            _connectionLastCheck = DateTime.UtcNow;

            bool hasInternet = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            bool linked = NextendoAccount.IsLinked && !NextendoServerOverride.HorsNextendo;

            await Dispatcher.UIThread.InvokeAsync(() => UpdateConnectionDots(hasInternet, linked));
        }

        private DateTime? _connectionLastCheck;
        private Avalonia.Media.IBrush? internetBrush;

        private static bool IsWifi(System.Net.NetworkInformation.NetworkInterfaceType type)
        {
            return type == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211;
        }

        private void UpdateConnectionDots(bool hasInternet, bool linked)
        {
            if (NextendoStatusDot != null)
            {
                NextendoStatusDot.Fill = linked ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#33E86B"))
                                                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7A7A7A"));
            }

            var brush = internetBrush = hasInternet
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#33E86B"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7A7A7A"));

            bool wifi = false;
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                {
                    wifi = true;
                    break;
                }
            }

            if (EthSignalBars != null)
                EthSignalBars.IsVisible = !wifi;

            if (WifiStrengthSymbol != null)
            {
                WifiStrengthSymbol.IsVisible = wifi;
                WifiStrengthSymbol.Foreground = wifi ? brush : null;
            }

            if (!wifi && EthSignalBars != null)
            {
                EthBar1.Fill = hasInternet ? brush : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7A7A7A"));
                EthBar2.Fill = hasInternet ? brush : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7A7A7A"));
                EthBar3.Fill = hasInternet ? brush : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7A7A7A"));
                EthBar4.Fill = hasInternet ? brush : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7A7A7A"));
            }
        }
    }
}
