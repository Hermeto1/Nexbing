using Avalonia.Interactivity;
using Avalonia.Media;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Common.Configuration;
using System;
using System.Net;

namespace Ryujinx.Ava.UI.Views.Settings
{
    public partial class SettingsNetworkView : RyujinxControl<SettingsViewModel>
    {
        private readonly Random _random;

        public SettingsNetworkView()
        {
            _random = new Random();
            InitializeComponent();

            // [Nextendo] Serveur personnalisé. Vit ici et non dans l'onglet Nextendo Network :
            // ce réglage ÉTEINT Nextendo, il n'en fait pas partie.
            ServerOverrideToggle.IsChecked = NextendoServerOverride.Enabled;
            ServerIpBox.Text = NextendoServerOverride.ServerIpText;
            NatIpBox.Text = NextendoServerOverride.NatIpText;
            OverrideFields.IsEnabled = NextendoServerOverride.Enabled;
            ServerOverrideToggle.IsCheckedChanged += (_, _) =>
                OverrideFields.IsEnabled = ServerOverrideToggle.IsChecked == true;
            SaveOverrideButton.Click += (_, _) => SaveOverride();
        }

        private void GenLdnPassButton_OnClick(object sender, RoutedEventArgs e)
        {
            byte[] code = new byte[4];
            _random.NextBytes(code);
            ViewModel.LdnPassphrase = $"Ryujinx-{BitConverter.ToUInt32(code):x8}";
        }

        private void ClearLdnPassButton_OnClick(object sender, RoutedEventArgs e)
        {
            ViewModel.LdnPassphrase = string.Empty;
        }

        private void TestLanPlayButton_OnClick(object sender, RoutedEventArgs e)
        {
            _ = ViewModel.TestLanPlayConnection();
        }

        /// <summary>
        /// [Nextendo] Enregistre le mode « serveur personnalisé ». Il ne prend effet qu'au
        /// prochain démarrage : la table de redirection est lue une fois, et la relire à chaud
        /// changerait l'adresse d'un jeu déjà connecté.
        /// </summary>
        private void SaveOverride()
        {
            bool actif = ServerOverrideToggle.IsChecked == true;
            string serveur = (ServerIpBox.Text ?? "").Trim();
            string nat = (NatIpBox.Text ?? "").Trim();

            if (actif && !IPAddress.TryParse(serveur, out _))
            {
                ShowOverrideStatus(LocaleManager.Instance[LocaleKeys.Dialog_Nextendo_OverrideBadIp], false);

                return;
            }

            // Les DEUX adresses sont exigées maintenant. Avant, un second champ vide laissait
            // notre répondeur NAT en place — ce qui était précisément « quelque chose qui vient
            // de Nextendo », et n'a plus sa place dans un mode qui s'en détache.
            if (actif && !IPAddress.TryParse(nat, out _))
            {
                ShowOverrideStatus(LocaleManager.Instance[LocaleKeys.Dialog_Nextendo_OverrideNatRequired], false);

                return;
            }

            // Deux répondeurs à la MÊME adresse font échouer le contrôle NAT en silence :
            // Pia les déduplique et n'envoie jamais la seconde sonde.
            if (actif && nat == serveur)
            {
                ShowOverrideStatus(LocaleManager.Instance[LocaleKeys.Dialog_Nextendo_OverrideSameIp], false);

                return;
            }

            NextendoServerOverride.Save(actif, serveur, nat);
            ShowOverrideStatus(LocaleManager.Instance[LocaleKeys.Dialog_Nextendo_OverrideSaved], true);
        }

        private void ShowOverrideStatus(string texte, bool ok)
        {
            OverrideStatusText.Text = texte;
            OverrideStatusText.Foreground = Brush.Parse(ok ? "#3EE8C8" : "#E8333E");
            OverrideStatusText.IsVisible = !string.IsNullOrEmpty(texte);
        }
    }
}
