namespace Ryujinx.Ava.Systems
{
    /// <summary>
    /// [Nextendo] Les images du statut Discord pour les jeux qui tournent sur nos serveurs.
    ///
    /// Pourquoi une URL et non une clé d'image : une clé d'asset n'existe que dans l'application
    /// Discord qui l'héberge. Les 198 clés utilisées jusqu'ici appartiennent à l'application de
    /// l'équipe Ryujinx amont, et deux jeux qu'on sert n'y figurent tout simplement pas — Splatoon 3
    /// et Minecraft — d'où la cartouche générique à leur place.
    ///
    /// Discord documente une seconde forme pour ce champ : une URL https ordinaire, qu'il convertit
    /// lui-même en image de son proxy. Aucun téléversement, aucun jeton, aucun secret à embarquer
    /// dans un binaire distribué. C'est donc la forme retenue : la couverture ne dépend plus de ce
    /// que quelqu'un d'autre a bien voulu téléverser.
    ///
    /// Les URL restent courtes à dessein : la bibliothèque refuse au-delà de 256 caractères.
    /// </summary>
    internal static class NextendoIcones
    {
        private const string Base = "https://nextendo.network/assets/rp/";

        /// <summary>L'insigne du réseau, en petite image et en repli.</summary>
        public static string UrlReseau() => Base + "nextendo.png";

        /// <summary>
        /// Les titres que Nextendo sert. Cette liste double volontairement
        /// ApplicationData.NextendoCompatibleVersion : celle-ci vit dans un autre projet, et une
        /// dépendance dans ce sens ferait remonter l'interface dans la couche des systèmes.
        /// Toute entrée ajoutée là-bas doit l'être ici, sans quoi le jeu garde le nom « Ryujinx ».
        ///
        /// ⚠️ Distinct de <see cref="UrlJeu"/> : un jeu peut être compatible sans qu'on héberge
        /// encore son image. Confondre les deux ferait perdre le nom « Ryujinx | Nextendo » aux
        /// jeux dont l'icône manque, ce qui est l'inverse de ce qu'on veut.
        /// </summary>
        public static bool EstCompatible(string titleId)
        {
            if (string.IsNullOrEmpty(titleId))
            {
                return false;
            }

            return titleId.ToLowerInvariant() switch
            {
                "0100152000022000"                                              // Mario Kart 8 Deluxe
                    or "01006a800016e000"                                       // Super Smash Bros. Ultimate
                    or "0100f8f0000a2000" or "01003bc0000a0000" or "01003c700009c800" // Splatoon 2 (EU/US/JP)
                    or "01006f8002326000"                                       // Animal Crossing: New Horizons
                    or "0100dca0064a6000"                                       // Luigi's Mansion 3
                    or "0100c2500fc20000"                                       // Splatoon 3
                    or "01006bd001e06000"                                       // Minecraft
                    => true,
                _ => false,
            };
        }

        /// <summary>
        /// L'image carrée du jeu, ou une chaîne vide si on ne l'héberge pas encore — auquel cas
        /// l'appelant retombe sur <see cref="UrlReseau"/>.
        ///
        /// Ces images sont les icônes que le jeu porte lui-même dans sa NCA de contrôle, sorties
        /// du dump. Une entrée n'est ajoutée ici qu'une fois le fichier réellement servi : une URL
        /// qui répond 404 ne donne pas une image de repli, elle donne un carré vide.
        /// </summary>
        public static string UrlJeu(string titleId)
        {
            if (string.IsNullOrEmpty(titleId))
            {
                return "";
            }

            return titleId.ToLowerInvariant() switch
            {
                // Extraites du dump du joueur (NCA de contrôle) et servies par nous.
                "0100152000022000" => Base + "mk8dx.png",
                "0100c2500fc20000" => Base + "s3.png",

                // Les quatre suivantes viennent du CDN de Discord lui-même : l'application
                // amont les héberge déjà, et une URL externe accepte n'importe quel https.
                // On ne recopie donc rien — l'image reste chez Discord, on ne fait que la
                // désigner. C'est ce qui permet de couvrir ces jeux sans avoir leur dump.
                // Identifiants relevés le 19/08/2026 sur
                // https://discord.com/api/v9/oauth2/applications/1293250299716173864/assets
                // (endpoint public, sans jeton), et chacun vérifié en 200 image/png.
                "01006a800016e000" => Amont + "1294426132443304009.png", // Super Smash Bros. Ultimate
                "0100f8f0000a2000" or "01003bc0000a0000" or "01003c700009c800"
                    => Amont + "1303889283886743672.png",               // Splatoon 2 (EU/US/JP)
                "01006f8002326000" => Amont + "1504264568413880352.png", // Animal Crossing: New Horizons
                "0100dca0064a6000" => Amont + "1294777186720677953.png", // Luigi's Mansion 3

                // Minecraft : absent du CDN amont ET sans dump ici. Il retombe donc sur la
                // marque Nextendo, ce qui est préférable à une URL qui répondrait 404 —
                // Discord n'affiche alors aucune image du tout.
                _ => "",
            };
        }

        /// <summary>
        /// Le CDN de l'application Ryujinx amont. Ces images ne nous appartiennent pas et ne
        /// sont pas recopiées : on ne fait que les désigner par leur URL publique.
        /// </summary>
        private const string Amont = "https://cdn.discordapp.com/app-assets/1293250299716173864/";
    }
}
