using DiscordRPC;
using Gommon;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.Systems.PlayReport;
using Ryujinx.Ava.Utilities;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.HLE;
using Ryujinx.HLE.Loaders.Processes;
using Ryujinx.Horizon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ryujinx.Ava.Systems
{
    public static class DiscordIntegrationModule
    {
        public static Timestamps EmulatorStartedAt { get; set; }
        public static Timestamps GuestAppStartedAt { get; set; }

        private static string VersionString
            => (ReleaseInformation.IsCanaryBuild ? "Canary " : string.Empty) + $"v{ReleaseInformation.Version}";

        private static readonly string _description = ReleaseInformation.IsValid ? VersionString : "dev build";

        private const string ApplicationId = "1293250299716173864";

        // [Nextendo] Le nom en gras affiché par Discord (« Ryujinx ») n'est PAS dans le message
        // envoyé : il vient du nom de l'application Discord désignée par l'identifiant ci-dessus,
        // qui appartient à l'équipe Ryujinx amont. La bibliothèque épinglée (DiscordRichPresence
        // 1.6.1.70) n'expose aucun champ « nom » — vérifié dans BaseRichPresence, qui ne porte que
        // State/Details/Timestamps/Assets/Party/Secrets/Type/StatusDisplay.
        //
        // D'où une seconde application, la nôtre, nommée « Ryujinx | Nextendo ». Elle n'est
        // utilisée QUE pendant un jeu compatible Nextendo : les clés d'images appartiennent à
        // l'application qui les héberge, donc basculer tout le temps ferait perdre leur jaquette
        // aux 198 jeux couverts par l'application amont. Vide = on ne bascule jamais.
        //
        // Application « Ryujinx | Nextendo », créée le 19/08/2026. Cet identifiant est PUBLIC —
        // c'est ce que tout client RPC annonce en clair ; la clé publique et les jetons de
        // l'application, eux, ne doivent jamais entrer ici.
        private const string NextendoApplicationId = "1539653082152050789";

        private const int ApplicationByteLimit = 128;
        private const string Ellipsis = "…";

        private static DiscordRpcClient _discordClient;

        /// <summary>L'application avec laquelle le client courant a été ouvert.</summary>
        private static string _clientAppId;

        private static RichPresence _discordPresenceMain;
        private static RichPresence _discordPresencePlaying;
        private static ApplicationMetadata _currentApp;

        /// <summary>Le titre en cours, retenu pour recomposer la présence sans attendre un rapport.</summary>
        private static string _currentTitleId = "";

        public static bool HasAssetImage(string titleId) => TitleIDs.DiscordGameAssetKeys.ContainsIgnoreCase(titleId);
        public static bool HasAnalyzer(string titleId) => PlayReports.Analyzer.TitleIds.ContainsIgnoreCase(titleId);

        public static void Initialize()
        {
            _discordPresenceMain = new RichPresence
            {
                Assets = new Assets
                {
                    LargeImageKey = "ryujinx",
                    LargeImageText = TruncateToByteLength(_description)
                },
                Details = "Main Menu",
                State = "Waiting",
                Timestamps = EmulatorStartedAt
            };

            ConfigurationState.Instance.EnableDiscordIntegration.Event += Update;
            TitleIDs.CurrentApplication.Event += (_, e) => Use(e.NewValue);
            HorizonStatic.PlayReport += HandlePlayReport;
            PlayReports.Initialize();

            // [Nextendo] Le salon change sans que le jeu n'émette quoi que ce soit : quelqu'un
            // entre, quelqu'un part. On republie donc à chaque changement observé côté serveur.
            NextendoDiscordPresence.Change += SurChangementDeSalon;

            // Même chose pour le circuit, repéré au chargement de ses fichiers : aucun rapport
            // de jeu ne l'annonce à ce moment-là.
            NextendoCourseWatcher.Change += SurChangementDeSalon;
        }

        private static void SurChangementDeSalon()
        {
            if (_discordClient is null || _discordPresencePlaying is null)
            {
                return;
            }

            AppliquerNextendo(_discordPresencePlaying);
            _discordClient.SetPresence(_discordPresencePlaying);
        }

        private static void Update(object sender, ReactiveEventArgs<bool> evnt)
        {
            if (evnt.OldValue != evnt.NewValue)
            {
                // If the integration was active, disable it and unload everything
                if (evnt.OldValue)
                {
                    _discordClient?.Dispose();

                    _discordClient = null;
                }

                // If we need to activate it and the client isn't active, initialize it
                if (evnt.NewValue && _discordClient == null)
                {
                    OuvrirClient(AppIdPour(_currentTitleId));

                    Use(TitleIDs.CurrentApplication);
                }
            }
        }

        /// <summary>
        /// [Nextendo] L'application Discord à employer pour ce titre.
        ///
        /// L'application détermine le nom affiché en gras ET le jeu d'images disponibles : une clé
        /// d'image n'existe que dans l'application qui l'héberge. On ne bascule donc que pour les
        /// titres où l'on apporte quelque chose — ceux qui tournent sur nos serveurs.
        /// </summary>
        private static string AppIdPour(string titleId)
        {
            if (string.IsNullOrEmpty(NextendoApplicationId) || string.IsNullOrEmpty(titleId))
            {
                return ApplicationId;
            }

            // En mode « serveur personnalisé », on ne se réclame pas de Nextendo : le nom
            // affiché redevient « Ryujinx », et les images restent celles de l'application
            // amont. Afficher « Ryujinx | Nextendo » pendant une partie sur le serveur de
            // quelqu'un d'autre serait faux pour tout le monde.
            if (NextendoServerOverride.HorsNextendo)
            {
                return ApplicationId;
            }

            return NextendoIcones.EstCompatible(titleId) ? NextendoApplicationId : ApplicationId;
        }

        /// <summary>
        /// Ouvre (ou rouvre) le client sur l'application demandée. Discord lie le nom affiché à la
        /// connexion elle-même : changer d'application impose donc de refermer et de rouvrir, on ne
        /// peut pas le faire dans un simple message.
        /// </summary>
        private static void OuvrirClient(string appId)
        {
            if (_discordClient is not null && _clientAppId == appId)
            {
                return;
            }

            _discordClient?.Dispose();
            _clientAppId = appId;
            _discordClient = new DiscordRpcClient(appId);

            // [Nextendo] Preuve, et non supposition : l'URL d'image externe n'est documentée que
            // pour les chemins récents de Discord, pas pour le RPC local qu'on emploie. Discord
            // répond en renvoyant la clé qu'il a retenue — préfixée « mp:external » s'il a bien
            // accepté l'URL et l'a passée par son proxy. On le consigne pour pouvoir affirmer que
            // ça marche, ou constater que non, au lieu d'en débattre.
            _discordClient.OnPresenceUpdate += (_, e) =>
            {
                if (e.Presence?.Assets is null)
                {
                    return;
                }

                Logger.Info?.Print(LogClass.UI,
                    $"[Nextendo] Discord (app {appId}) a retenu large_image=\"{e.Presence.Assets.LargeImageID}\" " +
                    $"externe={e.Presence.Assets.IsLargeImageKeyExternal}");
            };

            _discordClient.Initialize();
        }

        public static void Use(Optional<string> titleId)
        {
            if (titleId.TryGet(out string tid) && Switch.Shared.Processes.ActiveApplication is not null)
            {
                _currentTitleId = tid ?? "";

                // La bascule d'application doit précéder la publication : sinon le premier message
                // part sur l'ancienne application et l'utilisateur voit le mauvais nom une seconde.
                if (_discordClient is not null)
                {
                    OuvrirClient(AppIdPour(_currentTitleId));
                }

                // Le sondage du salon ne sert que pour un jeu qui tourne chez nous ; ailleurs il
                // interrogerait notre serveur pour rien.
                if (NextendoIcones.EstCompatible(_currentTitleId))
                {
                    NextendoDiscordPresence.Start();
                }
                else
                {
                    NextendoDiscordPresence.Stop();
                }

                // L'écoute des chemins de circuit coûte sur CHAQUE accès fichier du jeu : elle
                // ne s'allume que pour Mario Kart, le seul titre dont on sache lire les noms.
                if (EstMarioKart(_currentTitleId))
                {
                    NextendoCourseWatcher.Demarrer();
                }
                else
                {
                    NextendoCourseWatcher.Arreter();
                }

                SwitchToPlayingState(
                    ApplicationLibrary.LoadAndSaveMetaData(tid),
                    Switch.Shared.Processes.ActiveApplication
                );
            }
            else
            {
                _currentTitleId = "";
                NextendoDiscordPresence.Stop();
                NextendoCourseWatcher.Arreter();

                if (_discordClient is not null)
                {
                    OuvrirClient(ApplicationId);
                }

                SwitchToMainState();
            }
        }

        /// <summary>Mario Kart 8 Deluxe, mondial et Chine — les deux identifiants de la spec.</summary>
        private static bool EstMarioKart(string titleId) =>
            titleId is "0100152000022000" or "010075100e8ec000";

        private static RichPresence CreatePlayingState(ApplicationMetadata appMeta, ProcessResult procRes)
        {
            string tid = procRes.ProgramIdText;

            // [Nextendo] Sur notre application, les clés d'images d'en face n'existent pas — mais
            // Discord accepte aussi une URL https directement dans ce champ, qu'il convertit
            // lui-même en image de son proxy. Aucun jeton, aucun téléversement. C'est ce qui répare
            // la « cartouche vide » de Splatoon 3 et de Minecraft, absents des 198 clés amont.
            bool surNous = !string.IsNullOrEmpty(NextendoApplicationId) && _clientAppId == NextendoApplicationId;
            string grandeImage = surNous ? NextendoIcones.UrlJeu(tid) : "";
            if (string.IsNullOrEmpty(grandeImage))
            {
                grandeImage = surNous ? NextendoIcones.UrlReseau() : TitleIDs.GetDiscordGameAsset(tid);
            }

            RichPresence presence = new()
            {
                Assets = new Assets
                {
                    LargeImageKey = grandeImage,
                    LargeImageText = TruncateToByteLength($"{appMeta.Title} (v{procRes.DisplayVersion})"),
                    SmallImageKey = surNous ? NextendoIcones.UrlReseau() : "ryujinx",
                    SmallImageText = TruncateToByteLength(
                        surNous ? $"Nextendo Network · {_description}" : _description),
                },
                Details = TruncateToByteLength($"Playing {appMeta.Title}"),
                Timestamps = GuestAppStartedAt ??= Timestamps.Now
            };

            _etatJeu = "";
            _etatParDefaut = appMeta.LastPlayed.HasValue && appMeta.TimePlayed.TotalSeconds > 5
                ? $"Total play time: {ValueFormatUtils.FormatTimeSpan(appMeta.TimePlayed)}"
                : "Never played";

            AppliquerNextendo(presence);

            return presence;
        }

        /// <summary>Deuxième ligne venue du rapport de jeu, hors salon. Peut être vide.</summary>
        private static string _etatJeu = "";

        /// <summary>Ce qu'on affiche quand ni le salon ni le jeu n'ont rien à dire.</summary>
        private static string _etatParDefaut = "";

        /// <summary>
        /// [Nextendo] Pose ce que seul NOTRE serveur sait : que le joueur est en ligne, et combien
        /// ils sont. Le rapport de jeu ne le dit pas — une course contre l'ordinateur et une course
        /// mondiale produisent le même rapport.
        ///
        /// Le groupe (Party) est renseigné en plus du texte : Discord l'affiche de lui-même sous la
        /// forme « (4 sur 12) », ce qu'aucune phrase ne rend aussi bien.
        ///
        /// ⚠️ Cette méthode RECOMPOSE State de zéro à partir des deux sources retenues à part. Elle
        /// ne relit jamais la valeur déjà posée : une version antérieure préfixait l'état existant,
        /// et comme le même objet de présence est réutilisé à chaque rafraîchissement, le texte
        /// s'empilait — « In a match · In a match · In a match ». Ne pas réintroduire de lecture
        /// arrière ici.
        /// </summary>
        private static void AppliquerNextendo(RichPresence presence)
        {
            NextendoDiscordPresence.EtatSalon salon = NextendoDiscordPresence.Courant;

            string etatSalon = "";

            if (salon is { EnSalon: true })
            {
                presence.Party = new Party
                {
                    ID = salon.Id != 0 ? $"nx-{salon.Id}" : "nx",
                    Size = salon.Joueurs,
                    Max = salon.Max > 0 ? salon.Max : salon.Joueurs,
                };

                etatSalon = salon.CodeEtat switch
                {
                    "searching" => "Looking for players",
                    "matched" => "In a match",
                    _ => "Online",
                };
            }
            else
            {
                presence.Party = null;
            }

            // Le circuit vient du système de fichiers, pas du rapport de jeu : il est connu dès
            // le chargement, alors que le rapport n'arrive qu'à l'arrivée. Un nom inconnu de la
            // table n'affiche rien plutôt qu'un nom interne brut.
            string circuit = NextendoMk8Courses.NomAffiche(NextendoCourseWatcher.Courant);

            // Ordre : le salon d'abord (ce qu'on apporte), puis le circuit, puis ce que le jeu
            // a dit de lui-même. Tout est facultatif ; s'il ne reste rien, le temps de jeu.
            string compose = string.Join(" · ",
                new[] { etatSalon, circuit, _etatJeu }.Where(m => !string.IsNullOrEmpty(m)));

            presence.State = TruncateToByteLength(compose.Length > 0 ? compose : _etatParDefaut);
        }

        private static void SwitchToPlayingState(ApplicationMetadata appMeta, ProcessResult procRes)
        {
            _discordClient?.SetPresence(_discordPresencePlaying ??= CreatePlayingState(appMeta, procRes));
            _currentApp = appMeta;
        }

        private static void SwitchToMainState()
        {
            _discordClient?.SetPresence(_discordPresenceMain);
            _discordPresencePlaying = null;
            _currentApp = null;
        }

        private static void HandlePlayReport(Horizon.Prepo.Types.PlayReport playReport)
        {
            if (_discordClient is null)
                return;
            if (!TitleIDs.CurrentApplication.Value.HasValue)
                return;
            if (_discordPresencePlaying is null)
                return;

            FormattedValue formattedValue =
                PlayReports.Analyzer.Format(TitleIDs.CurrentApplication.Value, _currentApp, playReport);

            if (!formattedValue.Handled)
                return;
            

            
            try // New format that attempts to deserialize json, and if it fails (using old method)...
            {
                Dictionary<string, string> outDictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(formattedValue.FormattedString);
                
                _discordPresencePlaying.Details = TruncateToByteLength(
                    outDictionary["Details"].IsNullOrEmpty()
                        ? $"Playing {_currentApp.Title}"
                        : outDictionary["Details"]
                );
            
                // [Nextendo] Le rapport ne pose plus State directement : il le confie à _etatJeu, et
                // c'est AppliquerNextendo qui recompose la ligne à partir des deux sources. Écrire
                // ici et préfixer ensuite était la cause de l'empilement observé sur Discord.
                _etatJeu = outDictionary["State"].IsNullOrEmpty() ? "" : outDictionary["State"];
            }
            catch // Utilize original code
            {
                _discordPresencePlaying.Details = TruncateToByteLength(
                    formattedValue.Reset
                        ? $"Playing {_currentApp.Title}"
                        : formattedValue.FormattedString
                );
            }

            _etatParDefaut = $"Total play time: {ValueFormatUtils.FormatTimeSpan(_currentApp.TimePlayed)}";
            AppliquerNextendo(_discordPresencePlaying);

            if (_discordClient.CurrentPresence.Details.Equals(_discordPresencePlaying.Details) && _discordClient.CurrentPresence.State.Equals(_discordPresencePlaying.State))
                return; //don't trigger an update if the set presence Details are identical to current

            _discordClient.SetPresence(_discordPresencePlaying);
            Logger.Info?.Print(LogClass.UI, "Updated Discord RPC based on a supported play report.");
        }

        public static string PrepareMultilineRpcString(string line1 = "", string line2 = "")
        {
            Dictionary<string, string> rpcdict = new() { { "Details", line1 }, {"State", line2} };
            return JsonSerializer.Serialize(rpcdict);
        }

        private static string TruncateToByteLength(string input)
        {
            if (Encoding.UTF8.GetByteCount(input) <= ApplicationByteLimit)
            {
                return input;
            }

            // Find the length to trim the string to guarantee we have space for the trailing ellipsis.
            int trimLimit = ApplicationByteLimit - Encoding.UTF8.GetByteCount(Ellipsis);

            // Make sure the string is long enough to perform the basic trim.
            // Amount of bytes != Length of the string
            if (input.Length > trimLimit)
            {
                // Basic trim to best case scenario of 1 byte characters.
                input = input[..trimLimit];
            }

            while (Encoding.UTF8.GetByteCount(input) > trimLimit)
            {
                // Remove one character from the end of the string at a time.
                input = input[..^1];
            }

            return input.TrimEnd() + Ellipsis;
        }

        public static void Exit()
        {
            _discordClient?.Dispose();
        }
    }
}
