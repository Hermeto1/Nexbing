using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ryujinx.Ava.Common;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Models;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Windows
{
    /// <summary>
    /// [Nextendo] Fenêtre « Joueurs » : mon salon en direct, et les dernières
    /// personnes croisées en ligne.
    ///
    /// Ce que cet écran apporte par rapport à ce que le jeu affiche déjà : la
    /// VRAIE identité Nextendo. Les serveurs de jeu publient des pseudos
    /// fabriqués à partir du numéro de compte (« Joueur-3286 ») ; ici on voit le
    /// pseudo réel, la photo de profil, et on peut ajouter ou signaler.
    /// </summary>
    public partial class NextendoLobbyWindow : StyleableAppWindow
    {
        private readonly ObservableCollection<NextendoLobbyPlayerModel> _lobby = [];
        private readonly ObservableCollection<NextendoLobbyPlayerModel> _recent = [];

        private readonly DispatcherTimer _refreshTimer;

        /// <summary>Empreintes du dernier contenu affiché. Voir <see cref="Remplir"/>.</summary>
        private string _lobbySig = "";
        private string _recentSig = "";

        /// <summary>Le joueur visé par la modale de signalement ouverte, 0 si aucune.</summary>
        private ulong _reportTarget;

        /// <summary>Le motif choisi à l'étape 1, vide tant qu'on n'a pas choisi.</summary>
        private string _reportReason = "";

        /// <summary>Codes amis retenus du dernier chargement, pour l'ajout rapide.</summary>
        private readonly Dictionary<ulong, string> _friendCodes = [];

        /// <summary>Une seule fenêtre à la fois ; un second appel réactive la première.</summary>
        private static NextendoLobbyWindow _current;

        public static void Open() => Open(0);

        /// <summary>Ouvre la fenêtre sur l'onglet demandé (0 = salon, 1 = rencontres).</summary>
        public static void Open(int tab)
        {
            if (_current is not null)
            {
                _current.SelectTab(tab);
                _current.Activate();

                return;
            }

            _current = new NextendoLobbyWindow();
            _current.Closed += (_, _) => _current = null;
            _current.SelectTab(tab);
            _current.Show(RyujinxApp.MainWindow);
        }

        public NextendoLobbyWindow() : base(useCustomTitleBar: true, 37)
        {
            Title = LocaleManager.Instance[LocaleKeys.Dialog_Nextendo_LobbyWindowTitle];

            InitializeComponent();

            LobbyList.ItemsSource = _lobby;
            RecentList.ItemsSource = _recent;

            // 5 s et non 20 comme la fenêtre d'amis : la composition d'un salon
            // change en quelques secondes — quelqu'un entre, quelqu'un part, la
            // partie se lance. Une liste rafraîchie toutes les 20 s serait fausse
            // la plupart du temps.
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refreshTimer.Tick += async (_, _) => await Refresh();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            SelectTab(RecentPanel.IsVisible ? 1 : 0);
            _refreshTimer.Start();
            _ = Refresh();
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer.Stop();

            base.OnClosed(e);
        }

        private async Task Refresh()
        {
            await LoadLobby();
            await LoadRecent();
        }

        private async Task LoadLobby()
        {
            NextendoApi.NextendoLobby lobby = await NextendoApi.GetMyLobbyAsync();

            if (!lobby.InLobby)
            {
                // Hors salon : on N'EFFACE PAS la liste tout de suite si elle vient
                // d'un échec réseau — mais ici le serveur a bien répondu « pas de
                // salon », donc c'est un vrai état et non une panne.
                _lobby.Clear();
                _lobbySig = "";
                NoLobbyText.IsVisible = true;
                LobbyScroll.IsVisible = false;
                LobbyGameText.Text = "—";
                LobbyStateText.Text = "";

                return;
            }

            NoLobbyText.IsVisible = false;
            LobbyScroll.IsVisible = true;
            LobbyGameText.Text = NomDuJeu(lobby.TitleId);
            LobbyStateText.Text = LigneEtat(lobby);

            _lobbySig = await Remplir(_lobby, lobby.Players, _lobbySig, montrerLeJeu: false);
        }

        /// <summary>
        /// « 4 / 12 joueurs — appariés ». L'état vient d'un serveur de jeu qui
        /// l'écrit EN FRANÇAIS pour le monitoring : le relayer tel quel affichait
        /// « en recherche » dans une interface en anglais. On traduit donc le code
        /// stable envoyé à côté, et si le serveur n'a pas su classer l'état, on
        /// n'invente rien — on affiche le décompte seul.
        /// </summary>
        private static string LigneEtat(NextendoApi.NextendoLobby lobby)
        {
            LocaleKeys? cle = lobby.StateCode switch
            {
                "searching" => LocaleKeys.Dialog_Nextendo_LobbyStateSearching,
                "matched" => LocaleKeys.Dialog_Nextendo_LobbyStateMatched,
                _ => null,
            };

            if (cle is null)
            {
                return LocaleManager.Instance.UpdateAndGetDynamicValue(
                    LocaleKeys.Dialog_Nextendo_LobbyCountFormat, lobby.Count, lobby.Max);
            }

            return LocaleManager.Instance.UpdateAndGetDynamicValue(
                LocaleKeys.Dialog_Nextendo_LobbyStateFormat,
                lobby.Count, lobby.Max, LocaleManager.Instance[cle.Value]);
        }

        private async Task LoadRecent()
        {
            List<NextendoApi.NextendoPlayer> players = await NextendoApi.GetRecentPlayersAsync();

            // Une liste vide APRÈS une réponse valide est un vrai « personne
            // encore » ; une erreur réseau, elle, a déjà été avalée par l'API qui
            // rend une liste vide aussi. On garde donc l'ancienne liste plutôt que
            // de la vider : une liste qui clignote fait croire que tout le monde
            // est parti.
            if (players.Count == 0 && _recent.Count > 0)
            {
                return;
            }

            NoRecentText.IsVisible = players.Count == 0;
            RecentScroll.IsVisible = players.Count > 0;

            _recentSig = await Remplir(_recent, players, _recentSig, montrerLeJeu: true);
        }

        /// <summary>
        /// Reconstruit la liste, mais SEULEMENT si son contenu a changé.
        ///
        /// Sans cette comparaison, la collection était vidée et refaite toutes les
        /// cinq secondes : la barre de défilement remontait en haut à chaque
        /// passage, ce qui rend une liste de cinquante personnes impossible à
        /// parcourir. Rendue l'empreinte du contenu affiché.
        /// </summary>
        private async Task<string> Remplir(
            ObservableCollection<NextendoLobbyPlayerModel> cible,
            List<NextendoApi.NextendoPlayer> source,
            string empreintePrecedente,
            bool montrerLeJeu)
        {
            // Les avatars d'abord, AVANT de comparer : ils entrent dans
            // l'empreinte. Sans cela, un téléchargement d'avatar qui échoue une
            // fois ne serait jamais retenté — la composition du salon ne changeant
            // pas, la comparaison conclurait « rien de neuf » et la vignette
            // resterait sur l'initiale à vie. L'appel est gratuit dès qu'il a
            // réussi une fois : le cache répond sans requête, et seul un échec
            // repart sur le réseau.
            Dictionary<ulong, byte[]> images = [];
            foreach (NextendoApi.NextendoPlayer p in source)
            {
                images[p.Pid] = await NextendoApi.GetAvatarAsync(p.Pid, p.AvatarUrl);
            }

            StringBuilder sb = new();
            foreach (NextendoApi.NextendoPlayer p in source)
            {
                sb.Append(p.Pid).Append('|').Append(p.Name).Append('|')
                  .Append(p.Host).Append('|').Append(p.TitleId).Append('|')
                  .Append(p.SeenAt.Ticks).Append('|')
                  .Append(images.GetValueOrDefault(p.Pid) is { Length: > 0 }).Append(';');
            }
            string empreinte = sb.ToString();

            // Les codes amis, eux, se mettent à jour même sans changement visible :
            // ils ne coûtent rien et servent au bouton d'ajout.
            foreach (NextendoApi.NextendoPlayer p in source)
            {
                if (!string.IsNullOrEmpty(p.FriendCode))
                {
                    _friendCodes[p.Pid] = p.FriendCode;
                }
            }

            if (empreinte == empreintePrecedente && cible.Count == source.Count)
            {
                return empreinte;
            }

            cible.Clear();
            foreach (NextendoApi.NextendoPlayer p in source)
            {
                cible.Add(new NextendoLobbyPlayerModel
                {
                    Pid = p.Pid,
                    Name = string.IsNullOrEmpty(p.Name) ? $"#{p.Pid}" : p.Name,
                    Image = images.GetValueOrDefault(p.Pid),
                    Known = p.Known,
                    Host = p.Host,
                    IsMe = p.IsMe,
                    GameName = montrerLeJeu ? NomDuJeu(p.TitleId) : "",
                    SeenAt = p.SeenAt,
                });
            }

            return empreinte;
        }

        // Le serveur n'envoie que l'identifiant de titre : il ne connaît pas les
        // noms d'affichage. Cette table reprend exactement les titres que
        // l'émulateur déclare compatibles (ApplicationData.NextendoCompatibleVersion).
        private static string NomDuJeu(string titleId)
        {
            if (string.IsNullOrEmpty(titleId))
            {
                return "";
            }

            return titleId.ToLowerInvariant() switch
            {
                "0100152000022000" => "Mario Kart 8 Deluxe",
                "01006a800016e000" => "Super Smash Bros. Ultimate",
                "0100f8f0000a2000" or "01003bc0000a0000" or "01003c700009c800" => "Splatoon 2",
                "01006f8002326000" => "Animal Crossing: New Horizons",
                "0100dca0064a6000" => "Luigi's Mansion 3",
                "0100c2500fc20000" => "Splatoon 3",
                "01006bd001e06000" => "Minecraft",
                _ => titleId,
            };
        }

        private async void AddFriend_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ulong pid })
            {
                return;
            }

            if (!_friendCodes.TryGetValue(pid, out string code) || string.IsNullOrEmpty(code))
            {
                ShowMainStatus(LocaleManager.Instance[LocaleKeys.Dialog_Nextendo_LobbyAddFailed], false);

                return;
            }

            (bool ok, string message) = await NextendoApi.AddFriendAsync(code);
            ShowMainStatus(
                ok ? LocaleManager.Instance[LocaleKeys.Dialog_Nextendo_LobbyAddSent] : message,
                ok);
        }

        // --- signalement --------------------------------------------------------

        /// <summary>
        /// Ce que chaque motif affiche, et ce qu'on demande d'écrire ensuite. Le
        /// libellé de l'invite change avec le motif : « Que s'est-il passé ? » ne
        /// dit pas quoi écrire, alors que « Quel pseudo affiche-t-il en jeu ? »
        /// obtient l'information qui permettra de trancher.
        /// </summary>
        private static readonly Dictionary<string, (LocaleKeys Label, LocaleKeys Desc, LocaleKeys Hint)> _motifs = new()
        {
            ["cheating"] = (LocaleKeys.Dialog_Nextendo_ReportReasonCheating,
                            LocaleKeys.Dialog_Nextendo_ReportReasonCheatingDesc,
                            LocaleKeys.Dialog_Nextendo_ReportCommentHintCheating),
            ["name"] = (LocaleKeys.Dialog_Nextendo_ReportReasonName,
                        LocaleKeys.Dialog_Nextendo_ReportReasonNameDesc,
                        LocaleKeys.Dialog_Nextendo_ReportCommentHintName),
            ["name_mismatch"] = (LocaleKeys.Dialog_Nextendo_ReportReasonNameMismatch,
                                 LocaleKeys.Dialog_Nextendo_ReportReasonNameMismatchDesc,
                                 LocaleKeys.Dialog_Nextendo_ReportCommentHintMismatch),
            ["avatar"] = (LocaleKeys.Dialog_Nextendo_ReportReasonAvatar,
                          LocaleKeys.Dialog_Nextendo_ReportReasonAvatarDesc,
                          LocaleKeys.Dialog_Nextendo_ReportCommentHintAvatar),
            ["harassment"] = (LocaleKeys.Dialog_Nextendo_ReportReasonHarassment,
                              LocaleKeys.Dialog_Nextendo_ReportReasonHarassmentDesc,
                              LocaleKeys.Dialog_Nextendo_ReportCommentHintHarassment),
            ["griefing"] = (LocaleKeys.Dialog_Nextendo_ReportReasonGriefing,
                            LocaleKeys.Dialog_Nextendo_ReportReasonGriefingDesc,
                            LocaleKeys.Dialog_Nextendo_ReportCommentHint),
            ["impersonation"] = (LocaleKeys.Dialog_Nextendo_ReportReasonImpersonation,
                                 LocaleKeys.Dialog_Nextendo_ReportReasonImpersonationDesc,
                                 LocaleKeys.Dialog_Nextendo_ReportCommentHint),
            ["other"] = (LocaleKeys.Dialog_Nextendo_ReportReasonOther,
                         LocaleKeys.Dialog_Nextendo_ReportReasonOtherDesc,
                         LocaleKeys.Dialog_Nextendo_ReportCommentHint),
        };

        private void Report_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ulong pid })
            {
                return;
            }

            NextendoLobbyPlayerModel joueur =
                _lobby.FirstOrDefault(p => p.Pid == pid) ?? _recent.FirstOrDefault(p => p.Pid == pid);

            _reportTarget = pid;
            _reportReason = "";

            ReportTargetText.Text = joueur?.Name ?? $"#{pid}";
            ReportTargetSubText.Text = joueur?.SeenLine ?? "";
            ReportInitialText.Text = joueur?.Initial ?? "?";
            PoseAvatar(joueur?.Image);

            ReportCommentBox.Text = "";
            MontreEtape1();

            ShowMainStatus("", true);
            ReportOverlay.IsVisible = true;
        }

        /// <summary>Charge l'avatar du signalé dans la modale, ou retombe sur l'initiale.</summary>
        private void PoseAvatar(byte[] octets)
        {
            if (octets is not { Length: > 0 })
            {
                ReportAvatarImage.Source = null;
                ReportAvatarImage.IsVisible = false;
                ReportInitialText.IsVisible = true;

                return;
            }

            try
            {
                using MemoryStream flux = new(octets);
                ReportAvatarImage.Source = new Bitmap(flux);
                ReportAvatarImage.IsVisible = true;
                ReportInitialText.IsVisible = false;
            }
            catch (Exception ex)
            {
                // Une photo de profil illisible ne doit pas empêcher de signaler —
                // c'est même parfois le motif du signalement.
                Logger.Warning?.Print(LogClass.Application, $"[Nextendo] avatar decode failed: {ex.Message}");
                ReportAvatarImage.IsVisible = false;
                ReportInitialText.IsVisible = true;
            }
        }

        private void MontreEtape1()
        {
            ReportModalSubtitleText.IsVisible = true;
            ReportReasonScroll.IsVisible = true;
            ReportChosenBox.IsVisible = false;
            ReportCommentArea.IsVisible = false;
            ReportBackButton.IsVisible = false;
            ReportSendButton.IsVisible = false;
            ShowModalStatus("", true);
        }

        private void ReportReason_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string motif } || !_motifs.TryGetValue(motif, out var infos))
            {
                return;
            }

            _reportReason = motif;

            ReportChosenText.Text = LocaleManager.Instance[infos.Label];
            ReportChosenDescText.Text = LocaleManager.Instance[infos.Desc];
            ReportCommentBox.Watermark = LocaleManager.Instance[infos.Hint];

            ReportModalSubtitleText.IsVisible = false;
            ReportReasonScroll.IsVisible = false;
            ReportChosenBox.IsVisible = true;
            ReportCommentArea.IsVisible = true;
            ReportBackButton.IsVisible = true;
            ReportSendButton.IsVisible = true;
            ReportCommentBox.Focus();
        }

        private void ReportBack_Click(object sender, RoutedEventArgs e)
        {
            _reportReason = "";
            MontreEtape1();
        }

        private void ReportCancel_Click(object sender, RoutedEventArgs e) => FermeModale();

        /// <summary>
        /// Clic sur le voile : on referme. Le test sur la source est indispensable —
        /// sans lui, un clic n'importe où DANS la carte remonterait jusqu'ici et
        /// fermerait la modale au milieu de la saisie.
        /// </summary>
        private void ReportOverlay_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (ReferenceEquals(e.Source, ReportOverlay))
            {
                FermeModale();
            }
        }

        private void FermeModale()
        {
            _reportTarget = 0;
            _reportReason = "";
            ReportOverlay.IsVisible = false;
        }

        private async void ReportSend_Click(object sender, RoutedEventArgs e)
        {
            if (_reportTarget == 0 || string.IsNullOrEmpty(_reportReason))
            {
                return;
            }

            ulong cible = _reportTarget;

            ReportSendButton.IsEnabled = false;
            (bool ok, string erreur) = await NextendoApi.ReportPlayerAsync(cible, _reportReason, ReportCommentBox.Text ?? "");
            ReportSendButton.IsEnabled = true;

            if (ok)
            {
                FermeModale();
                ShowMainStatus(LocaleManager.Instance[LocaleKeys.Dialog_Nextendo_ReportSent], true);

                return;
            }

            // Le serveur distingue ses refus : le joueur mérite de savoir lequel.
            LocaleKeys cle = erreur switch
            {
                "not_encountered" => LocaleKeys.Dialog_Nextendo_ReportNotEncountered,
                "quota" => LocaleKeys.Dialog_Nextendo_ReportQuota,
                _ => LocaleKeys.Dialog_Nextendo_ReportFailed,
            };

            Logger.Info?.Print(LogClass.Application, $"[Nextendo] report refused: {erreur}");
            ShowModalStatus(LocaleManager.Instance[cle], false);
        }

        private void ShowModalStatus(string text, bool ok)
        {
            StatusText.Text = text;
            StatusText.Foreground = Brush.Parse(ok ? "#3EE8C8" : "#E8333E");
            StatusText.IsVisible = !string.IsNullOrEmpty(text);
        }

        private void ShowMainStatus(string text, bool ok)
        {
            MainStatusText.Text = text;
            MainStatusText.Foreground = Brush.Parse(ok ? "#3EE8C8" : "#E8333E");
            MainStatusText.IsVisible = !string.IsNullOrEmpty(text);
        }

        // Selecteur d'onglet fait main. Un TabControl aurait suffi, sauf qu'aucune
        // autre fenetre de l'application n'en utilise : le theme ne style donc pas
        // TabItem, et le bleu vif par defaut d'Avalonia rendait les onglets
        // illisibles sur fond sombre.
        private void SelectTab(int index)
        {
            bool salon = index == 0;

            LobbyPanel.IsVisible = salon;
            RecentPanel.IsVisible = !salon;

            TabLobbyButton.Background = salon ? _ongletActif : _ongletInactif;
            TabRecentButton.Background = salon ? _ongletInactif : _ongletActif;
            TabLobbyButton.Foreground = salon ? _texteActif : _texteInactif;
            TabRecentButton.Foreground = salon ? _texteInactif : _texteActif;
        }

        private void TabLobby_Click(object sender, RoutedEventArgs e) => SelectTab(0);

        private void TabRecent_Click(object sender, RoutedEventArgs e) => SelectTab(1);

        private static readonly IBrush _ongletActif = Brush.Parse("#33FFFFFF");
        private static readonly IBrush _ongletInactif = Brushes.Transparent;
        private static readonly IBrush _texteActif = Brush.Parse("#FFFFFF");
        private static readonly IBrush _texteInactif = Brush.Parse("#99FFFFFF");
    }
}
