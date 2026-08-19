using Ryujinx.Ava.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Systems
{
    /// <summary>
    /// [Nextendo] Ce que NOS serveurs savent d'une partie, et que le jeu ne dit pas.
    ///
    /// Le rapport de jeu (play report) décrit ce que le joueur fait DANS le jeu :
    /// « Racing », « VS Races », « Grand Prix ». Il ne dit pas s'il est en ligne — une
    /// course contre l'ordinateur et une course mondiale produisent exactement le même
    /// rapport. Seul le serveur sait qu'un joueur est assis dans un salon Nextendo, avec
    /// qui, et combien ils sont.
    ///
    /// C'est donc la seule source honnête pour écrire « en ligne » dans un statut. On ne
    /// le déduit pas de l'écran ni d'un délai : on le lit là où c'est vrai.
    /// </summary>
    internal static class NextendoDiscordPresence
    {
        /// <summary>L'instantané du salon. Null tant qu'on n'a rien lu.</summary>
        public sealed class EtatSalon
        {
            public bool EnSalon;
            public int Joueurs;
            public int Max;

            /// <summary>« searching », « matched », ou vide si le serveur de jeu n'a pas su classer.</summary>
            public string CodeEtat = "";

            /// <summary>Identifiant du salon : sert d'identifiant de groupe côté Discord.</summary>
            public ulong Id;
        }

        public static EtatSalon Courant { get; private set; }

        /// <summary>Levé quand l'état change RÉELLEMENT (entrée, sortie, arrivée d'un joueur).</summary>
        public static event Action Change;

        // 5 s, comme la fenêtre « Joueurs ». C'était 15 s au départ, en supposant que
        // personne ne regarde un statut Discord se rafraîchir — c'est faux : entre le moment
        // où une course démarre et celui où le statut le montre, quinze secondes se voient.
        // Le serveur met déjà sa réponse en cache 5 s, donc sonder à ce rythme ne lui coûte
        // rien de plus ; on colle simplement à sa fraîcheur réelle.
        private static readonly TimeSpan _intervalle = TimeSpan.FromSeconds(5);

        private static CancellationTokenSource _annulation;

        /// <summary>
        /// Démarre le sondage. Sans effet si le compte n'est pas lié : un invité n'a pas de
        /// salon côté serveur, l'interroger ne rendrait que des refus.
        /// </summary>
        public static void Start()
        {
            if (_annulation is not null)
            {
                return;
            }

            if (!NextendoAccount.IsLinked || NextendoAccount.IsGuest)
            {
                return;
            }

            _annulation = new CancellationTokenSource();
            _ = Boucle(_annulation.Token);
        }

        public static void Stop()
        {
            _annulation?.Cancel();
            _annulation?.Dispose();
            _annulation = null;

            // On oublie l'état : laisser « en ligne, 4 joueurs » affiché après la fermeture
            // du jeu serait une information fausse, pas une information périmée.
            if (Courant is not null)
            {
                Courant = null;
                Prevenir();
            }
        }

        private static async Task Boucle(CancellationToken jeton)
        {
            while (!jeton.IsCancellationRequested)
            {
                try
                {
                    NextendoApi.NextendoLobby salon = await NextendoApi.GetMyLobbyAsync();

                    EtatSalon nouveau = salon.InLobby
                        ? new EtatSalon
                        {
                            EnSalon = true,
                            Joueurs = salon.Count,
                            Max = salon.Max,
                            CodeEtat = salon.StateCode ?? "",
                            Id = salon.Id,
                        }
                        : null;

                    if (Different(Courant, nouveau))
                    {
                        Courant = nouveau;
                        Prevenir();
                    }
                }
                catch (Exception ex)
                {
                    // Un statut Discord est un agrément. Une panne réseau ne doit ni remonter
                    // ni arrêter la boucle : au pire le statut reste sur sa dernière valeur.
                    Logger.Debug?.Print(LogClass.Application, $"[Nextendo] presence poll: {ex.Message}");
                }

                try
                {
                    await Task.Delay(_intervalle, jeton);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        private static bool Different(EtatSalon a, EtatSalon b)
        {
            if (a is null || b is null)
            {
                return !ReferenceEquals(a, b);
            }

            return a.EnSalon != b.EnSalon
                || a.Joueurs != b.Joueurs
                || a.Max != b.Max
                || a.Id != b.Id
                || a.CodeEtat != b.CodeEtat;
        }

        private static void Prevenir()
        {
            try
            {
                Change?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Debug?.Print(LogClass.Application, $"[Nextendo] presence notify: {ex.Message}");
            }
        }
    }
}
