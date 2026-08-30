using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ryujinx.Ava.Common;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.Models;
using Ryujinx.Common.Configuration;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.Misc
{
    /// <summary>
    /// [Nextendo] App-style profile panel opened from the Switch launcher's circular profile
    /// button. Identity header (avatar / name / friend code / presence + session controls on
    /// top) and three social tabs mirroring the companion app: Friends, Activity and Play
    /// history. Reuses the same API + models as the settings and friends-window views.
    /// </summary>
    public partial class NextendoProfileView : UserControl
    {
        private readonly ObservableCollection<NextendoFriendModel> _friends = [];
        private readonly ObservableCollection<NextendoFriendModel> _requests = [];
        private readonly ObservableCollection<NextendoFriendModel> _playingNow = [];
        private readonly ObservableCollection<NextendoLobbyPlayerModel> _recent = [];
        private readonly ObservableCollection<NextendoHistoryModel> _history = [];

        private readonly DispatcherTimer _refreshTimer;

        public NextendoProfileView()
        {
            InitializeComponent();

            FriendsList.ItemsSource = _friends;
            RequestsList.ItemsSource = _requests;
            PlayingNowList.ItemsSource = _playingNow;
            RecentList.ItemsSource = _recent;
            HistoryList.ItemsSource = _history;

            CopyCodeButton.Click += CopyCode_Click;
            SignOutButton.Click += SignOut_Click;
            ConnectButton.Click += async (_, _) => await ConnectAccount();
            AddFriendButton.Click += async (_, _) => await AddFriend();
            AcceptAllButton.Click += async (_, _) => await AcceptAll();

            // Presence goes stale fast; 20s matches the account server's own freshness window.
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _refreshTimer.Tick += async (_, _) =>
            {
                RefreshOwnStatus();
                _ = LoadFriends();
                _ = LoadActivity();
            };
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            RefreshOwnStatus();

            _ = LoadProfileAsync();
            _ = LoadFriends();
            _ = LoadActivity();
            _ = LoadHistory();

            _refreshTimer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _refreshTimer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        private async Task LoadProfileAsync()
        {
            try
            {
                (string name, byte[] image) = await NextendoApi.GetProfileSyncAsync();

                if (!string.IsNullOrEmpty(name))
                {
                    ProfileName.Text = name;
                }

                if (image is { Length: > 0 })
                {
                    AvatarImage.Source = new Bitmap(new MemoryStream(image));
                }
            }
            catch
            {
                // Cosmetic only.
            }
        }

        private void RefreshOwnStatus()
        {
            bool linked = NextendoAccount.IsLinked;

            ProfileName.Text = string.IsNullOrEmpty(NextendoAccount.Username)
                ? (linked ? "Perfil" : "No conectado")
                : NextendoAccount.Username;
            ProfileFriendCode.Text = string.IsNullOrEmpty(NextendoAccount.FriendCode)
                ? "SW-…"
                : NextendoAccount.FriendCode;

            ProfileStatusDot.Fill = Brush.Parse(linked ? "#33E86B" : "#55808080");
            ProfileStatusText.Text = linked ? "En línea" : "Sin cuenta Nextendo";

            SignOutButton.IsVisible = linked;
            ConnectButton.IsVisible = !linked;
        }

        // Tabs: only the selected panel is visible; pill buttons reflect the active tab.
        private void SelectFriendsTab(object sender, RoutedEventArgs e)
        {
            FriendsTab.IsVisible = true;
            ActivityTab.IsVisible = false;
            HistoryTab.IsVisible = false;
        }

        private void SelectActivityTab(object sender, RoutedEventArgs e)
        {
            FriendsTab.IsVisible = false;
            ActivityTab.IsVisible = true;
            HistoryTab.IsVisible = false;

            _ = LoadActivity();
        }

        private void SelectHistoryTab(object sender, RoutedEventArgs e)
        {
            FriendsTab.IsVisible = false;
            ActivityTab.IsVisible = false;
            HistoryTab.IsVisible = true;

            _ = LoadHistory();
        }

        private async Task ConnectAccount()
        {
            // Full OAuth flow opens the browser; reuse the existing sign-in from the API.
            (bool ok, string error) = await NextendoApi.SignInWithBrowserAsync();
            if (ok)
            {
                ShowStatus(FriendsStatusText, "Cuenta conectada.", true);
                RefreshOwnStatus();
                _ = LoadProfileAsync();
                _ = LoadFriends();
                _ = LoadActivity();
                _ = LoadHistory();
            }
            else if (!string.IsNullOrEmpty(error))
            {
                ShowStatus(FriendsStatusText, error, false);
            }
        }

        private async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            bool confirm = await ContentDialogHelper.CreateConfirmationDialog(
                "Se cerrará tu sesión de Nextendo y se limpiará tu perfil enlazado.",
                "¿Continuar?",
                "Cerrar sesión",
                "Cancelar",
                "Cerrar sesión") == UserResult.Yes;

            if (!confirm)
            {
                return;
            }

            NextendoAccount.Clear();

            _friends.Clear();
            _requests.Clear();
            _history.Clear();
            _recent.Clear();
            _playingNow.Clear();

            RefreshOwnStatus();
        }

        private async Task LoadFriends()
        {
            (List<NextendoApi.Friend> friends, List<NextendoApi.Friend> requests) = await NextendoApi.GetSocialAsync();

            Fill(_friends, friends.OrderByDescending(f => f.Favorite).ThenByDescending(f => f.IsOnline).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
            Fill(_requests, requests);

            NoFriendsText.IsVisible = _friends.Count == 0;
            RequestsPanel.IsVisible = _requests.Count > 0;

            OnlineCountText.Text = _friends.Count > 0
                ? $"{_friends.Count(f => f.IsOnline)} / {_friends.Count} en línea"
                : "";
        }

        private async Task LoadActivity()
        {
            // Who is playing right now: online friends with a game, most recently active first.
            (List<NextendoApi.Friend> friends, _) = await NextendoApi.GetSocialAsync();

            var inGame = friends
                .Where(f => f.IsOnline && !string.IsNullOrEmpty(f.AppId))
                .OrderByDescending(f => f.OnlineStatus)
                .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _playingNow.Clear();
            foreach (var f in inGame)
            {
                byte[] img = null;
                if (!string.IsNullOrEmpty(f.ImageBase64))
                {
                    try { img = Convert.FromBase64String(f.ImageBase64); } catch { /* ignore */ }
                }

                _playingNow.Add(new NextendoFriendModel
                {
                    Pid = f.Pid,
                    Name = f.Name,
                    Image = img,
                    OnlineStatus = f.OnlineStatus,
                    AppId = f.AppId,
                    AppDetail = f.AppDetail,
                });
            }

            PlayingNowText.Text = inGame.Count == 0
                ? "Ninguno de tus amigos está jugando en este momento."
                : "";

            // Recent encounters: people met online, with avatar fetched separately.
            List<NextendoApi.NextendoPlayer> recent = await NextendoApi.GetRecentPlayersAsync();

            _recent.Clear();
            foreach (NextendoApi.NextendoPlayer p in recent)
            {
                byte[] avatar = await NextendoApi.GetAvatarAsync(p.Pid, p.AvatarUrl);

                _recent.Add(new NextendoLobbyPlayerModel
                {
                    Pid = p.Pid,
                    Name = p.Name,
                    Image = avatar,
                    Known = p.Known,
                    GameName = ResolveGame(p.TitleId),
                    SeenAt = p.SeenAt,
                });
            }

            RecentText.IsVisible = _recent.Count == 0;
        }

        private async Task LoadHistory()
        {
            List<NextendoApi.HistoryItem> merged = await NextendoApi.SyncHistoryAsync(NextendoHistorySync.CollectLocalHistory());

            _history.Clear();
            foreach (NextendoApi.HistoryItem h in merged)
            {
                byte[] icon = null;
                if (!string.IsNullOrEmpty(h.IconBase64))
                {
                    try { icon = Convert.FromBase64String(h.IconBase64); } catch { /* ignore */ }
                }

                _history.Add(new NextendoHistoryModel
                {
                    Name = h.Name,
                    Icon = icon,
                    PlayedText = FormatPlayed(h.Seconds),
                    LastText = FormatLast(h.LastPlayed),
                });
            }

            NoHistoryText.IsVisible = _history.Count == 0;
        }

        private static void Fill(ObservableCollection<NextendoFriendModel> target, List<NextendoApi.Friend> source)
        {
            target.Clear();
            foreach (NextendoApi.Friend f in source)
            {
                byte[] img = null;
                if (!string.IsNullOrEmpty(f.ImageBase64))
                {
                    try { img = Convert.FromBase64String(f.ImageBase64); } catch { /* ignore */ }
                }

                target.Add(new NextendoFriendModel
                {
                    Pid = f.Pid,
                    Name = f.Name,
                    FriendCode = f.FriendCode,
                    Image = img,
                    OnlineStatus = f.OnlineStatus,
                    AppId = f.AppId,
                    AppDetail = f.AppDetail,
                    Favorite = f.Favorite,
                });
            }
        }

        private static string ResolveGame(string titleId)
        {
            if (string.IsNullOrEmpty(titleId))
            {
                return "";
            }

            return NextendoGameNames.Resolve(titleId) ?? "";
        }

        private async Task AddFriend()
        {
            string code = AddFriendBox.Text?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                return;
            }

            (bool ok, string message) = await NextendoApi.AddFriendAsync(code);
            ShowStatus(FriendsStatusText, message, ok);

            if (ok)
            {
                AddFriendBox.Text = "";
                await LoadFriends();
            }
        }

        private async Task AcceptAll()
        {
            AcceptAllButton.IsEnabled = false;
            try
            {
                int n = await NextendoApi.AcceptAllRequestsAsync();
                await LoadFriends();
                ShowStatus(FriendsStatusText, n > 0 ? $"Aceptadas {n} solicitudes." : "No hay solicitudes que aceptar.", n > 0);
            }
            finally
            {
                AcceptAllButton.IsEnabled = true;
            }
        }

        private async void AcceptRequest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ulong pid })
            {
                await NextendoApi.AcceptFriendAsync(pid);
                await LoadFriends();
            }
        }

        private async void DeclineRequest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ulong pid })
            {
                await NextendoApi.DeclineFriendAsync(pid);
                await LoadFriends();
            }
        }

        private async void RemoveFriend_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ulong pid })
            {
                await NextendoApi.RemoveFriendAsync(pid);
                await LoadFriends();
            }
        }

        private async void Favorite_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ulong pid })
            {
                bool newState = _friends.FirstOrDefault(f => f.Pid == pid) is not { Favorite: true };
                await NextendoApi.SetFavoriteAsync(pid, newState);
                await LoadFriends();
            }
        }

        private async void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is not null && !string.IsNullOrEmpty(NextendoAccount.FriendCode))
            {
                await top.Clipboard.SetTextAsync(NextendoAccount.FriendCode);
            }
        }

        private static void ShowStatus(TextBlock target, string text, bool ok)
        {
            target.Text = text;
            target.Foreground = Brush.Parse(ok ? "#3EE8C8" : "#E8333E");
            target.IsVisible = !string.IsNullOrEmpty(text);
        }

        private static string FormatPlayed(long seconds)
        {
            if (seconds < 60)
            {
                return "Un instante";
            }
            if (seconds < 3600)
            {
                return $"{seconds / 60} min";
            }
            long hours = seconds / 3600;
            return hours <= 1 ? "1 hora o más" : $"{hours} h";
        }

        private static string FormatLast(string iso)
        {
            if (string.IsNullOrEmpty(iso) ||
                !DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime dt))
            {
                return "";
            }

            int days = (int)(DateTime.UtcNow.Date - dt.ToUniversalTime().Date).TotalDays;
            if (days <= 0)
            {
                return "Hoy";
            }
            if (days == 1)
            {
                return "Ayer";
            }
            if (days < 30)
            {
                return $"Hace {days} días";
            }
            int months = days / 30;
            return months == 1 ? "Hace 1 mes" : $"Hace {months} meses";
        }
    }
}
